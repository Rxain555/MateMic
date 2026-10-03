using System;

namespace MateMic.Dsp;

/// <summary>
/// 稳定的基频周期跟踪器（**专供 PSOLA 使用**）。
///
/// 为什么不直接复用 <see cref="PitchDetector"/>：那个检测器是给电音做"音阶吸附"用的，
/// 目标是"把音高吸到最近的音级上"，因此偶尔估错八度、或在兜底值与真值之间摆动都无所谓
/// —— 反正都要被吸附走。但对 PSOLA 是致命的：**每个分析窗都要对齐到声门脉冲**，
/// 周期只要错一点，脉冲就会逐窗漂移、重叠相加互相抵消。
/// 实测表现就是音高不准（+7 半音偏低 5.5%、−5 半音几乎没变），甚至输出成噪声。
///
/// 三条稳定性措施：
///   1. **倍频纠错**：自相关在真实周期的整数倍处都有峰，若 τ/2、τ/3 处也足够强就取小的那个，
///      避免把 220 Hz 认成 110 Hz（这是自相关法最典型的错法）；
///   2. **中值平滑**：对最近 5 次估计取中值，单次异常值不影响输出；
///   3. **跳变门限**：除非连续两次都指向新值，否则拒绝超过 ±35% 的突变。
///
/// 性能：先把 48 kHz 窗降采样 2 倍再做自相关，乘法量降到 1/4（约 2.5 万次/次估计），
/// 且**调用方控制频率**（建议每 512 个样本一次，约 5% 实时预算）。
/// </summary>
public sealed class PitchTracker
{
    /// <summary>48 kHz 下的分析窗长（约 43 ms）。必须 ≥ 2 × 最大 lag 才能覆盖最低频。</summary>
    public const int WindowSize = 2048;

    /// <summary>降采样倍数（只做 2 点平均，等效一个粗低通，测基频足够）。</summary>
    private const int Decimation = 2;

    private const int MinFrequency = 60;
    private const int MaxFrequency = 500;
    private const float ClarityThreshold = 0.45f;
    private const float JumpLimit = 0.35f;
    private const int HistoryLength = 5;

    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly float[] _decimated = new float[WindowSize / Decimation];
    private readonly float[] _corr;
    private readonly float[] _history = new float[HistoryLength];

    private int _historyCount;
    private int _historyIndex;
    private int _jumpCount;
    private float _period = -1f;

    public PitchTracker(int sampleRate)
    {
        var decimatedRate = sampleRate / Decimation;
        _minLag = Math.Max(2, decimatedRate / MaxFrequency);
        _maxLag = Math.Min(_decimated.Length - 2, decimatedRate / MinFrequency);
        _corr = new float[_maxLag - _minLag + 1];
    }

    /// <summary>当前周期（**原始采样率**下的样本数）。&lt;= 0 表示还没得到可靠值。</summary>
    public float Period => _period;

    public bool HasPeriod => _period > 0;

    /// <summary>最近一次估计的清晰度（归一化自相关峰值），0–1。</summary>
    public float Clarity { get; private set; }

    public void Reset()
    {
        Array.Clear(_history);
        _historyCount = 0;
        _historyIndex = 0;
        _jumpCount = 0;
        _period = -1f;
        Clarity = 0;
    }

    /// <summary>
    /// 喂入一段音频（长度必须 ≥ <see cref="WindowSize"/>，只用最后 WindowSize 个样本）。
    /// 返回 true 表示这次得到了一个可靠的周期更新。
    /// </summary>
    public bool Feed(ReadOnlySpan<float> samples)
    {
        if (samples.Length < WindowSize) return false;

        var source = samples[^WindowSize..];

        // 1) 去均值 + 降采样
        double mean = 0;
        for (var i = 0; i < WindowSize; i++) mean += source[i];
        mean /= WindowSize;

        for (var i = 0; i < _decimated.Length; i++)
        {
            var a = source[i * Decimation] - mean;
            var b = source[i * Decimation + 1] - mean;
            _decimated[i] = (float)((a + b) * 0.5);
        }

        // 2) 归一化自相关。**有先验周期时只在先验附近搜**：
        //    颤音会让自相关峰变宽、还会冒出假峰，全局搜索容易跳到假峰上
        //    （实测：180 Hz + 5 Hz 颤音被估成 245 Hz，误差 36%）。
        //    限制在 ±40% 内既能跟上颤音，又天然防止跑飞；搜不到足够强的峰再退回全局搜。
        Array.Clear(_corr);
        var (best, bestLag) = SearchCorrelation(_period / Decimation, restrict: _period > 0);
        if (best < ClarityThreshold && _period > 0)
            (best, bestLag) = SearchCorrelation(0, restrict: false);   // 先验失效 → 全局重找

        Clarity = best;
        if (best < ClarityThreshold || bestLag == 0) return false;   // 清音 / 静音


        // 4) 抛物线插值细化到亚样本精度
        var refined = Interpolate(bestLag);

        // 5) 中值平滑
        var period = (float)(refined * Decimation);
        _history[_historyIndex] = period;
        _historyIndex = (_historyIndex + 1) % HistoryLength;
        if (_historyCount < HistoryLength) _historyCount++;

        var median = Median();
        if (!(median > 0)) return false;

        // 6) 跳变门限：连续两次指向新值才接受
        if (_period > 0 && Math.Abs(median - _period) / _period > JumpLimit)
        {
            _jumpCount++;
            if (_jumpCount < 2) return false;
        }

        _jumpCount = 0;
        _period = median;
        return true;
    }

    /// <summary>
    /// 做归一化自相关并返回峰值与对应 lag。restrict=true 时只在先验周期 ±40% 内搜，
    /// 用于压制颤音/噪声造成的假峰；否则全范围搜（首次估计或先验失效时的兜底）。
    /// </summary>
    private (float Best, int Lag) SearchCorrelation(double priorLag, bool restrict)
    {
        var lower = _minLag;
        var upper = _maxLag;
        if (restrict && priorLag > 0)
        {
            lower = Math.Max(_minLag, (int)(priorLag * 0.6));
            upper = Math.Min(_maxLag, (int)(priorLag * 1.4));
        }

        // (a) 先把范围内的归一化相关全算出来
        var rMax = 0f;
        for (var lag = lower; lag <= upper; lag++)
        {
            double sum = 0, energyA = 0, energyB = 0;
            for (var i = 0; i + lag < _decimated.Length; i++)
            {
                sum += _decimated[i] * _decimated[i + lag];
                energyA += _decimated[i] * _decimated[i];
                energyB += _decimated[i + lag] * _decimated[i + lag];
            }

            var denominator = Math.Sqrt(energyA * energyB);
            var r = denominator > 1e-9 ? (float)(sum / denominator) : 0f;
            _corr[lag - _minLag] = r;
            if (r > rMax) rMax = r;
        }

        if (rMax <= 0) return (0f, 0);

        // (b) 取"**从最小 lag 起**第一个足够强的局部峰"，而不是全局最大：
        //     自相关在真实周期的整数倍处都有同样高的峰，取最大容易落到 2 倍/3 倍周期上
        //     （实测：220 Hz 的谐波音被锁成约 1/3 频率，输出低了近两个八度）。
        //     取最小的那个强峰才对应基频。
        var threshold = rMax * 0.9f;
        for (var lag = lower + 1; lag < upper; lag++)
        {
            var r = _corr[lag - _minLag];
            if (r < threshold) continue;
            if (r >= _corr[lag - 1 - _minLag] && r >= _corr[lag + 1 - _minLag])
                return (r, lag);
        }

        // (c) 兜底：全局最大
        for (var lag = lower; lag <= upper; lag++)
            if (_corr[lag - _minLag] >= rMax) return (rMax, lag);

        return (rMax, lower);
    }

    /// <summary>取某个 lag 附近（±1）的相关峰值。</summary>
    private float LocalPeak(int lag)
    {
        var index = lag - _minLag;
        var best = 0f;
        for (var i = Math.Max(0, index - 1); i <= Math.Min(_corr.Length - 1, index + 1); i++)
            if (_corr[i] > best) best = _corr[i];

        return best;
    }

    /// <summary>对相关峰做抛物线插值，返回小数 lag。</summary>
    private double Interpolate(int lag)
    {
        var index = lag - _minLag;
        if (index <= 0 || index >= _corr.Length - 1) return lag;

        var y0 = _corr[index - 1];
        var y1 = _corr[index];
        var y2 = _corr[index + 1];
        var denominator = y0 - 2 * y1 + y2;
        if (Math.Abs(denominator) < 1e-6) return lag;

        var offset = 0.5 * (y0 - y2) / denominator;
        return lag + Math.Clamp(offset, -0.5, 0.5);
    }

    private float Median()
    {
        if (_historyCount == 0) return 0;

        Span<float> temp = stackalloc float[HistoryLength];
        _history.AsSpan(0, _historyCount).CopyTo(temp);
        temp[.._historyCount].Sort();
        return temp[_historyCount / 2];
    }
}
