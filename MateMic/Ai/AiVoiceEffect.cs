using NAudio.Wave;
using MateMic.Core;
using MateMic.Denoise;
using MateMic.Dsp;

namespace MateMic.Ai;

/// <summary>
/// AI 变声（RVC）在处理链中的适配层。位置：**AI 降噪之后、DSP 变声之前**。
///
/// <b>为什么不能直接在 Read 里推理</b><br/>
/// 处理链的 <see cref="IAudioEffect.Read"/> 是**同步就地处理**（上游把数据喂进来、
/// 处理完原地写回），而一次 RVC 推理要几十毫秒、音频回调只有 5ms 一次。
/// 在 Read 里推理会直接卡死音频线程。所以拆成两半：
/// <code>
///   Read（音频线程，必须即时返回）   输入块 → 输入环；输出环 → 返回给上游
///   Worker（后台线程，慢速异步）     输入环攒够一个块 → 推理 → 输出环
/// </code>
/// 输出环在启动时**预填充静音**，这段静音就是算法延迟的来源
/// （官方口径：延迟 = 2 × block —— 一个 block 用来攒输入，一个 block 用来等推理结果）。
///
/// <b>采样率</b>：处理链是 48 kHz 单声道 float，而 RVC 需要
/// ① 16 kHz 喂内容编码器与音高提取，② 40 kHz 是音色模型的输出采样率。
/// 所以：48k→40k 送进 <see cref="StreamingRvc"/>（它内部再重采样到 16k），
/// 合成出的 40k 再转回 48k。
/// </summary>
public sealed class AiVoiceEffect : IAudioEffect
{
    private const int ChainRate = 48000;   // 处理链采样率
    private const int ModelRate = 40000;   // 音色模型的输出采样率

    private readonly AppConfig _config;
    private readonly object _gate = new();

    // 环形缓冲（均为处理链采样率 48k 的单声道 float）
    private float[] _inRing = Array.Empty<float>();
    private float[] _outRing = Array.Empty<float>();
    private int _inWrite, _inRead, _outWrite, _outRead;

    private StreamingRvc? _engine;
    private RateConverter? _toModel;       // 48k -> 40k
    private RateConverter? _fromModel;     // 40k -> 48k
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _starting;
    private readonly ManualResetEventSlim _wake = new(false);

    /// <summary>引擎正在后台加载。界面据此显示加载动画。</summary>
    public bool IsLoading => _starting;

    /// <summary>加载进度 0~100。</summary>
    public int LoadProgress => _loadProgress;

    private volatile int _loadProgress;

    /// <summary>加载状态变化（true=开始加载，false=结束）。在后台线程触发。</summary>
    public event EventHandler<bool>? LoadingChanged;

    /// <summary>加载进度变化（0~100）。在后台线程触发。</summary>
    public event EventHandler<int>? ProgressChanged;

    private int _blockSamples48;           // 一个音频块在 48k 下的样本数
    private float[] _inScratch = Array.Empty<float>();
    private float[] _outScratch = Array.Empty<float>();

    // 统计（供界面显示；全部用 Interlocked，避免音频线程与 UI 线程竞争）
    private long _blocks, _lateBlocks, _droppedSamples;
    private double _lastInferMs;

    public AiVoiceEffect(AppConfig config, WaveFormat format)
    {
        _config = config;
        WaveFormat = format;
        Allocate();
    }

    public string Name => "AI 变声";
    public bool Enabled { get; set; }
    public WaveFormat WaveFormat { get; }

    /// <summary>累计处理块数。</summary>
    public long Blocks => Interlocked.Read(ref _blocks);

    /// <summary>欠载次数：输出环空了、只能补静音的块数。越小越好。</summary>
    public long LateBlocks => Interlocked.Read(ref _lateBlocks);

    /// <summary>最近一次推理耗时（ms）。</summary>
    public double LastInferMs => _lastInferMs;

    /// <summary>当前配置下的算法延迟（ms），= 2 × block，官方口径。</summary>
    public int LatencyMs => 2 * _config.AiVoice.BlockMs;

    // ---------------------------------------------------------------- 参数

    /// <summary>
    /// 把界面参数写回引擎。
    ///
    /// 分两类处理：
    ///   · **变调** 只影响每块的 f0 变换，直接热更新即刻生效；
    ///   · **块长 / 上下文 / 交叉淡化 / 音色** 会改变缓冲几何或模型，必须重建引擎（约 1.5 秒）。
    ///
    /// ⚠ 前一版只比较了"块长"，导致改变调、声线、上下文都被直接 return 掉——
    /// 用户实测反馈"声调相关设置好像没功能"。
    /// </summary>
    public void UpdateParameters()
    {
        lock (_gate)
        {
            if (!Enabled || !Ready(out _)) return;

            // 变调：热更新，不重建
            if (_engine != null) _engine.Semitones = (int)Math.Round(_config.AiVoice.Semitones);

            var signature = string.Join('|',
                _config.AiVoice.BlockMs,
                _config.AiVoice.ContextMs,
                _config.AiVoice.CrossfadeMs,
                _config.AiVoice.VoiceModel ?? string.Empty);

            if (_engine != null && signature == _engineSignature) return;

            _engineSignature = signature;
            Restart();
        }
    }

    /// <summary>重建引擎所依据的参数指纹。</summary>
    private string _engineSignature = string.Empty;

    private int ExpectedBlockSamples()
        => Math.Max(1, _config.AiVoice.BlockMs * ChainRate / 1000);

    /// <summary>组件与音色是否齐备（缺任何一项都不启用，避免半路崩溃）。</summary>
    public bool Ready(out string reason)
    {
        var status = AiComponent.Inspect();
        if (status.State != AiComponentState.Ready)
        {
            reason = status.Message;
            return false;
        }
        if (string.IsNullOrWhiteSpace(_config.AiVoice.VoiceModel))
        {
            reason = "还没有选择音色";
            return false;
        }
        var voice = Path.Combine(ConfigStore.AiVoicesDirectory, _config.AiVoice.VoiceModel);
        if (!File.Exists(voice))
        {
            reason = $"音色文件不存在：{_config.AiVoice.VoiceModel}";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    // ---------------------------------------------------------------- 生命周期

    private void Allocate()
    {
        // 输入环留 2 秒：下游偶尔卡一下也不至于丢数据
        var inCap = ChainRate * 2;
        // 输出环留足"预填充静音 + 若干块"，启动时全部为零 = 初始延迟
        var outCap = ChainRate * 2;
        _inRing = new float[inCap];
        _outRing = new float[outCap];
        Array.Clear(_outRing);

        // ⚠ 必须把所有读写索引一起归零。
        // 曾经只设了 _outWrite、漏了 _outRead：重建缓冲后 _outRead 还指着**旧数组**里的位置，
        // 于是 available 算出一个虚假的巨大积压（实测报 839ms），Read 也从错乱的位置取数据。
        // 表现就是"额外延迟一秒多"，而且每次参数变化（触发 Restart）都会再犯一次。
        _inWrite = _inRead = 0;
        _outWrite = _outRead = 0;

        _blockSamples48 = ExpectedBlockSamples();
        _inScratch = new float[_blockSamples48];
        _outScratch = new float[_blockSamples48];

        // 预填充一个块的静音作为"启动延迟"，此后 worker 一产出就能接上
        _outWrite = _blockSamples48;
    }

    private void Restart()
    {
        StopWorker();
        Allocate();
        StartWorker();
    }

    /// <summary>
    /// 启动推理线程。
    ///
    /// **耗时的模型加载（约 1.5 秒 + CUDA 初始化）必须放到后台线程**：
    /// 这个方法是从界面线程（开关的 Checked 事件）调过来的，同期加载三个 ONNX 会话
    /// 会把界面完全卡死（用户实测："打开 AI 变声按钮时，在加载时间内软件是卡死的"）。
    /// 加载期间 <see cref="Read"/> 照常把输入排队、输出补静音，音频线程不受影响。
    /// </summary>
    private void StartWorker()
    {
        if (_engine != null || _starting) return;
        if (!Ready(out var reason))
        {
            Log.Warn($"[AI 变声] 暂不可用：{reason}");
            return;
        }

        _starting = true;
        _loadProgress = 0;
        LoadingChanged?.Invoke(this, true);
        _ = Task.Run(() =>
        {
            try
            {
                CreateEngineCore();
                _loadProgress = 100;
                ProgressChanged?.Invoke(this, 100);
            }
            catch (Exception ex)
            {
                Log.Error("[AI 变声] 引擎创建失败，本模块将不参与处理", ex);
                _engine = null;
            }
            finally
            {
                _starting = false;
                LoadingChanged?.Invoke(this, false);
            }
        });
    }

    /// <summary>在后台线程里真正创建引擎与工作线程。</summary>
    private void CreateEngineCore()
    {
        var engineDir = ConfigStore.AiEngineDirectory;

        // 索引：与音色同名的 .simple 目录（由 Python 侧一次性转换而来，见 RvcIndex 注释）
        string? indexDir = null;
        var rate = 0f;
        if (!string.IsNullOrWhiteSpace(_config.AiVoice.IndexFile))
        {
            var name = Path.GetFileNameWithoutExtension(_config.AiVoice.IndexFile);
            var dir = Path.Combine(ConfigStore.AiIndexDirectory, name + ".simple");
            if (Directory.Exists(dir)) { indexDir = dir; rate = _config.AiVoice.IndexRate / 100f; }
            else Log.Warn($"[AI 变声] 索引 {name} 尚未转换（缺 {name}.simple 目录），本次不使用索引");
        }

        _engine = new StreamingRvc(
            Path.Combine(engineDir, "contentvec.onnx"),
            Path.Combine(engineDir, "rmvpe.onnx"),
            Path.Combine(ConfigStore.AiVoicesDirectory, _config.AiVoice.VoiceModel!),
            _config.AiVoice.BlockMs,
            _config.AiVoice.ContextMs,
            _config.AiVoice.CrossfadeMs,
            (int)Math.Round(_config.AiVoice.Semitones),
            useGpu: true,
            indexSimpleDir: indexDir,
            indexRate: rate,
            onProgress: p =>
            {
                _loadProgress = p;
                ProgressChanged?.Invoke(this, p);
            });

        _toModel = new RateConverter(ChainRate, ModelRate, _blockSamples48 + 64);
        _fromModel = new RateConverter(ModelRate, ChainRate, _blockSamples48 * ModelRate / ChainRate + 64);

        _running = true;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "MateMic.AiVoice",
            // 略低于音频线程，避免推理抢占采集/输出
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
        Log.Info($"[AI 变声] 引擎已启动：块 {_config.AiVoice.BlockMs}ms / 上下文 {_config.AiVoice.ContextMs}ms"
                 + $" / 算法延迟 {LatencyMs}ms，音色 {_config.AiVoice.VoiceModel}");
    }

    private void StopWorker()
    {
        _running = false;
        _wake.Set();
        try { _worker?.Join(1500); } catch { /* 忽略 */ }
        _worker = null;
        _wake.Reset();
        try { _engine?.Dispose(); } catch { /* 忽略 */ }
        _engine = null;
        _toModel = null;
        _fromModel = null;
    }

    // ---------------------------------------------------------------- 音频线程

    /// <summary>
    /// 音频线程调用，**必须立刻返回**：把输入塞进输入环，再从输出环取同样长度返回。
    /// 输出环空了就补静音并记一次欠载（正常运行时不该发生）。
    /// </summary>
    public int Read(Span<float> buffer)
    {
        if (buffer.Length == 0) return 0;

        // 输入环：只有积压到接近容量上限（这种情况只可能是引擎创建期间的追赶）才丢最老的。
        // 平时**绝不能丢**——丢输入会让音频出现断裂，听感是"呲呲/咔哒"。
        var inAvailNow = Available(_inRing, _inRead, _inWrite);
        var inCapSamples = ChainRate;                 // 1 秒
        if (inAvailNow > inCapSamples)
        {
            var dropIn = inAvailNow - inCapSamples;
            _inRead = (_inRead + dropIn) % _inRing.Length;
            Interlocked.Add(ref _droppedSamples, dropIn);
        }

        // 输入环可能装不下（下游阻塞太久）：放不下就丢弃最老的，保证实时性优先
        for (var i = 0; i < buffer.Length; i++)
        {
            var next = (_inWrite + 1) % _inRing.Length;
            if (next == _inRead)
            {
                _inRead = (_inRead + 1) % _inRing.Length;
                Interlocked.Increment(ref _lateBlocks);
            }
            _inRing[_inWrite] = buffer[i];
            _inWrite = next;
        }

        // 通知 worker 有新数据
        if ((_inWrite - _inRead + _inRing.Length) % _inRing.Length >= _blockSamples48)
            _wake.Set();

        // 输出：有就取，没有补静音
        var available = (_outWrite - _outRead + _outRing.Length) % _outRing.Length;

        // ⚠ 限流：积压超过目标就把**最老的**丢掉，把延迟拉回来。
        //
        // 为什么必须做：引擎创建要 1.5 秒左右，这期间 Read 一直在往输入环塞数据
        // （环容量 2 秒），worker 一起来就连续追赶、一次性把输出环灌满；而积压**不会自然消失**
        // （稳态下产出≈消费），于是变成永久延迟——实测曾达 839ms，用户听到的是"延迟一秒多"。
        // 实时语音里宁可丢一点音频，也不能让延迟无界增长。
        // 输出环稳态需要容纳"一次推理的产出"，所以上限取 2 个块：
        // 1 个块是 worker 一次产出的量，另 1 个块是给消费端留的余量。
        // 取更大只会徒增延迟——实测上限 3 个块时稳态积压达 380ms，用户听到的总延迟约 590ms。
        var capSamples = _blockSamples48 * 2;
        if (available > capSamples)
        {
            var drop = available - capSamples;
            _outRead = (_outRead + drop) % _outRing.Length;
            available = capSamples;
            Interlocked.Add(ref _droppedSamples, drop);
        }

        var take = Math.Min(buffer.Length, available);
        for (var i = 0; i < take; i++)
        {
            buffer[i] = _outRing[_outRead];
            _outRead = (_outRead + 1) % _outRing.Length;
        }
        if (take < buffer.Length)
        {
            buffer[take..].Clear();
            Interlocked.Increment(ref _lateBlocks);
        }

        // 诊断：积压量就是额外延迟。每隔一段时间记一次，用于判断"延迟到底是算法本身
        // 还是缓冲在持续增长"（缓冲只增不减 = 产出快过消费 = 延迟会一直涨）。
        if ((Interlocked.Read(ref _blocks) & 0x3F) == 0)
        {
            var inAvail = Available(_inRing, _inRead, _inWrite);
            Log.Info($"[AI 变声] 诊断：输出环积压 {available * 1000 / ChainRate}ms"
                     + $"（额外延迟，上限 {_blockSamples48 * 2 * 1000 / ChainRate}ms）"
                     + $"，输入环 {inAvail * 1000 / ChainRate}ms"
                     + $"，欠载 {LateBlocks}，累计丢弃 {Interlocked.Read(ref _droppedSamples) * 1000 / ChainRate}ms"
                     + $"，上次推理 {LastInferMs:F0}ms");
        }

        // 输出噪声门：**只作为兜底**。真正压掉静音底噪的是上面"静音不推理"那条路径，
        // 门的作用是处理"输入有微弱电平、但合成结果仍很轻"的边缘情况。
        ApplyOutputGate(buffer);

        return buffer.Length;
    }

    private float _gateEnv;
    private float _gateGain = 1f;

    /// <summary>
    /// 输出端**平滑**噪声门。
    ///
    /// 为什么要平滑而不是"低于阈值就置 0"：后者会造成**整段被切断**——语音的字头字尾
    /// 一旦落在判定为静音的区间里就被削掉，听感非常生硬（用户实测反馈"前面也切断后面也切断"）。
    /// 这里改成用**短时包络**驱动一个平滑增益：起音快（立刻打开，不切字头）、
    /// 释放慢（字尾自然衰减，不切字尾），中间是连续过渡。
    ///
    /// 阈值：合成器对静音本身只输出约 −61.7 dBFS（离线实测），而正常说话在 −20~−35 dBFS，
    /// 所以默认 −35 dB 落在两者之间，既能压掉静音噪声，又不会碰到正常语音。
    /// </summary>
    private void ApplyOutputGate(Span<float> buffer)
    {
        // ⚠ 用下限夹紧：配置里可能残留早期偏松的值（曾默认 −45，压不住 −43 的残留噪声）。
        // 只改代码默认值不够——已存在的 config.json 会一直带着旧值，必须在这里兜底。
        var threshold = Math.Max(_config.AiVoice.GateDb, MinGateDb);
        if (_config.AiVoice.GateDb >= 0) return;          // 0 表示用户主动关闭噪声门

        var linear = (float)Math.Pow(10, threshold / 20.0);

        // 每样本时间常数（48kHz）：包络 2ms 跟随；增益起音 2ms、释放 250ms
        // （释放取长一点，让字尾自然衰减，而不是被"关门"切掉）
        const float envCoeff = 1f / (0.002f * ChainRate);
        var attack = 1f - MathF.Exp(-1f / (0.002f * ChainRate));
        var release = 1f - MathF.Exp(-1f / (0.250f * ChainRate));

        foreach (ref var sample in buffer)
        {
            _gateEnv += (Math.Abs(sample) - _gateEnv) * envCoeff;

            var target = _gateEnv > linear ? 1f : 0f;
            var coeff = target > _gateGain ? attack : release;
            _gateGain += (target - _gateGain) * coeff;

            sample *= _gateGain;
        }
    }

    /// <summary>噪声门阈值下限（dBFS）。低于它的配置值会被夹到这里。</summary>
    private const float MinGateDb = -30f;

    // ---------------------------------------------------------------- 推理线程

    private void WorkerLoop()
    {
        while (_running)
        {
            _wake.Wait(20);
            _wake.Reset();
            if (!_running) break;

            while (_running && Available(_inRing, _inRead, _inWrite) >= _blockSamples48)
            {
                try
                {
                    ProcessOneBlock();
                }
                catch (Exception ex)
                {
                    Log.Error("[AI 变声] 推理失败，跳过该块", ex);
                    SkipOneBlock();
                }
            }
        }
    }

    private static int Available(float[] ring, int read, int write)
        => (write - read + ring.Length) % ring.Length;

    /// <summary>静音判定阈值：−55 dBFS。远低于正常说话（约 −20~−35 dBFS）。</summary>
    private const float SilenceRms = 0.00178f;

    /// <summary>淡出长度（样本）：10ms @ 48kHz。</summary>
    private const int FadeSamples = 480;

    /// <summary>上一块输出的尾部，用于静音段的淡出。</summary>
    private readonly float[] _tail = new float[FadeSamples];

    /// <summary>
    /// 输出"淡出 + 静音"。
    /// 先让上一块的最后 10ms 平滑衰减到 0，再补静音——这样块边界处没有硬切，
    /// 听感是"话音自然收尾"而不是"被剪断"。
    /// </summary>
    private void WriteSilenceWithFade()
    {
        for (var i = 0; i < _blockSamples48; i++)
        {
            float v;
            if (i < FadeSamples)
            {
                var k = 1f - (float)i / FadeSamples;      // 线性淡出，块内 10ms，足够听不出台阶
                v = _tail[i] * k * k;                     // 平方让尾部更快收敛
            }
            else
            {
                v = 0f;
            }
            _outRing[_outWrite] = v;
            _outWrite = (_outWrite + 1) % _outRing.Length;
        }
        Array.Clear(_tail);
        Interlocked.Increment(ref _blocks);
    }

    private void ProcessOneBlock()
    {
        // 取一个块（40k 域）
        for (var i = 0; i < _blockSamples48; i++)
        {
            _inScratch[i] = _inRing[_inRead];
            _inRead = (_inRead + 1) % _inRing.Length;
        }

        // ------------------------------------------------------------------
        // 输入几乎无声时**不推理**，直接输出一段"上一块尾部的淡出 + 静音"。
        //
        // 依据（离线实测，见工作日志）：
        //   · 合成器对静音/极低电平输入仍会输出约 −50~−61 dBFS 的内容；
        //   · 而**把无声帧的特征置 0 反而更糟**（−61 → −41 dBFS），模型不认识零特征会乱编；
        //   · 所以"静音处不推理"才是唯一能得到**绝对静音**的做法。
        //
        // 上一版之所以被用户评价"前面也切断后面也切断、很生硬"，是因为整块硬切。
        // 这里改为：用上一块尾部做 **10ms 指数淡出**再进静音，边界就听不出来了。
        // ------------------------------------------------------------------
        var sum = 0f;
        foreach (var s in _inScratch) sum += s * s;
        if (MathF.Sqrt(sum / _inScratch.Length) < SilenceRms)
        {
            WriteSilenceWithFade();
            return;
        }

        var modelBlock = _blockSamples48 * ModelRate / ChainRate;
        var block40 = new float[modelBlock];
        var produced = _toModel!.Process(_inScratch, block40);
        if (produced < modelBlock) Array.Resize(ref block40, produced);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var out40 = _engine!.Process(block40);
        sw.Stop();
        _lastInferMs = sw.Elapsed.TotalMilliseconds;
        Interlocked.Increment(ref _blocks);

        // 回到 48k 并写进输出环
        var out48 = new float[out40.Length * ChainRate / ModelRate + 64];
        var n = _fromModel!.Process(out40, out48);
        for (var i = 0; i < n; i++)
        {
            _outRing[_outWrite] = out48[i];
            _outWrite = (_outWrite + 1) % _outRing.Length;
        }
    }

    private void SkipOneBlock()
    {
        for (var i = 0; i < _blockSamples48; i++)
            _inRead = (_inRead + 1) % _inRing.Length;
        // 输出补一段静音，保持时间轴对齐
        for (var i = 0; i < _blockSamples48; i++)
        {
            _outRing[_outWrite] = 0f;
            _outWrite = (_outWrite + 1) % _outRing.Length;
        }
    }

    // ---------------------------------------------------------------- 启停

    /// <summary>由链重建时调用：启用则起线程，禁用则停。</summary>
    public void Sync()
    {
        if (Enabled) { if (_worker == null) StartWorker(); }
        else StopWorker();
    }

    public void Dispose()
    {
        StopWorker();
        _wake.Dispose();
    }
}
