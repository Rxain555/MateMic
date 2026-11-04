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
    /// <summary>目标水位（毫秒）：留足余量吸收抖动，又不明显增加延迟。</summary>
    public const int TargetMs = 40;

    /// <summary>硬限（毫秒）：超过它就一次性纠偏回目标。定在 140 —— 既远低于 500 的环容量，又不会频繁触发。</summary>
    public const int HardLimitMs = 140;

    /// <summary>一次性纠偏时保留的余量（毫秒），避免纠偏后立刻又欠载。</summary>
    private const int CorrectionMarginMs = 20;

    /// <summary>慢速维持的校正速率（样本/秒）。0.5 ⇒ 10.4 ppm；实测漂移 7.6 ppm，够用。</summary>
    private const double CorrectionPerSecond = 0.5;

    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _source;
    private readonly float[] _scratch = new float[8192];

    /// <summary>累积的"待丢弃样本数"（小数）。攒够 1 个才真的丢一个样本。</summary>
    private double _dropCredit;

    private long _droppedSamples;

    public CaptureRateGovernor(WaveFormat format, BufferedWaveProvider buffer)
    {
        WaveFormat = format;
        _buffer = buffer;
        _source = buffer.ToSampleProvider();
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>累计丢弃的样本数（诊断用）。</summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

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

        // 1) 硬超限：一次性纠偏到目标（留一点余量，别纠到刚好贴住目标）
        if (levelMs > HardLimitMs)
        {
            var excessMs = levelMs - TargetMs - CorrectionMarginMs;
            if (excessMs > 0)
            {
                var n = (int)(excessMs * WaveFormat.SampleRate / 1000.0);
                _dropCredit = 0;
                Log.Info($"[水位治理] 采集环水位 {levelMs:0} ms 超过硬限 {HardLimitMs} ms，"
                         + $"一次性丢弃 {n} 个样本（约 {excessMs:0} ms）以拉回 {TargetMs} ms。");
                return n;
            }
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
