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

    /// <summary>欠载诊断日志的节流时间戳（毫秒）。实时线程上不能频繁写日志。</summary>
    private long _lastUnderrunLogMs;

    /// <summary>
    /// **实测**的相邻产出帧最大间隔（毫秒）。
    ///
    /// 由取线程每 2 秒更新。它天然包含"推理 + 管道传输 + 线程调度"的全部延迟，
    /// 所以预填充直接拿它当余量，不再需要任何估算常数 ——
    /// 这正是 2026-11-05 反复爆音的根源：我一直在猜这个数。
    /// 0 表示还没有测量值（宿主刚就绪），此时按两块保底。
    /// </summary>
    private long _measuredProduceGapMs;

    // ---- 跨进程**同步**直连用的缓冲（2026-11-06 重做，方案 A）----
    // 只有"一整块"这一个概念：`_syncIn` 送出去、`_syncOut` 收回来，
    // `_syncOut[0.._syncLength]` 是本次可交付的数据。没有任何水位/余量/漂移逻辑。
    private float[] _syncIn = Array.Empty<float>();
    private float[] _syncOut = Array.Empty<float>();
    private int _syncLength;
    private int _syncRead;

    /// <summary>同步往返的诊断计数（只打头几次，避免刷屏）。</summary>
    private int _syncDiagCount;

    /// <summary>
    /// 消费侧观测到的输出环水位最低点（毫秒）。由 `ReadCore` 写、诊断日志读后清零。
    /// 它才是"预填充够不够"的判据：> 0 说明一直有余量，= 0 说明每次都被吃光。
    /// </summary>
    private int _minOutAvailMs = int.MaxValue;

    /// <summary>读取并清零消费侧水位最低点；没测到过返回 -1。</summary>
    private int TakeMinOutAvailMs()
    {
        var value = Volatile.Read(ref _minOutAvailMs);
        Volatile.Write(ref _minOutAvailMs, int.MaxValue);
        return value == int.MaxValue ? -1 : value;
    }

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
    {
        // **跨进程时补一点点余量（20 ms），不需要一整块。**
        //
        // 机理（与 2026-10-07 / 2026-10-08 两次"周期性咔哒"同源，都是水位触底）：
        // 宿主每 **一整块**一次性产出（块长 120ms、5760 样本），主进程每 20ms 消费 960，
        // 所以输出环水位天然是**锯齿**，幅度正好等于一块。
        // 预填充也正好一块 ⇒ 水位在 [0, 一块] 摆动，每个块末尾精确触底。
        //
        // 进程内能撑住，是因为它"产出 → 写环"是微秒级、几乎无延迟；
        // 跨进程多了管道传输 + 两次线程调度（毫秒级且不确定），就会**穿破 0**
        // ⇒ 补静音 ⇒ 周期性轻微爆音/底噪（用户实测）。
        //
        // 所以这里只补 20ms（约 0.02 秒的额外延迟，用户明确要求延迟要省着用），
        // 足够吸收调度抖动，又不会像"补一整块"那样白送 120ms。
        // 余量取 **40ms**（2026-11-05 实测后从 20ms 上调）。
        //
        // 依据：用户的配置是"极限档"—— 块长 80ms，而 GPU 推理中位 69ms、最大 82ms，
        // 占用率 86%，余量只有 11ms。实测 40 块里有 1 块推理 ≥80ms（超过块长），
        // 正好对应每秒一次的欠载 ⇒ 周期性轻微爆音。
        //
        // 20ms 不够：此时水位最低点恰好等于一次读取量（20ms），等于没有余量，一抖就欠。
        // 40ms 让水位落在 [40, 120]ms，足以吸收 69→82ms 的推理抖动。
        //
        // ⚠⚠ **余量必须大于"每块的推理耗时"** —— 这是 2026-11-05 排查多轮的最终根因。
        //
        // 算一笔账：每产出一块的周期里，输出环要同时供两件事 ——
        //   · 推理期间主进程照常消费（推理多慢就消费多久）；
        //   · 以及一整块本身的消费。
        // 所以预填充至少要 = 块长 + 推理耗时 + 一点抖动余量。
        //
        // 原先这里写死 40ms：块长 500、推理 75 时，预填充 540 < 消耗 575 ⇒ **每块净亏 35ms**
        // ⇒ 几十块后被抽干（日志实测："需要 480 样本，实际只有 0，水位 0ms"）⇒ 爆音。
        //
        // 而差值恒为「40 − 推理耗时」，**与块长无关** ——
        // 这正是用户看到的"改块长只能减少、不能消除，看起来与参数无关"。
        //
        // 现在按"**产出延迟**"给余量，而不是只看推理耗时（2026-11-05 最终修正）。
        //
        // 产出测量实测（块长 80ms）：宿主产出间隔 82ms、帧长 80ms ⇒ **产能完全正常**。
        // 但输出环水位在 0~155ms 摆动、最低点精确到 0 ⇒
        // 预填充（155ms = 块长 80 + 推理约 75）< 需要的（块长 + 产出延迟）。
        // 其中产出延迟 = 推理 + 管道 + 线程调度，实测约 155ms，明显大于单看推理的 75ms。
        //
        // **预填充完全按实测数据算，不再有任何估算常数**（2026-11-05 最终版）。
        //
        // 之前这里一路猜：写死 40ms → 推理+20 → 推理×1.5+40，每改一次都是"再大一点"，
        // 然后又被吃穿。用户明确指出"预估的数字造成了大量 bug"—— 他是对的。
        //
        // 现在只有两个来源：
        //   · `_measuredProduceGapMs`：取线程**实测**的相邻产出帧最大间隔（每 2 秒更新）。
        //     它天然包含推理 + 管道 + 线程调度的全部延迟，不需要我再估；
        //   · 块长：输出环要容纳"一次一整块的突发写入"。
        // 所以预填充 = 块长 + 实测最大产出间隔 + 20ms（测量窗口内的抖动）。
        //
        // 启动初期还没有测量值（宿主刚就绪），此时按 2×块长保底 —— 最保守的起点，
        // 第一次产出测量出来后就自动收敛到真实值。
        var measuredGap = Volatile.Read(ref _measuredProduceGapMs);
        int hostPad;
        if (_hostClient == null)
        {
            hostPad = 0;
        }
        else if (measuredGap > 0)
        {
            hostPad = ChainRate * (int)(measuredGap + 20) / 1000;
        }
        else
        {
            hostPad = _blockSamples48 * 2;   // 尚无实测：按两块保底
        }

        return Math.Min(_blockSamples48 + hostPad + ChainRate * BacklogPadMs() / 1000,
                        _outRing.Length / 2);
    }
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
        // 输出环留 4 秒（原来 2 秒）。
        //
        // ⚠ 为什么从 2 秒加到 4 秒（2026-11-05）：预填充与 capSamples 都取
        // `min(..., 环容量/2)`，而块长一大，"预填充（块长 + 40ms）+ 一整块突发"
        // 就会超过"容量/2" ⇒ capSamples 被容量卡住 ⇒ 宿主每产出一块就被削一次。
        // 实测块长 500ms 时：预填充 540ms + 一块 500ms = 1040ms > 2 秒容量给的 1000ms 上限。
        // 4 秒容量把这条上限抬到 2000ms，块长到 900ms 都不会再触发。
        var outCap = ChainRate * 4;
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

        // 跨进程同步直连的两块缓冲（送出一块、收回一块）
        _syncIn = new float[_blockSamples48];
        _syncOut = new float[_blockSamples48];
        _syncLength = 0;
        _syncRead = 0;

        // 预填一块静音到输入环：同步模式首次 Read 就要"攒满一整块"，
        // 而启动瞬间采集还没送来任何数据，不预填的话第一块会是静音补位。
        // 预填一块后，第一次往返就有真实输入可用。
        var prefill = (int)Math.Min(_blockSamples48, _inRing.Length - 1);
        _inWrite = prefill;
        _inRead = 0;
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

            // **预热**：进度条走到 100% 时应该已经能听到效果。
            //
            // 宿主"就绪"只代表模型加载完；第一块还要等 worker 起来、推理一次才产出
            //（实测约 200ms，用户明确反馈"进度条走完后仍有一小段时间不生效"）。
            // 所以这里主动送一块进去、把它等回来并写进输出环 ——
            // 等这段返回时，输出环里已经有真实音频，进度条一满就能听见。
            _loadProgress = 95;
            ProgressChanged?.Invoke(this, 95);
            WarmUpHost(client);

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
    /// 宿主预热：送一块静音进去，等它处理完并**写进输出环**。
    ///
    /// 目的只有一个：让"进度条走满"与"已经能听到效果"这两件事对齐。
    /// 代价是输出环里先有一段约一块长的静音，但那本来就是预填充要垫的，
    /// 不额外增加延迟。超时只是放弃预热（不影响后续正常运行）。
    /// </summary>
    /// <summary>
    /// 宿主预热。
    ///
    /// ⚠ **同步模式下不要在这里等输出**（2026-11-06 踩到的坑）：
    /// 产品是由音频线程的 `Read` 驱动的 —— `Read` 调 `SyncRoundTrip` 送一块、等一块。
    /// 而预热发生在引擎创建阶段，那时音频线程还没开始调 `Read`，
    /// 所以宿主永远不会产出，这里就会白等满 10 秒（实测：用户看到"开启用很长时间"，
    /// 期间子进程不占 CPU/GPU，因为它也在等输入）。
    ///
    /// 保留这个方法只为"启动时把 CUDA kernel / 内存池 预热一次"，
    /// 发一块进去即可，**不等待结果** —— 那一块的结果会被第一次 `Read` 顺带取走
    /// （帧是对齐的，多一块只影响最初 120ms，不会错位）。
    /// </summary>
    private void WarmUpHost(AiVoiceHostClient client)
    {
        try
        {
            var block = new float[_blockSamples48];
            client.WriteInput(block);
        }
        catch (Exception ex)
        {
            Log.Warn("[AI 变声] 预热失败：" + ex.Message);
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

        // **产出口径的测量**（2026-11-05）：记录每帧到达的间隔与帧长。
        // 判据很直接：
        //   · 帧间隔 ≈ 块长 ⇒ 宿主产能正常，问题在别处；
        //   · 帧间隔明显 > 块长（如 1.5~2 倍）⇒ 宿主供给不足，就是它导致水位被抽干。
        var lastFrameMs = 0L;
        long frames = 0, frameSamples = 0, maxGapMs = 0, gapSumMs = 0;
        var statClock = Environment.TickCount64;

        while (_running)
        {
            var client = _hostClient;
            if (client == null) break;

            // 阻塞读：管道对端有专门的写线程
            var got = client.ReadOutput(scratch);
            if (got <= 0) break;

            var now = Environment.TickCount64;
            if (lastFrameMs != 0)
            {
                var gap = now - lastFrameMs;
                frames++;
                frameSamples += got;
                gapSumMs += gap;
                if (gap > maxGapMs) maxGapMs = gap;
            }
            lastFrameMs = now;

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

                // ⚠ 这里**不再**测水位下限（2026-11-05 修正）：
                // 此刻是"刚写完一整块"，水位必然最高，测出来毫无意义（实测显示 80ms，
                // 而消费侧真正的最低点是 0ms）。水位最低点只能由消费侧（ReadCore）记录。
            }

            // 每 2 秒报一次"产出口径"，并把**实测最大间隔**存给 PadSamples 用。
            if (now - statClock >= 2000)
            {
                statClock = now;
                if (frames > 0)
                {
                    // 实测值直接决定预填充：maxGap 就是"最长多久没有新数据进来"，
                    // 输出环必须能撑过这段。不再做任何估算。
                    //
                    // 但**不能直接取单次 max**：那样预填充会随每次抖动跳变，延迟忽大忽小，
                    // 用户能明显感觉到。所以做成"快升慢降"：
                    //   · 遇到更大的间隔立刻跟上（安全第一）；
                    //   · 否则每 2 秒衰减 10%（缓慢收敛回去，几十秒后回到正常水平）。
                    var previous = Volatile.Read(ref _measuredProduceGapMs);
                    var decayed = (long)(previous * 0.9);
                    Volatile.Write(ref _measuredProduceGapMs, Math.Max(maxGapMs, decayed));

                    Log.Info($"[AI 变声] 产出测量：{frames} 帧 / 2s"
                             + $"｜平均间隔 {gapSumMs / frames}ms（块长 {_blockSamples48 * 1000 / ChainRate}ms）"
                             + $"｜最长间隔 {maxGapMs}ms"
                             + $"｜平均帧长 {frameSamples / frames * 1000 / ChainRate}ms"
                             + $"｜预填充 {PadSamples() * 1000 / ChainRate}ms"
                             + $"｜消费侧水位下限 {TakeMinOutAvailMs()}ms");
                }
                else
                {
                    Log.Warn("[AI 变声] 产出测量：2 秒内一帧都没收到（宿主可能卡死）");
                }
                frames = 0;
                frameSamples = 0;
                gapSumMs = 0;
                maxGapMs = 0;
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

        // ⚠ **独立进程已关闭，回退到进程内**（2026-11-06，用户决定）。
        //
        // 决定依据：跨进程后的"水位漂移"无法在现有结构下消除 ——
        // 产出速率 96%（每块多 4~5ms 的管道唤醒延迟）而消费严格 100%，
        // 水位必然单调下降，迟早被吃穿 ⇒ 周期性切断/爆音。
        // 加大预填充只是延后见底，压开销也到不了 100%（唤醒是结构性的）。
        //
        // **继续在这里改来改去没有意义**，改为：写文档 → 回退 → 按官方方式重做。
        // 完整复盘与重做方案见：
        //   文档\工作日志\2026-11-06-AI变声独立进程复盘与重做方案.md
        //
        // 重做的核心思路（文档第五节）：
        //   官方 realtime_gui 是"音频回调粒度 = 块长 + 回调内同步推理"，
        //   所以零缓冲、零漂移；本项目回调只有 10ms 而块长 120ms，粒度差 20 倍，
        //   所以只能异步 + 缓冲，而缓冲就会漂移。
        //   ⇒ **重做时先把渲染粒度提到一个整块，再同步推理**（方案 A）。
        //
        // 代码与自检全部保留：Ai/AiVoiceIpc/*、--pipecheck / --hostcheck /
        // --rpc-check / --synccheck / --rtcheck。重做时先让 --rtcheck 在目标配置下
        // 通过（欠载 = 0），再上真机。
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

        // ⚠⚠ **必须先关管道，再 Join worker**（2026-11-05 用户实测"关闭时界面卡一会儿"）。
        //
        // 喂/取线程阻塞在管道的 Read/Write 上，而 worker 线程正在 Join 它们。
        // 原先的顺序是"先 Join(3000) 再 ClosePipes" —— 于是 Join 必然白等满 3 秒，
        // 而这段等待发生在 **UI 线程**上，表现就是"关掉 AI 变声时界面卡住"。
        //
        // 用户的观察正好印证：开启后马上关（worker 还没建，Join 立即返回）不卡；
        // 稳定运行很久后关（数据流不断、循环走得快）也不卡；
        // 偏偏"刚就绪、输出环还没填满"那段时间关会卡 —— 那时取线程正阻塞在等宿主产出。
        //
        // 关管道是毫秒级的，可以放在 UI 线程上；"等子进程退出"才需要挪到后台。
        var client = _hostClient;
        _hostClient = null;
        if (client != null)
        {
            try { client.ClosePipes(); } catch { }
        }

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

        // "等子进程自己退出"（正常几百毫秒，最坏 5 秒强杀）放后台，不占 UI 线程。
        // 进程一结束，模型内存 / CUDA context / 显存 / faiss 索引全由操作系统回收 ——
        // 这是进程内 Dispose 做不到的事。
        if (client != null)
        {
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

        // ================================================================
        // 跨进程：**同步直连**（2026-11-06 重做，方案 A）
        //
        // 之前是异步：主进程喂 → 宿主算 → 取线程再写进输出环，音频线程从环里取。
        // 那条路必然漂移 —— 宿主每块要多花 4~5ms 等管道唤醒，产出只有 96%，
        // 而消费严格 100% ⇒ 水位单调下降 ⇒ 周期性补静音（用户听到的切断）。
        //
        // 现在改成**同步**：音频线程自己把一整块交给宿主、等它算完、直接拿走结果。
        // "送出"与"收回"严格 1:1 ⇒ **不存在速率差，也就不可能漂移**。
        // 这与官方 realtime_gui 在 `sd.Stream` 回调里同步推理是同一个结构。
        //
        // 代价（必须说清）：音频线程会被阻塞一个推理耗时（约 60~100ms），
        // 靠播放器的设备缓冲吸收。若某次推理超出缓冲余量，会整块补静音 ——
        // 这与官方行为一致，是同步方案的固有代价，不是 bug。
        // ================================================================
        var hostClient = _hostClient;
        if (hostClient != null)
        {
            return ReadSyncHost(hostClient, buffer);
        }

        lock (_ringGate)
        {
            return ReadCore(buffer);
        }
    }

    /// <summary>
    /// 跨进程**同步**读取：按块与宿主往返。
    ///
    /// 只做三件事，没有任何水位/余量/漂移的概念：
    ///   1. 缓冲里的数据用完 ⇒ 从输入环攒满一整块（不足就补静音）；
    ///   2. **同步**把这块交给宿主（阻塞写），再**同步**等回同样长度（阻塞读）；
    ///   3. 把结果放进缓冲，按调用方要的长度交出去。
    /// </summary>
    private int ReadSyncHost(AiVoiceHostClient client, Span<float> buffer)
    {
        // ⚠⚠ **必须先把本次采集到的音频写进输入环**（2026-11-06 修"没声音"）。
        //
        // 这段逻辑原本在 `ReadCore` 里。我在 `Read` 开头就 return 到同步分支，
        // 于是输入环**永远收不到任何样本** ⇒ 送进宿主的全是静音 ⇒ 输出静音。
        // 表现：宿主 CPU/GPU 都在跑（它确实在推理），但一点声音都没有，
        // 日志显示"输入攒 0/5760（RMS 0.0000）"。
        //
        // 这里只做"搬进环"这一件事；攒块与往返交给 SyncRoundTrip。
        lock (_ringGate)
        {
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
        }

        var taken = 0;

        while (taken < buffer.Length)
        {
            if (_syncRead >= _syncLength)
            {
                if (!SyncRoundTrip(client)) break;
            }

            var n = Math.Min(buffer.Length - taken, _syncLength - _syncRead);
            _syncOut.AsSpan(_syncRead, n).CopyTo(buffer[taken..]);
            _syncRead += n;
            taken += n;
        }

        if (taken < buffer.Length)
        {
            buffer[taken..].Clear();
            Interlocked.Increment(ref _lateBlocks);
        }

        return taken;
    }

    /// <summary>
    /// 与宿主做一次**同步往返**：喂一整块，取回一整块。
    /// 返回 false 表示对端已关闭（此时调用方会补静音）。
    /// </summary>
    private bool SyncRoundTrip(AiVoiceHostClient client)
    {
        // ---- 1) 攒满一整块输入 ----
        var got = 0;
        lock (_ringGate)
        {
            var available = Available(_inRing, _inRead, _inWrite);
            var n = (int)Math.Min(available, _blockSamples48);
            for (var i = 0; i < n; i++)
            {
                _syncIn[i] = _inRing[_inRead];
                _inRead = (_inRead + 1) % _inRing.Length;
            }
            got = n;
        }

        // 输入不够一块（启动初期 / 采集抖动）：后半补静音。
        // 这不会造成漂移 —— 送出去多少、收回来多少，始终 1:1。
        if (got < _blockSamples48)
        {
            Array.Clear(_syncIn, got, _blockSamples48 - got);
        }

        // ---- 2) 同步送 ----
        var wrote = client.WriteInput(_syncIn);
        if (!wrote)
        {
            if (Interlocked.Increment(ref _syncDiagCount) <= 5)
                Log.Warn("[AI 变声] 同步往返：写输入失败（对端已关闭）");
            return false;
        }

        // ---- 3) 同步收（等够一整块）----
        var read = 0;
        while (read < _blockSamples48)
        {
            var n = client.ReadOutput(_syncOut.AsSpan(read, _blockSamples48 - read));
            if (n <= 0) break;
            read += n;
        }

        if (read < _blockSamples48)
        {
            Array.Clear(_syncOut, read, _blockSamples48 - read);
            Interlocked.Increment(ref _lateBlocks);
        }

        // 头几次往返打详细信息：输入攒到多少、写成功与否、收回多少、输出是否全静音
        if (Interlocked.Increment(ref _syncDiagCount) <= 5)
        {
            double inRms = 0, outRms = 0;
            for (var i = 0; i < _blockSamples48; i++)
            {
                inRms += (double)_syncIn[i] * _syncIn[i];
                outRms += (double)_syncOut[i] * _syncOut[i];
            }
            inRms = Math.Sqrt(inRms / _blockSamples48);
            outRms = Math.Sqrt(outRms / _blockSamples48);
            Log.Info($"[AI 变声] 同步往返：输入攒 {got}/{_blockSamples48}（RMS {inRms:0.0000}）"
                     + $"｜写 {wrote}｜收回 {read}/{_blockSamples48}（RMS {outRms:0.0000}）");
        }

        _syncLength = _blockSamples48;
        _syncRead = 0;
        return true;
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
        // ⚠⚠ 两处关键修正（2026-11-05，修"跨进程后周期性爆音/底噪"）：
        //
        // 1) 必须用 **LastInferMs 属性**，不能用字段 _lastInferMs。
        //    字段只有进程内 worker 会写，跨进程时**恒为 0** ⇒ headroomMs 永远算成 40ms。
        //    属性在跨进程时读的是宿主回传的真实推理耗时。
        //
        // 2) capSamples 必须 ≥ **预填充 + 一整块**，不能只有"块长 + headroom"。
        //    宿主是**一整块突发**写入的：预填充 P 加上刚写入的一块 B，瞬时水位就是 P+B。
        //    原来 cap = 块长 + 40ms，而预填充也正好是 块长 + 40ms（跨进程时 headroom 被算成 40）
        //    ⇒ 两者相等 ⇒ 水位一开始就顶在 cap 上 ⇒ 宿主每产出一块就被"丢最老"削掉一整块
        //    ⇒ 时间轴每块跳一次 ⇒ 听感是有节奏的爆音/底噪。
        //    也因为 cap 与预填充同步随块长变化、差值恒为 0，所以**改参数完全没效果**
        //    —— 这正是用户观察到"参数无关"的原因。
        var headroomMs = Math.Max(BacklogPadMs(), (int)Math.Ceiling(LastInferMs) + BacklogJitterMarginMs);
        var capSamples = Math.Min(PadSamples() + _blockSamples48, _outRing.Length / 2);

        // **消费侧的水位最低点**（2026-11-05 修正：之前测在写侧，测出来永远偏高）。
        // 只有这里才看得到"两次产出之间被消费到什么程度"。
        var availMs = (int)(available * 1000 / ChainRate);
        if (availMs < Volatile.Read(ref _minOutAvailMs)) Volatile.Write(ref _minOutAvailMs, availMs);
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

            // **诊断**：欠载却"积压不为 0"是矛盾的（说明瞬时水位到过 0，
            // 而 2 秒一次的诊断采样看不到）。把欠载当时的实际可用量与请求量记下来，
            // 才能判断是"水位真的空了"还是"计算/同步有问题"。
            // 节流到 5 秒一条，避免刷屏（实时线程上不能频繁写日志）。
            var now = Environment.TickCount64;
            if (now - _lastUnderrunLogMs >= 5000)
            {
                _lastUnderrunLogMs = now;
                Log.Warn($"[AI 变声] 欠载：需要 {buffer.Length} 样本，实际只有 {take}"
                         + $"（输出环水位 {available * 1000 / ChainRate}ms，本次补静音 {(buffer.Length - take) * 1000 / ChainRate}ms）"
                         + $"｜cap {capSamples * 1000 / ChainRate}ms｜预填充 {PadSamples() * 1000 / ChainRate}ms");
            }
        }
        else if (_hostClient != null)
        {
            // ---- 抗漂移：用极小的时间伸缩稳住水位（2026-11-05）----
            //
            // 实测：跨进程产出速率约 96%（每块 120ms 的节拍里要多花 4~5ms 等管道唤醒），
            // 而消费严格 100% ⇒ 水位必然单调下降 ⇒ 迟早被吃穿 ⇒ 周期性补静音。
            // 预填充只是延后，压开销也到不了 100%（唤醒延迟是结构性的）。
            //
            // 做法：按水位偏差**微调消费量**（只动读指针，不改音频内容）：
            //   水位偏低 ⇒ 读指针**回退 1 个样本**（这一段少消费 1 个）；
            //   水位偏高 ⇒ 读指针**前进 1 个样本**（多消费 1 个，丢掉一个）。
            // 每次只动 1 个样本（10ms 一次 ⇒ 0.2% 的速率微调），人耳听不出，
            // 但足以把水位稳在目标附近，消掉周期性爆音。
            // 只在偏差超过一个块时才动手，避免常态抖动也去改。
            var target = PadSamples();
            var delta = target - available;
            if (Math.Abs(delta) > _blockSamples48)
            {
                lock (_ringGate)
                {
                    if (delta > 0)
                    {
                        // 水位偏低：把读指针回退一个（相当于这次少消费一个样本）
                        _outRead = (_outRead - 1 + _outRing.Length) % _outRing.Length;
                    }
                    else
                    {
                        // 水位偏高：把读指针再前进一个（多消费一个）
                        _outRead = (_outRead + 1) % _outRing.Length;
                    }
                }
            }
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
                     + $"（额外延迟，上限 {capSamples * 1000 / ChainRate}ms）"
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
        // 独立进程走同步直连（见 ReadSyncHost / SyncRoundTrip），这里什么都不做。
        // 旧的异步流水 HostWorkerLoopV2 仍保留在代码里，但不再启用。
        if (_hostClient != null)
        {
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
