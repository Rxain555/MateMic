using System;

namespace MateMic.Denoise;

/// <summary>
/// 任意采样率之间的线性插值重采样器（流式、保状态）。
///
/// 用途：本软件处理链固定 48 kHz，而降噪模型生态主流是 16 kHz
/// （RNNoise / GTCRN / DPDFNet 的 16 kHz 版本）。
/// 有了这层，模型可以与处理链采样率不同：先降到模型采样率推理，再升回 48 kHz。
///
/// 实现说明：用线性插值而不是多相 FIR——降噪模型的输入本来就被网络重新加权，
/// 降采样带来的轻微混叠影响远小于换个模型带来的差别；线性插值在流式场景下
/// 也更容易保持相位连续（不引入额外延迟与块边界不连续）。
/// 若将来需要更高质量，可换成 NWaves 的 <c>Resampler</c>（离线整段）或多相 FIR。
/// </summary>
public sealed class RateConverter
{
    private readonly double _ratio;          // 输入采样率 / 输出采样率
    private readonly int _maxOutput;
    private readonly float[] _inputBuffer;
    private readonly float[] _pending;

    private int _pendingCount;
    private double _position;

    /// <param name="inputRate">输入采样率（源）。</param>
    /// <param name="outputRate">输出采样率（目标）。</param>
    /// <param name="maxInput">单次处理的最大输入样本数。</param>
    public RateConverter(int inputRate, int outputRate, int maxInput)
    {
        _ratio = (double)inputRate / outputRate;
        _maxOutput = (int)Math.Ceiling(maxInput / _ratio) + 2;
        _inputBuffer = new float[maxInput + 4];
        _pending = new float[maxInput + 4];

        InputRate = inputRate;
        OutputRate = outputRate;
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public bool IsBypassed => InputRate == OutputRate;

    /// <summary>把输入重采样为输出。返回写入 <paramref name="destination"/> 的样本数。</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> destination)
    {
        if (IsBypassed)
        {
            var copy = Math.Min(input.Length, destination.Length);
            input[..copy].CopyTo(destination);
            return copy;
        }

        // 1) 把上一次的余量 + 新输入拼成连续序列
        var total = 0;
        for (var i = 0; i < _pendingCount && total < _inputBuffer.Length; i++)
            _inputBuffer[total++] = _pending[i];

        foreach (var sample in input)
        {
            if (total >= _inputBuffer.Length) break;
            _inputBuffer[total++] = sample;
        }

        // 2) 线性插值读出
        var produced = 0;
        while (produced < destination.Length && produced < _maxOutput)
        {
            var index = (int)_position;
            if (index + 1 >= total) break;   // 数据不够，留待下次

            var fraction = (float)(_position - index);
            destination[produced++] = _inputBuffer[index] +
                                      (_inputBuffer[index + 1] - _inputBuffer[index]) * fraction;
            _position += _ratio;
        }

        // 3) 丢弃已消费部分，保留余量供下次使用
        var consumed = (int)_position;
        _pendingCount = 0;
        for (var i = consumed; i < total && _pendingCount < _pending.Length; i++)
            _pending[_pendingCount++] = _inputBuffer[i];

        _position -= consumed;
        return produced;
    }

    public void Reset()
    {
        _pendingCount = 0;
        _position = 0;
    }
}
