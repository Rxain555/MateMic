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
    private readonly ManualResetEventSlim _wake = new(false);

    private int _blockSamples48;           // 一个音频块在 48k 下的样本数
    private float[] _inScratch = Array.Empty<float>();
    private float[] _outScratch = Array.Empty<float>();

    // 统计（供界面显示；全部用 Interlocked，避免音频线程与 UI 线程竞争）
    private long _blocks, _lateBlocks;
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

    /// <summary>把界面参数写回引擎。参数变化需要重启工作线程（块长变了，缓冲尺寸也要变）。</summary>
    public void UpdateParameters()
    {
        lock (_gate)
        {
            if (!Enabled || !Ready(out _)) return;
            // 块长/上下文/交叉淡化变化都会改变缓冲几何，最省事也最可靠的做法是重建
            if (_engine != null && _blockSamples48 == ExpectedBlockSamples()) return;
            Restart();
        }
    }

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

    private void StartWorker()
    {
        if (_engine != null) return;
        if (!Ready(out var reason))
        {
            Log.Warn($"[AI 变声] 暂不可用：{reason}");
            return;
        }

        try
        {
            var engineDir = ConfigStore.AiEngineDirectory;
            _engine = new StreamingRvc(
                Path.Combine(engineDir, "contentvec.onnx"),
                Path.Combine(engineDir, "rmvpe.onnx"),
                Path.Combine(ConfigStore.AiVoicesDirectory, _config.AiVoice.VoiceModel!),
                _config.AiVoice.BlockMs,
                _config.AiVoice.ContextMs,
                _config.AiVoice.CrossfadeMs,
                (int)Math.Round(_config.AiVoice.Semitones),
                useGpu: true);

            _toModel = new RateConverter(ChainRate, ModelRate, _blockSamples48 + 64);
            _fromModel = new RateConverter(ModelRate, ChainRate, _blockSamples48 * ModelRate / ChainRate + 64);
        }
        catch (Exception ex)
        {
            Log.Error("[AI 变声] 引擎创建失败，本模块将不参与处理", ex);
            _engine = null;
            return;
        }

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

        // AI 变声自己的输出噪声门（静音处 RVC 仍有微弱输出，实测原始素材 6.3% 静音帧、
        // 转换后变成 0.0%）。放在这里而不是复用链上的噪声门，是因为它只该作用于本模块的产物。
        ApplyOutputGate(buffer);

        return buffer.Length;
    }

    private float _gateEnv;

    private void ApplyOutputGate(Span<float> buffer)
    {
        var threshold = _config.AiVoice.GateDb;
        if (threshold >= 0) return;

        var linear = (float)Math.Pow(10, threshold / 20.0);
        foreach (ref var sample in buffer)
        {
            var level = Math.Abs(sample);
            // 一阶包络：起音快、释放慢，避免削掉字头也避免咔哒
            var coeff = level > _gateEnv ? 0.35f : 0.002f;
            _gateEnv += (level - _gateEnv) * coeff;
            if (_gateEnv < linear && level < linear) sample = 0;
        }
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
