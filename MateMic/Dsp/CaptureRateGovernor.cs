using MateMic.Core;
using NAudio.Wave;

namespace MateMic.Dsp;

/// <summary>
/// 采集环的**水位治理器**：把"采集端比输出端快一点"造成的积压摁回目标水位。
///
/// ## 为什么需要它
///
/// 采集设备与输出设备是**两个独立的 WASAPI 端点**，各有各的晶振，频率永远不可能完全相等。
/// 实测（2026-11-04，用户构建版连续运行 10 小时）：
///   · 采集环水位 0.5 分钟时 16 ms → 599.5 分钟时 **284 ms**；
///   · 折算漂移 **约 7.6 ppm**（采集端每秒多进 0.36 个样本）；
///   · 而链路上没有补偿，水位**只涨不落**，最后靠 <c>DiscardOnBufferOverflow</c> 丢满出的部分。
/// 用户感知就是"跑几个小时延迟越来越大"，而**切换一次输入设备**（重建链路）立刻恢复正常。
///
/// ## 怎么治理
///
/// 漂移消不掉（物理上做不到），但可以让它**不累积** —— 分两段：
///   1. **硬超限一次性纠偏**：水位超过 <see cref="HardLimitMs"/> 时，直接读走多出来的样本
///      （一次性丢弃，会听到一次轻微跳跃；只在严重积压时发生）；
///   2. **慢速维持**：之后按 <see cref="CorrectionPerSecond"/> 持续微调 ——
///      默认每秒丢 0.5 个样本（等效 10.4 ppm 的校正能力，足以覆盖实测的 7.6 ppm）。
///      48 kHz 下一个样本只有 20 微秒，每秒丢半个，听感上不可闻。
///
/// 水位**偏低**时不补静音：采集环配了 <c>ReadFully</c>，数据不足时它自己会补零，
/// 再去插样本只会让节奏更乱。
///
/// 线程：只在音频线程的 Read 里跑，不加锁、不做分配。
/// </summary>
public sealed class CaptureRateGovernor : ISampleProvider
{
    /// <summary>
    /// 目标水位（毫秒）。**实测正常水位只有 10~20 ms**（11-05 连续 8 小时全程 10 ms），
    /// 所以目标贴着它取 20 —— 不人为抬高延迟（这一点是用户 2026-11-05 提的：
    /// 原来定 40/50 等于默认把延迟抬到正常的 2~3 倍，没有道理）。
    /// </summary>
    public const int TargetMs = 20;

    /// <summary>
    /// 硬限（毫秒）：超过它就纠偏回目标。
    ///
    /// 取 40 = 目标 20 + 20 的抖动余量：实测抖动峰值约 30 ms（11-05 监听环最高 30、采集环 10），
    /// 所以正常波动碰不到 40；而真积压（实测出现过 84 ms、284 ms）一定超，必被纠偏。
    ///
    /// 另外要求**连续两块都超限**才动手（见 <see cref="OverLimitBlocksToCorrect"/>），
    /// 避免偶发的一次尖峰被当成积压而白丢一段音频。
    /// </summary>
    public const int HardLimitMs = 40;

    /// <summary>一次性纠偏时保留的余量（毫秒）：纠回 25 ms 左右，别纠到贴住目标。</summary>
    private const int CorrectionMarginMs = 5;

    /// <summary>连续多少块超过硬限才真的纠偏。两块 = 约 40 ms 的持续性，足以区分"尖峰"与"积压"。</summary>
    private const int OverLimitBlocksToCorrect = 2;

    /// <summary>单次纠偏最多丢多少毫秒 —— 大积压分几次丢，听感比一次跳几十毫秒好得多。</summary>
    private const int MaxDropPerCorrectionMs = 20;

    /// <summary>单次 Read 内水位突然上涨超过这个值（毫秒）就记日志 —— 用来抓"消费端停顿"的时刻。</summary>
    private const int SpikeLogMs = 15;

    /// <summary>慢速维持的校正速率（样本/秒）。0.5 ⇒ 10.4 ppm；实测漂移 7.6 ppm，够用。</summary>
    private const double CorrectionPerSecond = 0.5;

    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _source;
    private readonly float[] _scratch = new float[8192];

    /// <summary>累积的"待丢弃样本数"（小数）。攒够 1 个才真的丢一个样本。</summary>
    private double _dropCredit;

    private long _droppedSamples;

    /// <summary>上一次 Read 时看到的水位（毫秒），用于检测"突然抬高"的停顿时刻。负值表示还没采过样。</summary>
    private double _lastLevelMs = -1;

    /// <summary>连续超过硬限的块数（间断即清零）。用来区分"偶发尖峰"和"真积压"。</summary>
    private int _overLimitBlocks;

    public CaptureRateGovernor(WaveFormat format, BufferedWaveProvider buffer)
    {
        WaveFormat = format;
        _buffer = buffer;
        _source = buffer.ToSampleProvider();
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>累计丢弃的样本数（诊断用）。</summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    /// <summary>
    /// 采集环当前积压的毫秒数 —— **这就是"延迟构成"里那项真实存在、但以前没被计入的延迟**。
    ///
    /// 采集端点延迟（20 ms）是设备决定的常量，而这里的积压是**动态**的：
    /// 正常 10~20 ms 可忽略，但一旦消费端被拖慢就会台阶式涨到 84 ms、284 ms 甚至更多，
    /// 且不会自己回落（2026-11-04 实测）。把它显示出来，"延迟变大"才看得见。
    /// </summary>
    public double CurrentLevelMs => _buffer.BufferedDuration.TotalMilliseconds;

    public int Read(Span<float> buffer)
    {
        try
        {
            var toDrop = ComputeDropSamples(buffer.Length);
            // 先把"要丢掉"的样本读进临时区（不交给下游），再读正常的量 ——
            // 顺序不能反：若先读进 buffer 再丢，数据已经给下游了，丢的就变成后面的正常音频。
            while (toDrop > 0)
            {
                var n = Math.Min(toDrop, _scratch.Length);
                var got = _source.Read(_scratch.AsSpan(0, n));
                if (got <= 0) break;
                toDrop -= got;
                Interlocked.Add(ref _droppedSamples, got);
            }
        }
        catch (Exception ex)
        {
            // 治理失败绝不能中断音频：退化成直通
            Log.Warn("[水位治理] 纠偏失败，本块直通：" + ex.Message);
        }

        return _source.Read(buffer);
    }

    /// <summary>按当前水位算出本次要丢弃的样本数（0 表示不丢）。</summary>
    private int ComputeDropSamples(int blockSamples)
    {
        var levelMs = _buffer.BufferedDuration.TotalMilliseconds;

        // 突变检测：水位在两块之间突然抬高，说明刚才消费端被拖慢了
        // （线程抢占 / GC 停顿 / 系统卡顿 / 别的程序抢设备）。
        // 记一条日志把时刻留下来 —— 这是"台阶式积压"的唯一线索，否则事后根本不知道什么时候停的。
        if (_lastLevelMs >= 0 && levelMs - _lastLevelMs > SpikeLogMs)
        {
            Log.Warn($"[水位治理] 水位突增 {levelMs - _lastLevelMs:0} ms（{_lastLevelMs:0} → {levelMs:0} ms）："
                     + $"消费端刚才明显被拖慢过。超过硬限 {HardLimitMs} ms 会被立即纠偏。");
        }

        _lastLevelMs = levelMs;

        // 1) 硬超限：**连续两块都超限**才算积压（偶发尖峰不算，避免白丢音频）；
        //    而且分次纠偏，每次最多丢 20 ms —— 大积压（实测出现过 84、284 ms）分几块丢完，
        //    听感比一次性跳几十毫秒好得多。
        if (levelMs > HardLimitMs)
        {
            _overLimitBlocks++;

            var excessMs = levelMs - TargetMs - CorrectionMarginMs;
            if (_overLimitBlocks >= OverLimitBlocksToCorrect && excessMs > 0)
            {
                var dropMs = Math.Min(excessMs, MaxDropPerCorrectionMs);
                var n = (int)(dropMs * WaveFormat.SampleRate / 1000.0);
                _dropCredit = 0;
                Log.Info($"[水位治理] 采集环水位 {levelMs:0} ms 超过硬限 {HardLimitMs} ms，"
                         + $"丢弃 {n} 个样本（约 {dropMs:0} ms）拉回 {TargetMs} ms 附近"
                         + (excessMs > dropMs ? $"（还差 {excessMs - dropMs:0} ms，下一块继续）。" : "。"));
                return n;
            }
        }
        else
        {
            _overLimitBlocks = 0;
        }

        // 2) 慢速维持：水位略高于目标就慢慢丢，低于目标就停止累积
        var deviationMs = levelMs - TargetMs;
        var budgetPerBlock = CorrectionPerSecond * blockSamples / (double)WaveFormat.SampleRate;

        if (deviationMs > 0)
            _dropCredit += budgetPerBlock;
        else
            _dropCredit = Math.Max(0, _dropCredit - budgetPerBlock);

        if (_dropCredit >= 1.0)
        {
            var n = (int)_dropCredit;
            _dropCredit -= n;
            return n;
        }

        return 0;
    }
}
