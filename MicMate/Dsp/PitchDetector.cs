using System;

namespace MicMate.Dsp;

/// <summary>
/// 基频检测器（自相关法）。用于硬调音：每帧估出人声基频，再吸附到音阶。
///
/// 采用归一化自相关 + 抛物线插值，对单声道人声足够稳；
/// 通过"能量阈值 + 清晰度阈值"判定有声/无声，避免在静音或清音段乱吸音高。
/// </summary>
public sealed class PitchDetector
{
    private readonly int _sampleRate;
    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly float[] _window;
    private readonly float[] _buffer;

    private float _previousPitch;

    public PitchDetector(int sampleRate, float minFrequency = 70f, float maxFrequency = 1000f, int windowSize = 1024)
    {
        _sampleRate = sampleRate;
        _minLag = Math.Max(2, (int)(sampleRate / maxFrequency));
        _maxLag = Math.Min(windowSize / 2, (int)(sampleRate / minFrequency));
        _window = new float[windowSize];
        _buffer = new float[windowSize];

        for (var i = 0; i < windowSize; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / windowSize));
    }

    public int WindowSize => _window.Length;

    /// <summary>最近一次检测的清晰度（0–1），用于判断结果是否可信。</summary>
    public float LastClarity { get; private set; }

    /// <summary>
    /// 检测一段样本的基频。返回 0 表示未检测到可靠的浊音（静音/清音）。
    /// </summary>
    public float Detect(ReadOnlySpan<float> samples)
    {
        var n = Math.Min(samples.Length, _buffer.Length);
        if (n < _maxLag * 2) return 0f;

        // 去均值 + 加窗
        float mean = 0;
        for (var i = 0; i < n; i++) mean += samples[i];
        mean /= n;

        double energy = 0;
        for (var i = 0; i < n; i++)
        {
            var v = (samples[i] - mean) * _window[i];
            _buffer[i] = v;
            energy += v * v;
        }

        var rms = Math.Sqrt(energy / n);
        if (rms < 0.0035)   // 约 −49 dBFS，低于此视为静音
        {
            LastClarity = 0f;
            return 0f;
        }

        var bestLag = -1;
        var bestCorrelation = 0f;

        // 先算一遍自相关，取全局峰值
        var correlations = new float[_maxLag + 1];
        for (var lag = _minLag; lag <= _maxLag; lag++)
        {
            var correlation = Correlation(lag, n);
            correlations[lag] = correlation;
            if (correlation > bestCorrelation) bestCorrelation = correlation;
        }

        if (bestCorrelation < 0.5f)
        {
            LastClarity = bestCorrelation;
            return 0f;
        }

        // 在"足够高"的峰里挑一个作为周期。
        //
        // 两个原则：
        //   1) 只考虑与全局峰值差距在阈值内的峰——排除噪声引起的偶然小峰
        //   2) 这些峰里**优先取最长的周期**（即最低的基频）：
        //      周期信号在周期的整数倍处都有高相关，取最短周期会误取谐波，
        //      导致音高被识别成高八度（实测 228 Hz 被识别错，调音因此不生效）。
        //      取最长周期天然得到基频。
        var threshold = bestCorrelation * 0.95f;
        int? chosen = null;
        for (var lag = _maxLag; lag >= _minLag + 1; lag--)
        {
            if (correlations[lag] < threshold) continue;
            if (correlations[lag] < correlations[lag - 1]) continue;
            if (lag + 1 <= _maxLag && correlations[lag] < correlations[lag + 1]) continue;

            chosen = lag;
            bestCorrelation = correlations[lag];
        }

        if (chosen == null) return 0f;
        bestLag = chosen.Value;

        // 抛物线插值提高精度
        var refined = (double)bestLag;
        if (bestLag > _minLag && bestLag < _maxLag)
        {
            var y0 = Correlation(bestLag - 1, n);
            var y1 = bestCorrelation;
            var y2 = Correlation(bestLag + 1, n);
            var denominator = y0 - 2 * y1 + y2;
            if (Math.Abs(denominator) > 1e-9)
                refined += 0.5 * (y0 - y2) / denominator;
        }

        var pitch = (float)(_sampleRate / refined);
        if (pitch is < 60f or > 1200f) return 0f;

        // 与上一帧做平滑，避免八度跳变造成的刺耳抖动
        if (_previousPitch > 0)
        {
            var ratio = pitch / _previousPitch;
            if (ratio is > 1.9f or < 0.53f) pitch = _previousPitch;   // 疑似八度错误
            else pitch = 0.6f * pitch + 0.4f * _previousPitch;
        }

        _previousPitch = pitch;
        return pitch;
    }

    private float Correlation(int lag, int n)
    {
        double sum = 0, energyA = 0, energyB = 0;
        var count = n - lag;
        for (var i = 0; i < count; i++)
        {
            var a = _buffer[i];
            var b = _buffer[i + lag];
            sum += a * b;
            energyA += a * a;
            energyB += b * b;
        }

        var denominator = Math.Sqrt(energyA * energyB);
        return denominator < 1e-9 ? 0f : (float)(sum / denominator);
    }

    public void Reset() => _previousPitch = 0f;
}
