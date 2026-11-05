using NAudio.Wave;
using MateMic.Ai.AiVoiceIpc;
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

    /// <summary>
    /// 「额外缓冲」的**下限**（ms）。
    ///
    /// 用户 2026-10-09 明确要求把可调下限放到 **0**：
    /// "当然这是不可能稳定输出的，我只是想把选择权交到用户手里"。所以这里不再兜底。
    ///
    /// 调到 0 会怎样：水位最低点 ≈ `0 − 一次推理耗时` ⇒ **每批产出之间必然欠载**
    /// （补静音 ⇒ 咔哒 / 断续）。滑条的 0ms 是"能调，但基本不能听"的一档。
    /// 另一侧仍有保护：积压上限 = 块长 + max(本值, 实测推理耗时 + 40ms)。
    /// </summary>
    private const int MinBacklogPadMs = 0;

    /// <summary>预填充之外、给推理耗时抖动留的额外余量（ms），用于算积压上限。</summary>
    private const int BacklogJitterMarginMs = 40;

    /// <summary>
    /// 当前配置下的「额外缓冲」余量（ms）—— 界面上那个滑条，用户在**延迟与稳定性**之间取舍。
    ///
    /// 它决定稳态水位的**下沿**：worker 从"输入攒够一块"到"把结果写进输出环"要花
    /// 一次推理的时间，这段窗口里输出环只出不进，所以水位最低点 ≈ 余量 − 推理耗时。
    /// 取小了会在每批产出之间欠载（补静音 ⇒ 听感是咔哒），取大了白白增加延迟。
    /// 实测推理 54~64ms（2026-10-08 用户日志），故默认取 120ms。
    /// </summary>
    private int BacklogPadMs() => Math.Max(MinBacklogPadMs, _config.AiVoice.BacklogPadMs);

    /// <summary>
    /// 输出环的预填充量（样本）= 一个块 + 用户选的缓冲余量，并夹到环容量的一半
    /// （块长与余量都来自配置，可能被手改成超范围的值，必须保证读写指针不会碰头）。
    /// </summary>
    private int PadSamples()
        => Math.Min(_blockSamples48 + ChainRate * BacklogPadMs() / 1000, _outRing.Length / 2);
    private double _lastInferMs;

    /// <summary>输出环积压（毫秒），audio 线程写、UI 线程读。</summary>
    private int _lastBacklogMs;

    /// <summary>
    /// 「额外缓冲」滑条被拖动后置位：下一次 Read 时把水位**一次性**拉到目标值。
    ///
    /// 为什么需要它：调小 → 积压上限自然把水位压下来（限流逻辑）；但**调大**时水位
    /// 不会自己升上去（稳态下产出≈消费，水位不回归），滑条看起来就"没功能" ——
    /// 本项目已经因为这类"滑条不生效"被用户报过一次（音频块那次）。
    /// 一次性校准之后仍交给自然锯齿，**不做持续调节**（持续丢/垫样本反而会断音）。
    /// </summary>
    private volatile bool _backlogRetargetPending;

    /// <summary>由界面在「额外缓冲」变化后调用：请求把水位重新拉到目标值。</summary>
    public void RequestBacklogRetarget() => _backlogRetargetPending = true;

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
    /// <summary>最近一块的推理耗时（毫秒）。跨进程时读宿主写回来的值，否则读本地引擎的。</summary>
    public double LastInferMs => _hostClient?.HostInferMs ?? _lastInferMs;

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
            else if (_hostClient != null)
            {
                // 跨进程：走控制块的热参数通道（宿主每块读一次），同样不重建引擎。
                // 漏了这条，跨进程时变调/索引占比的滑条会完全空转。
                _hostClient.SetHotParams(
                    (int)Math.Round(_config.AiVoice.Semitones),
                    _config.AiVoice.IndexFile is { Length: > 0 }
                        ? Math.Clamp(_config.AiVoice.IndexRate / 100f, 0f, 1f)
                        : 0f);
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

        // 预填充 = 一个块 + 余量（见 StartupPadMs）。
        //
        // ⚠ 只垫"一个块"是不够的：worker 首次产出之前，输出环要撑过
        // 「等输入攒够一块 + 一次推理耗时」，于是稳态水位被压到 0 附近、
        // 每批产出之间欠载（补静音 ⇒ 咔哒）。2026-10-08 之前之所以没暴露这个问题，
        // 是因为引擎加载期间输入环攒了 1 秒历史、worker 起来追赶把水位顶到了上限，
        // 那个"上限水位"顺带掩盖了预填充的不足 —— 代价是几百毫秒的永久延迟。
        //
        // 就绪后的稳态水位 = [P − 推理耗时 − 块长, P − 推理耗时]，
        // 即**最低点 ≈ 余量 − 推理耗时**（块长 160ms、推理 60ms、余量 120ms ⇒ 约 [60 … 220]ms）。
        //
        // ⚠ 这个式子成立的前提，是 ReadCore 里"引擎未就绪时不消费输出环"：
        // 否则预填充会在加载的 1.5 秒里被吃光，就绪时水位从 0 起步，稳态水位掉到
        // [0 … 块长]，每批产出的末尾恰好触底 —— 任何抖动都欠载（比高延迟更糟）。
        //
        // 夹到环容量的一半：块长与余量都来自配置（可能被手改成超范围的值），
        // 必须保证读写指针不会碰头。
        _outWrite = PadSamples();
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
    /// <summary>独立进程客户端（<c>EnableHostProcess</c> 为真时才有值；否则为 null，走进程内引擎）。</summary>
    private AiVoiceHostClient? _hostClient;

    /// <summary>
    /// 进程内引擎（回退路径）：三个 ONNX 全部在本进程里加载。
    ///
    /// 它是独立进程起不来时的兜底 —— 功能完全可用，只是**关掉 AI 变声后内存回收不干净**
    ///（ONNX 与 CUDA 的内存池不还给系统）。
    /// </summary>
    private void CreateLocalEngine(string engineDir, FaissIndex? index, float rate)
    {
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
    }

    /// <summary>
    /// 尝试起独立进程并等它就绪。**失败不抛异常**，而是返回 false 并给出原因，
    /// 让调用方自动退回 <see cref="CreateLocalEngine"/>。
    ///
    /// 跨进程时采样率转换在宿主里（<see cref="RvcBlockProcessor"/>），所以这里**不碰**
    /// <c>_toModel/_fromModel</c> —— 主进程收发的一直是 48 kHz。
    /// </summary>
    private bool TryStartHostBackend(string engineDir, out string error)
    {
        error = string.Empty;
        AiVoiceHostClient? client = null;

        try
        {
            var parameters = new AiVoiceHostParams
            {
                Cv = Path.Combine(engineDir, "contentvec.onnx"),
                Rmvpe = Path.Combine(engineDir, "rmvpe.onnx"),
                Voice = Path.Combine(ConfigStore.AiVoicesDirectory, _config.AiVoice.VoiceModel!),
                Index = string.IsNullOrWhiteSpace(_config.AiVoice.IndexFile)
                    ? null
                    : Path.Combine(ConfigStore.AiIndexDirectory, _config.AiVoice.IndexFile),
                BlockMs = _config.AiVoice.BlockMs,
                ContextMs = _config.AiVoice.ContextMs,
                CrossfadeMs = _config.AiVoice.CrossfadeMs,
                Semitones = (int)Math.Round(_config.AiVoice.Semitones),
                IndexRate = _config.AiVoice.IndexRate / 100f,
                UseGpu = true,
            };

            client = AiVoiceHostClient.Start(parameters, out var startError);
            if (client == null)
            {
                error = "子进程启动失败：" + startError;
                return false;
            }

            // 等就绪，并**持续上报加载进度**（界面上那条进度条要动）。
            // 不能用 client.WaitReady —— 它是阻塞的，期间界面进度条纹丝不动，
            // 用户看到的就是"点了开关什么都没发生"。
            var deadline = Environment.TickCount64 + 180_000;
            while (Environment.TickCount64 < deadline)
            {
                var state = client.State;
                var progress = client.Progress;
                var err = client.LastError;

                if (state == AiVoiceHostState.Ready) break;
                if (state == AiVoiceHostState.Failed)
                {
                    error = "子进程加载失败：" + err;
                    client.Dispose();
                    return false;
                }
                if (!client.IsAlive)
                {
                    error = "子进程在加载过程中退出：" + err;
                    client.Dispose();
                    return false;
                }

                // 宿主给的进度是 0~1，映射到 15~95（前 15% 留给"起进程/连管道"）
                _loadProgress = 15 + (int)Math.Round(progress * 80);
                ProgressChanged?.Invoke(this, _loadProgress);
                Thread.Sleep(80);
            }

            if (client.State != AiVoiceHostState.Ready)
            {
                error = client.State == AiVoiceHostState.Failed
                    ? "子进程加载失败：" + client.LastError
                    : $"子进程在 180 秒内未就绪（当前状态 {client.State}）";
                client.Dispose();
                return false;
            }

            _hostClient = client;
            _loadProgress = 100;
            ProgressChanged?.Invoke(this, 100);
            Log.Info($"[AI 变声] 推理在独立进程（PID {client.HostPid}）：块 {_config.AiVoice.BlockMs}ms"
                     + $" / 上下文 {_config.AiVoice.ContextMs}ms / 音色 {_config.AiVoice.VoiceModel}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { client?.Dispose(); } catch { }
            return false;
        }
    }

    /// <summary>
    /// 跨进程的工作循环：把输入环的数据喂给宿主，再把宿主的输出写进输出环。
    ///
    /// 与进程内那套最大的不同是**异步**：写进去不会立刻有结果，要过一会儿才能取。
    /// 所以这里不是"取一块 → 处理 → 写回"，而是"喂一批、取一批"反复进行，
    /// 输入与输出各自按自己的节奏流动，靠两端的环做解耦。
    ///
    /// 输出环的水位策略与进程内**完全一致**（同样受 <c>capSamples</c> 限制、超了丢最老），
    /// 所以延迟不会因为跨进程而变大。
    /// </summary>
    private void HostWorkerLoop()
    {
        var scratch = new float[_blockSamples48];
        var outScratch = new float[_blockSamples48 * 2];

        // 跨进程路径的独立统计（**不能复用 _lateBlocks**：它的语义是"输出环读不满=欠载"，
        // 而这里要分别看"喂出去多少/取回来多少/输出环满丢了多少/输出环空了多久"，
        // 混在一个计数器里就看不出问题在哪）。
        long fedSamples = 0, gotSamples = 0, droppedOut = 0, starved = 0;
        var statClock = Environment.TickCount64;

        while (_running)
        {
            var client = _hostClient;
            if (client == null || !client.IsAlive)
            {
                // 宿主没了：静音输出，等 Stop/重启（不在这里自作主张重启，
                // 免得崩溃后陷入"起来就崩"的循环）
                lock (_ringGate)
                {
                    for (var i = 0; i < _blockSamples48; i++)
                    {
                        _outRing[_outWrite] = 0f;
                        _outWrite = (_outWrite + 1) % _outRing.Length;
                    }
                }

                Thread.Sleep(20);
                continue;
            }

            var didWork = false;

            // ---- 1) 喂：把输入环里已有的数据送给宿主（最多一整块）----
            int take;
            lock (_ringGate)
            {
                var available = Available(_inRing, _inRead, _inWrite);
                take = Math.Min(available, _blockSamples48);
                for (var i = 0; i < take; i++)
                {
                    scratch[i] = _inRing[_inRead];
                    _inRead = (_inRead + 1) % _inRing.Length;
                }
            }

            if (take > 0)
            {
                // ⚠ 这一段属于**旧的单线程实现**，已不再被调用（管道版改用 HostWorkerLoopV2
                // 的两个线程）。保留它只是为了留个对照，所以只要保证能编译即可。
                client.WriteInput(scratch.AsSpan(0, take));
                fedSamples += take;
                didWork = true;
            }

            // ---- 2) 取：把宿主的输出写进输出环（受水位上限限制，超了丢最老）----
            var got = client.ReadOutput(outScratch);
            if (got > 0)
            {
                // 与进程内路径一致：水位上限就是预填充量（一个块 + 额外缓冲），
                // 超过就丢最老的，防止积压变成永久延迟。
                var cap = PadSamples();
                lock (_ringGate)
                {
                    for (var i = 0; i < got; i++)
                    {
                        var next = (_outWrite + 1) % _outRing.Length;
                        if (next == _outRead)
                        {
                            _outRead = (_outRead + 1) % _outRing.Length;
                            droppedOut++;
                        }

                        _outRing[_outWrite] = outScratch[i];
                        _outWrite = next;
                    }

                    // 与进程内路径一致：一旦超过水位上限就丢最老的，防止积压变成永久延迟
                    var avail = Available(_outRing, _outRead, _outWrite);
                    while (avail > cap)
                    {
                        _outRead = (_outRead + 1) % _outRing.Length;
                        droppedOut++;
                        avail--;
                    }
                }

                gotSamples += got;
                didWork = true;
            }
            else if (Available(_outRing, _outRead, _outWrite) < _blockSamples48)
            {
                starved++;   // 输出环空到不够一块：这是真正的"欠载"信号
            }

            if (!didWork) Thread.Sleep(2);   // 两头都没事干，让出 CPU

            // ---- 3) 每 2 秒打一条跨进程统计，用来定位"音色不对"到底是不是断流/丢块 ----
            var now = Environment.TickCount64;
            if (now - statClock >= 2000)
            {
                statClock = now;
                long outAvail;
                long inAvail;
                lock (_ringGate)
                {
                    outAvail = Available(_outRing, _outRead, _outWrite);
                    inAvail = Available(_inRing, _inRead, _inWrite);
                }

                Log.Info($"[AI 变声] 跨进程统计：喂 {fedSamples * 1000 / ChainRate}ms｜取 {gotSamples * 1000 / ChainRate}ms"
                         + $"｜输出环 {outAvail * 1000 / ChainRate}ms｜输入环 {inAvail * 1000 / ChainRate}ms"
                         + $"｜输出环丢 {droppedOut}｜空转 {starved}｜宿主活 {client.IsAlive}"
                         + $"｜推理 {client.HostInferMs:0}ms");
                fedSamples = gotSamples = droppedOut = starved = 0;
            }
        }
    }

    /// <summary>
    /// 跨进程工作循环（管道版）：**两个线程**分别负责喂与取。
    ///
    /// 为什么必须两个线程：管道是全同步阻塞的。
    /// 在一条线程上轮流做必然死锁 ——
    /// 写管道时若缓冲满就要等人来读，而"读"也在同一条线程上（正卡在写）；
    /// 反过来读的时候若没数据就要等人来写，而"写"同样卡在同一条线程上。
    ///
    /// 停止流程：<c>StopWorker</c> 会把 <c>_running</c> 置 false 并 Dispose 客户端，
    /// 关闭管道会让阻塞的读写抛 IOException 后返回，两个线程随即退出。
    /// </summary>
    private void HostWorkerLoopV2()
    {
        var feeder = new Thread(HostFeederLoop)
        {
            IsBackground = true,
            Name = "MateMic.AiVoice.Feed",
            Priority = ThreadPriority.BelowNormal,
        };
        var fetcher = new Thread(HostFetcherLoop)
        {
            IsBackground = true,
            Name = "MateMic.AiVoice.Fetch",
            Priority = ThreadPriority.BelowNormal,
        };

        feeder.Start();
        fetcher.Start();

        feeder.Join();
        fetcher.Join();
    }

    /// <summary>喂线程：把输入环里的数据成块写进管道（阻塞写）。</summary>
    private void HostFeederLoop()
    {
        var scratch = new float[_blockSamples48];

        while (_running)
        {
            var client = _hostClient;
            if (client == null) break;

            int take;
            lock (_ringGate)
            {
                var available = Available(_inRing, _inRead, _inWrite);
                take = (int)Math.Min(available, _blockSamples48);
                for (var i = 0; i < take; i++)
                {
                    scratch[i] = _inRing[_inRead];
                    _inRead = (_inRead + 1) % _inRing.Length;
                }
            }

            if (take <= 0)
            {
                Thread.Sleep(2);
                continue;
            }

            // 阻塞写：管道对端有专门的读线程，写完是安全的
            if (!client.WriteInput(scratch.AsSpan(0, take))) break;
        }
    }

    /// <summary>取线程：从管道读回处理结果，写进输出环（受水位上限约束，超了丢最老）。</summary>
    private void HostFetcherLoop()
    {
        var scratch = new float[_blockSamples48 * 2];

        while (_running)
        {
            var client = _hostClient;
            if (client == null) break;

            // 阻塞读：管道对端有专门的写线程
            var got = client.ReadOutput(scratch);
            if (got <= 0) break;

            var cap = PadSamples();
            lock (_ringGate)
            {
                for (var i = 0; i < got; i++)
                {
                    var next = (_outWrite + 1) % _outRing.Length;
                    if (next == _outRead) _outRead = (_outRead + 1) % _outRing.Length;
                    _outRing[_outWrite] = scratch[i];
                    _outWrite = next;
                }

                // 与进程内路径一致：超过水位上限就丢最老的，防止积压变成永久延迟
                var avail = Available(_outRing, _outRead, _outWrite);
                while (avail > cap)
                {
                    _outRead = (_outRead + 1) % _outRing.Length;
                    avail--;
                }
            }
        }
    }

    private void StartWorker()
    {
        // ⚠ 必须把 _hostClient 也算进"已经在跑"：跨进程时 _engine 永远是 null
        // （引擎在子进程里），只看 _engine 会导致每次 Sync 都重新起一个子进程。
        if (_engine != null || _hostClient != null || _starting) return;
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

        // ⚠ **独立进程暂时打开，用于定位"音色不对"**（2026-11-05 夜间）。
        //
        // 已经确认的事：
        //   · 管道传输正确（--pipecheck：46080 样本 0 不一致）；
        //   · 跨进程与进程内的 DSP 输出差异是**均匀分布在全段**的 1~10%，
        //     两边 SessionOptions 相同（都是默认线程数）⇒ 属于 ONNX 多线程浮点非确定性，
        //     是正常现象，且不足以解释用户听到的"机器人声 + 高频抖动"；
        //   · 所以嫌疑转向**主进程的输出环被抽干、周期性补静音**那条路径 ——
        //     而 --rpc-check 根本不经过它（它直接从管道读），因此测不出。
        //
        // 现在打开它，靠 [AI 变声] 诊断日志里的「欠载 / 积压」判断到底是哪一种。
        // 定位完要改回 false 再发布。
        const bool hostProcessEnabled = true;

        if (hostProcessEnabled)
        {
            if (!TryStartHostBackend(engineDir, out var hostError))
            {
                Log.Warn("[AI 变声] 独立进程不可用，已自动退回进程内推理：" + hostError);
                CreateLocalEngine(engineDir, index, rate);
            }
        }
        else
        {
            CreateLocalEngine(engineDir, index, rate);
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

        // 独立进程：Dispose 会先请求它自己退出（最多等 5 秒），超时才强杀整棵进程树。
        // 这就是"关掉 AI 变声就能把内存还回去"的关键一步：进程一结束，
        // 模型内存 / CUDA context / 显存 / faiss 索引全由操作系统回收。
        //
        // ⚠ **必须放到后台线程**（2026-11-05 用户实测"关掉 AI 变声时主界面卡住"）。
        // StopWorker 是在 UI 线程上被调用的，而 Dispose 内部要等子进程退出（正常也要
        // 几百毫秒，最坏 5 秒强杀）—— 同步做就会把整个界面冻住。
        //
        // 但**先同步关管道**：喂/取线程正阻塞在 Read/Write 上，不关管道它们不会退，
        // 下面 _worker.Join(3000) 就要白等 3 秒。关管道是毫秒级的，可以放在 UI 线程上。
        var client = _hostClient;
        _hostClient = null;
        if (client != null)
        {
            try { client.ClosePipes(); } catch { }

            Task.Run(() =>
            {
                try { client.Dispose(); }
                catch (Exception ex) { Log.Warn("[AI 变声] 回收宿主进程时出错：" + ex.Message); }
            });
        }

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
        // 输入环：只有积压到接近容量上限（下游长时间不来取）才丢最老的。
        // 平时**绝不能丢**——丢输入会让音频出现断裂，听感是"呲呲/咔哒"。
        var inAvailNow = Available(_inRing, _inRead, _inWrite);
        var inCapSamples = ChainRate;                 // 1 秒
        if (inAvailNow > inCapSamples)
        {
            var dropIn = inAvailNow - inCapSamples;
            _inRead = (_inRead + dropIn) % _inRing.Length;
            Interlocked.Add(ref _droppedSamples, dropIn);
        }

        // 引擎尚未就绪（首次加载 / 重建中，实测约 1.5~2 秒）：输入环**整体丢掉**。
        //
        // 原先这里照常攒到 1 秒上限，worker 一起来就连续追赶（产出速率是实时的好几倍），
        // 一路把输出环灌到上限、并丢掉那段历史音频。实测（2026-10-08 用户日志）
        // 启动一次**丢弃 1679ms、欠载 186 次**（约 0.9 秒静音）；更糟的是被灌上去的
        // 水位**不会自然回落**（稳态下产出≈消费），于是变成永久延迟。
        //
        // 那段历史要等 1.5 秒后才被处理，实时通话里早就过期（听到的是 1.5 秒前的话），
        // 宁可丢掉。
        //
        // ⚠ 为什么是"全丢"而不是"留最近一块"：留下的那一块会让 worker 一就绪就**立刻**
        // 产出，那一次产出把水位顶到上限，而水位一旦被顶上去就**不会自然回落**，
        // 于是又回到"上限 − 块长"的老位置（实测就是这么来的）。全丢掉之后，worker 要等
        // 实时输入重新攒够一块才首次产出，水位正好落在设计值上。
        // ⚠ 判断"引擎未就绪"必须同时看两条路径：跨进程时 _engine 恒为 null
        // （引擎在子进程里），只判 _engine 会让跨进程路径**永远**走这个分支 ——
        // 输入环被一直丢空、输出环没人消费，表现为"有输入没输出、负载 0"
        //（2026-11-05 用户实测踩到）。
        if (_engine == null && _hostClient == null)
        {
            var dropped = Available(_inRing, _inRead, _inWrite);
            if (dropped > 0) Interlocked.Add(ref _droppedSamples, dropped);
            _inRead = _inWrite;
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

        // 引擎尚未就绪（加载 / 重建中）：输出直接给静音，**不消费输出环** ——
        // 让 Allocate 预填充的那段静音完整留到"就绪那一刻"。
        //
        // ⚠ 这一条不是可有可无的优化，而是**预填充能否生效的前提**：
        // 加载要 1.5 秒，若这期间照常消费，预填充会被耗尽、就绪时水位从 0 起步，
        // 稳态水位就落到 [0 … 块长] —— 每批产出的末尾恰好触底，任何抖动都会欠载
        //（补静音 ⇒ 咔哒）。等于把"永久高延迟"换成了"周期性咔哒"，更糟。
        // 加载期间消费与不消费，输出都是静音，听感没有区别。
        // 同上：跨进程时 _engine 恒为 null，必须把 _hostClient 也视为"就绪"，
        // 否则这里会一直输出静音、且不消费输出环（用户实测"有输入没输出"）。
        if (_engine == null && _hostClient == null)
        {
            buffer.Clear();
            Volatile.Write(ref _lastBacklogMs, 0);
            return buffer.Length;
        }

        // 输出：有就取，没有补静音
        var available = (_outWrite - _outRead + _outRing.Length) % _outRing.Length;

        // 「额外缓冲」滑条刚被拖过：把水位**一次性**拉到目标值。
        //   高了 → 丢掉最老的（那正是用户想砍掉的那段延迟）；
        //   低了 → 垫静音（多出一小段静音，换回"不欠载"的余量）。
        // 只做一次，之后仍由自然锯齿运行。
        if (_backlogRetargetPending)
        {
            _backlogRetargetPending = false;
            var target = PadSamples();
            if (available > target)
            {
                var drop = available - target;
                _outRead = (_outRead + drop) % _outRing.Length;
                Interlocked.Add(ref _droppedSamples, drop);
                available = target;
            }
            else if (available < target)
            {
                var fill = target - available;
                for (var i = 0; i < fill; i++)
                {
                    _outRing[_outWrite] = 0f;
                    _outWrite = (_outWrite + 1) % _outRing.Length;
                }
                available = target;
            }
        }

        // 记下当前积压供界面显示。这是**真实存在的额外延迟**（产出快过消费时的排队量），
        // 稳态下应稳定在"一个块 + 推理余量"这一档（2026-10-08 起上限不再固定为 2×块长）。
        Volatile.Write(ref _lastBacklogMs, available * 1000 / ChainRate);

        // ⚠ 限流：积压超过目标就把**最老的**丢掉，把延迟拉回来。
        //
        // 为什么必须做：引擎创建要 1.5 秒左右，这期间 Read 一直在往输入环塞数据
        // （环容量 2 秒），worker 一起来就连续追赶、一次性把输出环灌满；而积压**不会自然消失**
        // （稳态下产出≈消费），于是变成永久延迟——实测曾达 839ms，用户听到的是"延迟一秒多"。
        // 实时语音里宁可丢一点音频，也不能让延迟无界增长。
        // 输出环稳态要容纳"一次推理的产出"（一个块），**再加上覆盖一次推理耗时的余量**：
        // 写入之前水位必须撑得过 worker 算这一块的时间，否则会欠载（补静音 ⇒ 咔哒）。
        // 上限 = 块长 + max(120ms, 实测推理耗时 + 40ms)。
        //
        // 旧实现直接取 2 个块 = 块长 + 块长。块长 160ms 时是 320ms，而这一档真正的
        // 物理需求只有约 220~260ms（推理实测 54~64ms）。多出来的部分在"积压不会自然
        // 回落"的前提下等于白送的永久延迟 —— 实测稳态水位就停在 [160 … 300]ms。
        var headroomMs = Math.Max(BacklogPadMs(), (int)Math.Ceiling(_lastInferMs) + BacklogJitterMarginMs);
        var capSamples = Math.Min(_blockSamples48 + ChainRate * headroomMs / 1000, _outRing.Length / 2);
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
        // 独立进程路径走另一套循环（跨进程是异步的，不能复用下面"一块进一块出"的逻辑）
        if (_hostClient != null)
        {
            HostWorkerLoopV2();
            return;
        }
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
        // ⚠ 环的读/写各用一次锁，**推理本身在锁外**。
        // 音频线程的 Read 持同一把锁，锁内只有内存拷贝（微秒级）；
        // 推理要 60ms 上下，绝不能放进锁里，否则音频线程会被卡住。
        // 为什么 worker 也要持锁：界面线程随时可能改环（「额外缓冲」滑条、参数重建），
        // 不串行化就会让 worker 在错位的位置上读写。
        lock (_ringGate)
        {
            // 取一个块（40k 域）
            for (var i = 0; i < _blockSamples48; i++)
            {
                _inScratch[i] = _inRing[_inRead];
                _inRead = (_inRead + 1) % _inRing.Length;
            }
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
        lock (_ringGate)
        {
            for (var i = 0; i < n; i++)
            {
                _outRing[_outWrite] = out48[i];
                _outWrite = (_outWrite + 1) % _outRing.Length;
            }
        }
    }

    private void SkipOneBlock()
    {
        lock (_ringGate)
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
