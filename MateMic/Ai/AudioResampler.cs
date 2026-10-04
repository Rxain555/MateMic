namespace MateMic.Ai;

/// <summary>
/// 有状态的线性重采样器。两个用途：
///   · 48 kHz 采集 → 16 kHz（内容编码器与基频模型要 16 kHz）
///   · 音色模型输出（这个音色是 40 kHz）→ 48 kHz（处理链的采样率）
///
/// **状态必须跨块保留**：每块各自重采样会在块边界产生相位跳变，听感就是周期性的咔哒声。
/// 降采样前先过低通（窗函数 sinc FIR），避免高频折叠；升采样直接线性插值，够用。
/// </summary>
public sealed class AudioResampler
{
    private readonly double _ratio;          // 输入采样率 / 输出采样率
    private readonly float[] _fir;           // 降采样低通（升采样时为空）
    private readonly float[] _rawHistory;    // FIR 需要的历史原始样本
    private int _historyCount;

    private float _previous;                 // 上一个（滤波后的）输入样本
    private bool _hasPrevious;
    private double _fraction;                // 下一个输出点相对当前输入样本的位置

    /// <param name="inputRate">输入采样率。</param>
    /// <param name="outputRate">输出采样率。</param>
    /// <param name="taps">低通阶数（降采样时生效）。</param>
    public AudioResampler(int inputRate, int outputRate, int taps = 63)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        _ratio = (double)inputRate / outputRate;

        if (_ratio > 1.001)
        {
            if (taps % 2 == 0) taps++;
            _fir = DesignLowPass(taps, 0.45 * outputRate / inputRate);
            _rawHistory = new float[taps - 1];
        }
        else
        {
            _fir = Array.Empty<float>();
            _rawHistory = Array.Empty<float>();
        }
    }

    /// <summary>本块的输出样本数上限（调用方按它准备缓冲）。</summary>
    public int MaxOutput(int inputCount) => (int)Math.Ceiling(inputCount / _ratio) + 4;

    /// <summary>
    /// 处理一块输入，输出写到 <paramref name="output"/>。返回实际输出样本数。
    /// </summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var produced = 0;
        var taps = _fir.Length;
        var half = taps / 2;

        for (var i = 0; i < input.Length; i++)
        {
            var raw = input[i];

            // 1) 滤波（降采样时）；滤波后的样本作为插值节点
            float current;
            if (taps > 0)
            {
                current = 0f;
                // 历史（最早的在前）与当前样本一起卷积
                for (var k = 0; k < taps - 1; k++)
                    current += _fir[k] * _rawHistory[k];
                current += _fir[taps - 1] * raw;

                // 历史整体前移一位
                for (var k = 0; k < taps - 2; k++) _rawHistory[k] = _rawHistory[k + 1];
                _rawHistory[taps - 2] = raw;
            }
            else
            {
                current = raw;
            }

            // 2) 分数位置线性插值
            if (!_hasPrevious)
            {
                _previous = current;
                _hasPrevious = true;
                _fraction = 0;
            }

            _fraction -= 1.0;
            while (_fraction <= 0.0 && produced < output.Length)
            {
                var t = (float)(1.0 + _fraction);       // _fraction ∈ (-1, 0] → t ∈ (0, 1]
                output[produced++] = _previous + (current - _previous) * t;
                _fraction += _ratio;
            }

            _previous = current;
        }

        return produced;
    }

    /// <summary>重置状态（切换设备/模型时调用）。</summary>
    public void Reset()
    {
        Array.Clear(_rawHistory);
        _historyCount = 0;
        _hasPrevious = false;
        _fraction = 0;
    }

    /// <summary>窗函数 sinc 低通（Hamming 窗），截止频率按输入采样率归一化。</summary>
    private static float[] DesignLowPass(int taps, double cutoffNormalized)
    {
        var fir = new float[taps];
        var center = (taps - 1) / 2.0;
        double sum = 0;
        for (var i = 0; i < taps; i++)
        {
            var n = i - center;
            var sinc = n == 0 ? 2 * cutoffNormalized : Math.Sin(2 * Math.PI * cutoffNormalized * n) / (Math.PI * n);
            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1));
            fir[i] = (float)(sinc * window);
            sum += fir[i];
        }

        // 归一化到直流增益 1
        for (var i = 0; i < taps; i++) fir[i] = (float)(fir[i] / sum);
        return fir;
    }
}
