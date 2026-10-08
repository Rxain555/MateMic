using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MateMic.Ai;

/// <summary>
/// RMVPE 音高提取（对照官方 `infer/rmvpe.py` 复刻）。
///
/// 与 CREPE 的关键差别在**解码**：
///   CREPE 取单个 argmax bin（分辨率只有 20 cents）
///   RMVPE 以 argmax 为中心，在 ±4 个 bin 上做**加权平均**（to_local_average_cents）
///   ⇒ 实测逐帧抖动从 20.0 cents 降到 3.9 cents
///
/// 注意两个坑：
///   · U-Net 有 5 级下采样，输入长度必须是 **32 的倍数**，要 pad 后推理再裁回，
///     否则报 "Concat ... Axis 2 has mismatched dimensions"
///   · 无声帧输出的是 **10 Hz**（cents=0 → 10·2^0），不是 0。
///     下游按 `f0 == 0` 判无声会永远失败 —— 官方 pipeline 里那两句保护代码因此是死代码。
/// </summary>
public sealed class RmvpeF0 : IDisposable
{
    private const double CentsOffset = 1997.3794084376191;
    private const int Bins = 360;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly MelSpectrogram _mel = new();

    public RmvpeF0(string modelPath, bool useGpu = false)
    {
        var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        // 按当前生效的运算后端挂 provider（CUDA / DirectML / CPU）——
        // 不能无条件挂 CUDA：CPU 与 DirectML 版的 onnxruntime 没有 CUDA，会抛异常导致引擎建不起来。
        OrtProviders.Append(options);
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>返回 f0（Hz；无声处约 10 Hz），帧率 100 fps。同时返回 log-mel 便于比对。</summary>
    public (float[] F0, float[] Mel, int MelFrames) Extract(ReadOnlySpan<float> audio16k, float thred = 0.03f)
    {
        var (mel, frames) = _mel.Compute(audio16k);

        // pad 到 32 的倍数
        var nPad = 32 * ((frames - 1) / 32 + 1) - frames;
        var paddedFrames = frames + nPad;

        var input = new DenseTensor<float>(new[] { 1, MelSpectrogram.NMels, paddedFrames });
        var span = input.Buffer.Span;
        for (var m = 0; m < MelSpectrogram.NMels; m++)
            for (var f = 0; f < frames; f++)
                span[m * paddedFrames + f] = mel[m * frames + f];      // 其余为 0

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        var salience = results.First(r => r.Name == _outputName).AsTensor<float>();
        var outFrames = salience.Dimensions[1];                        // = paddedFrames

        var f0 = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            // to_local_average_cents：以 argmax 为中心，在 ±4 个 bin 上加权平均
            var best = 0;
            var bestValue = float.MinValue;
            for (var b = 0; b < Bins; b++)
            {
                var v = salience[0, f, b];
                if (v > bestValue) { bestValue = v; best = b; }
            }

            double productSum = 0, weightSum = 0;
            for (var d = -4; d <= 4; d++)
            {
                var b = best + d;
                if (b < 0 || b >= Bins) continue;
                var s = salience[0, f, b];
                var cents = CentsOffset + 20.0 * b;
                productSum += s * cents;
                weightSum += s;
            }

            var centsOut = weightSum > 0 ? productSum / weightSum : 0.0;
            f0[f] = bestValue <= thred ? 10f : (float)(10.0 * Math.Pow(2.0, centsOut / 1200.0));
            // 注：官方对低于阈值的帧给 cents=0，于是 f0 = 10·2^0 = 10 Hz（不是 0）
        }

        return (f0, mel, frames);
    }

    public void Dispose() => _session.Dispose();
}
