using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MateMic.Ai;

/// <summary>
/// 内容编码器（自导出的 ContentVec ONNX）。
///
/// 与 RVC 对齐的要点（照 `infer/hubert.py` 复刻）：
///   · 输入是 **16 kHz 原始波形**，**不做归一化**（这份权重的 preprocessor 里 do_normalize = false）
///   · 帧率 50 fps（16 kHz 下每帧 20 ms）
///   · **v2 用 768 维输出**（`last_hidden_state`，即 unit12），v1 用 final_proj(hidden[9]) 的 256 维
/// </summary>
public sealed class ContentEncoder : IDisposable
{
    public const int FeatureDim = 768;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public ContentEncoder(string modelPath, bool useGpu = false)
    {
        var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        // 按当前生效的运算后端挂 provider（CUDA / DirectML / CPU）——
        // 不能无条件挂 CUDA：CPU 与 DirectML 版的 onnxruntime 没有 CUDA，会抛异常导致引擎建不起来。
        OrtProviders.Append(options);
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();

        // 取最后一维为 768 的那个输出（v2）
        _outputName = _session.OutputMetadata
            .Where(kv => kv.Value.Dimensions.Length == 3 && kv.Value.Dimensions[^1] == FeatureDim)
            .Select(kv => kv.Key)
            .FirstOrDefault() ?? _session.OutputMetadata.Keys.First();
    }

    public string OutputName => _outputName;

    /// <summary>对整段 16 kHz 单声道音频编码，返回 [帧数, 768] 行优先数组。</summary>
    public (float[] Features, int Frames) Encode(ReadOnlySpan<float> audio16k)
    {
        var input = new DenseTensor<float>(new[] { 1, audio16k.Length });
        var span = input.Buffer.Span;
        audio16k.CopyTo(span);

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        var tensor = results.First(r => r.Name == _outputName).AsTensor<float>();
        var frames = tensor.Dimensions[1];
        var dim = tensor.Dimensions[2];
        if (dim != FeatureDim) throw new InvalidOperationException($"编码器输出维度异常：{dim}");

        var features = new float[frames * dim];
        var flat = tensor.ToArray();
        Array.Copy(flat, features, features.Length);
        return (features, frames);
    }

    public void Dispose() => _session.Dispose();
}
