using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MateMic.Ai;

/// <summary>
/// 基频提取（CREPE-tiny 的 ONNX 版本，约 1.9 MB）。
///
/// 与 RVC 对齐的要点：
///   · RVC 每帧 160 个样本（16 kHz 下 10 ms），基频也按 10 ms 取，所以跳步 = 160；
///   · CREPE 输出的 360 个值是"cents 分箱"：第 i 箱对应
///     cents = 1997.3794084376191 + 20 * i，频率 = 10 * 2^(cents / 1200)；
///   · 置信度低于阈值按无声处理（返回 0），由调用方插值补齐（RVC 就是这么做的）。
/// </summary>
public sealed class F0Extractor : IDisposable
{
    private const int FrameSize = 1024;
    private const int Hop = 160;
    private const double CentsOffset = 1997.3794084376191;
    private const double CentsPerBin = 20.0;

    private readonly InferenceSession _session;
    private readonly float _threshold;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly float[] _frame = new float[FrameSize];

    public F0Extractor(string modelPath, float threshold = 0.5f, bool useGpu = true)
    {
        _threshold = threshold;
        _session = OnnxSessionFactory.Create(modelPath, useGpu);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>每帧跳步（样本）。基频结果与 RVC 一样是 100 fps。</summary>
    public static int FrameHop => Hop;

    /// <summary>本块能得到多少帧。</summary>
    public static int FrameCount(int sampleCount)
        => sampleCount < FrameSize ? 0 : (sampleCount - FrameSize) / Hop + 1;

    /// <summary>
    /// 对 16 kHz 单声道提取基频。写入 <paramref name="f0"/>（Hz，无声处为 0）与
    /// <paramref name="confidence"/>（可为 null）。返回帧数。
    /// </summary>
    public int Extract(ReadOnlySpan<float> audio, Span<float> f0, Span<float> confidence = default)
    {
        var frames = FrameCount(audio.Length);
        if (frames <= 0) return 0;

        var input = new DenseTensor<float>(new[] { frames, FrameSize });
        for (var f = 0; f < frames; f++)
        {
            var start = f * Hop;
            for (var i = 0; i < FrameSize; i++) input[f, i] = audio[start + i];
        }

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        var probabilities = results.First(r => r.Name == _outputName).AsTensor<float>();

        var bins = probabilities.Dimensions[1];
        for (var f = 0; f < frames; f++)
        {
            var best = 0;
            var bestValue = float.MinValue;
            for (var b = 0; b < bins; b++)
            {
                var value = probabilities[f, b];
                if (value > bestValue)
                {
                    bestValue = value;
                    best = b;
                }
            }

            var cents = CentsOffset + CentsPerBin * best;
            var hz = (float)(10.0 * Math.Pow(2.0, cents / 1200.0));
            var voiced = bestValue >= _threshold;

            f0[f] = voiced ? hz : 0f;
            if (confidence.Length >= frames) confidence[f] = bestValue;
        }

        return frames;
    }

    public void Dispose() => _session.Dispose();
}
