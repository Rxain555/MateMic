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

    /// <summary>上次输出诊断日志的时间戳（Stopwatch ticks）。见 Read() 里的时间门控。</summary>
    private long _lastDiagTicks;

    /// <summary>输出诊断日志的最小间隔（秒）。曾经用 _blocks 计数门控，推理持续失败时会退化成每次调用都打。</summary>
    private const double DiagIntervalSeconds = 2.0;
    private double _lastInferMs;

    /// <summary>输出环积压（毫秒），audio 线程写、UI 线程读。</summary>
    private int _lastBacklogMs;

    /// <summary>最近一次引擎创建失败的原因（成功时为 null）。供界面提示用户。</summary>
    private volatile string? _lastError;

    /// <summary>
    /// 引擎创建失败的原因。像"这是 RVC v1 模型"这类信息只写日志用户看不到，
    /// 结果就是"选了音色却没声音"（2026-10-08 用户实测）。
    /// </summary>
    public string? LastError => _lastError;

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

    /// <summary>
    /// 输出环当前积压（毫秒）—— 这是**真实存在的额外延迟**（产出快过消费时的排队量），
    /// 稳态下不应长期偏大（上限约 2×块长）。界面把它计入总延迟。
    /// </summary>
    public int OutputBacklogMs => Volatile.Read(ref _lastBacklogMs);

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

            // ⚠ 后台加载期间直接返回。
            // 引擎加载约 1.5~2 秒，这期间 _engine 还是 null；若不拦住，
            // 全项目十几个 UpdateAllParameters 调用点会各自发起一次 Restart，
            // 实测在自检里触发了 8 次重建、8 次重读 305MB 索引。
            if (_starting) return;

            // 变调与索引占比都只影响每块的计算，不动缓冲几何 ⇒ 热更新即刻生效。
            // （索引占比漏掉热更新会让滑条完全空转——用户实测"拉到最大和最小没多大区别"。）
            if (_engine != null)
            {
                _engine.Semitones = (int)Math.Round(_config.AiVoice.Semitones);
                _engine.IndexRate = _config.AiVoice.IndexFile is { Length: > 0 }
                    ? Math.Clamp(_config.AiVoice.IndexRate / 100f, 0f, 1f)
                    : 0f;
            }

            var signature = string.Join('|',
                _config.AiVoice.BlockMs,
                _config.AiVoice.ContextMs,
                _config.AiVoice.CrossfadeMs,
                _config.AiVoice.VoiceModel ?? string.Empty);

            // ⚠ 指纹比较**不能**再加 "_engine != null" 这个前提：
            // 否则引擎未就绪时每次调用都会重新 Restart（见上面的说明）。
            if (signature == _engineSignature) return;

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
        // 记录触发重建的参数指纹，便于定位"启动阶段被反复重建"
        //（启动时每次重建都要重读上下文与索引，代价不小）。
        Log.Info($"[AI 变声] 重建引擎：{_engineSignature}");
        StopWorker();
        lock (_ringGate) { Allocate(); }     // 只锁住"换缓冲"这一瞬，音频线程最多等微秒
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
                _lastError = null;
                _loadProgress = 100;
                ProgressChanged?.Invoke(this, 100);
            }
            catch (Exception ex)
            {
                Log.Error("[AI 变声] 引擎创建失败，本模块将不参与处理", ex);
                _engine = null;
                // 把原因留给界面显示：像"这是 RVC v1 模型"这类信息很有价值，
                // 只写进日志的话用户只会觉得"选了音色却没声音"（2026-10-08）。
                _lastError = (ex.InnerException ?? ex).Message;
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

        // 索引（可选）：**直接打开用户放在 index\ 下的 .index 文件**。
        // 读取走 C++ 桥接层调 faiss（见 FaissIndex），因此不需要任何预处理、
        // 也不需要 cache\ 里的中间产物，用户端更不需要 Python。
        FaissIndex? index = null;
        var rate = 0f;
        if (!string.IsNullOrWhiteSpace(_config.AiVoice.IndexFile))
        {
            var indexPath = Path.Combine(ConfigStore.AiIndexDirectory, _config.AiVoice.IndexFile);
            index = FaissIndex.Open(indexPath);
            if (index != null)
            {
                index.SetNprobe(1);          // faiss 默认值，官方也未改动
                rate = _config.AiVoice.IndexRate / 100f;
            }
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
            index: index,
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

        // ⚠ 必须检查 Join 的结果：超时说明工作线程还在跑（可能正卡在一次推理里），
        // 这时若继续 Dispose 引擎并置 null，旧线程会踩到已释放的对象；
        // 而它还持有输出环的写入位置，与新线程并发写就会让音频"一颤一颤"
        //（2026-10-08 用户实测：重启软件后抖动就不明显了 —— 正是这类状态残留的特征）。
        var stopped = true;
        try { stopped = _worker?.Join(3000) ?? true; } catch { /* 忽略 */ }
        if (!stopped)
            Log.Warn("[AI 变声] 旧工作线程未能在 3 秒内退出，将等待其自然结束再释放引擎");

        _worker = null;
        _wake.Reset();
        try { _engine?.Dispose(); } catch { /* 忽略 */ }
        _engine = null;
        _toModel = null;
        _fromModel = null;

        // 关键：清掉"正在加载"标记。否则上一次后台加载还没结束时，
        // StartWorker 会因为 _starting == true 直接 return，引擎永远建不起来。
        _starting = false;
    }

    // ---------------------------------------------------------------- 音频线程

    /// <summary>
    /// 音频线程调用，**必须立刻返回**：把输入塞进输入环，再从输出环取同样长度返回。
    /// 输出环空了就补静音并记一次欠载（正常运行时不该发生）。
    /// </summary>
    /// <summary>
    /// 保护"缓冲交换"的锁：<see cref="Allocate"/>（界面线程，改参数时）与
    /// <see cref="Read"/>（音频线程）必须互斥。
    ///
    /// 不加锁的后果（2026-10-08 用户实测"一颤一颤"）：Allocate 会把读写下标归零、
    /// 把数组换成新的，而音频线程此刻可能正按旧下标读写 —— 数据立刻错位。
    /// 表现就是"改过参数之后开始抖，重启软件（不经 Allocate）就不抖了"。
    ///
    /// 锁内**只有内存操作**（微秒级），1.5 秒的模型加载在锁外，不会阻塞音频线程。
    /// </summary>
    private readonly object _ringGate = new();

    public int Read(Span<float> buffer)
    {
        if (buffer.Length == 0) return 0;

        lock (_ringGate)
        {
            return ReadCore(buffer);
        }
    }

    private int ReadCore(Span<float> buffer)
    {
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

        // 记下当前积压供界面显示。这是**真实存在的额外延迟**（产出快过消费时的排队量），
        // 稳态下不应长期偏大（上限 2×块长）。2026-10-08 加入"延迟与性能"卡片。
        Volatile.Write(ref _lastBacklogMs, available * 1000 / ChainRate);

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
        //
        // ⚠ 门控必须**基于时间**，不能基于 _blocks 的计数：
        // _blocks 只在"推理成功"之后自增。一旦推理持续失败（模型版本不对、引擎没起来等），
        // _blocks 会**冻结**；若它恰好冻结在 64 的倍数上，`(_blocks & 0x3F) == 0` 就**恒为真**，
        // 于是这一行会在实时线程上**每次 Read() 都写一条日志**。
        // 实测后果：日志 8 MB/天、每 10 ms 一条（约 100 行/秒），
        // 实时线程每次都要字符串插值 + 加锁入队 + 置内核事件，后台还反复写盘。
        //（2026-10-08 从用户日志 app_20261008.log 里量出来的。）
        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (nowTicks - _lastDiagTicks >= System.Diagnostics.Stopwatch.Frequency * DiagIntervalSeconds)
        {
            _lastDiagTicks = nowTicks;
            var inAvail = Available(_inRing, _inRead, _inWrite);
            Log.Info($"[AI 变声] 诊断：输出环积压 {available * 1000 / ChainRate}ms"
                     + $"（额外延迟，上限 {_blockSamples48 * 2 * 1000 / ChainRate}ms）"
                     + $"，输入环 {inAvail * 1000 / ChainRate}ms"
                     + $"，欠载 {LateBlocks}，累计丢弃 {Interlocked.Read(ref _droppedSamples) * 1000 / ChainRate}ms"
                     + $"，上次推理 {LastInferMs:F0}ms");
        }

        // 刻意**不做输出噪声门**（2026-10-08 用户要求去掉）：
        // 它只在静音段关门，说话时照样开门、底噪会混进人声，治不了根本问题，
        // 反而在阈值附近造成生硬的门感。静音段由上面的"不推理 + 淡出"处理即可。

        return buffer.Length;
    }


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

    /// <summary>
    /// 静音判定阈值（带迟滞）：
    /// 进入静音用 −55 dBFS（远低于正常说话，约 −20~−35 dBFS，不误伤气声与轻声），
    /// 退出静音用 −48 dBFS，中间 7dB 是死区，避免说话间隙在阈值附近反复切换。
    private void ProcessOneBlock()
    {
        // 取一个块（40k 域）
        for (var i = 0; i < _blockSamples48; i++)
        {
            _inScratch[i] = _inRing[_inRead];
            _inRead = (_inRead + 1) % _inRing.Length;
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
