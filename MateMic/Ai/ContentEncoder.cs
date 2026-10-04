using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MateMic.Core;

namespace MateMic.Ai;

/// <summary>
/// 内容编码器（我们自己从 ContentVec 导出的 ONNX）。
///
/// 与 RVC 对齐的要点（照着 `infer/hubert.py` 复刻）：
///   · 输入是 **16 kHz 原始波形**（不做归一化：这份权重的 preprocessor 里 do_normalize = false）；
///   · 帧率 50 fps（16 kHz 下每帧 20 ms）；
///   · **v2 用 768 维输出**（`last_hidden_state`，即编码器最后一层 LayerNorm 之后），
///     v1 用 `final_proj(hidden_states[9])` 的 256 维；这里默认取 768 维。
///
/// 导出的模型与参考实现实测余弦相似度 1.00000、零帧位移（见工作日志）。
/// </summary>
public sealed class ContentEncoder : IDisposable
{
    /// <summary>特征帧率（Hz）。</summary>
    public const int FrameRate = 50;
    public const int FeatureDim = 768;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public ContentEncoder(string modelPath, bool useGpu = true)
    {
        _session = OnnxSessionFactory.Create(modelPath, useGpu);
        _inputName = _session.InputMetadata.Keys.First();

        // 选 768 维那个输出（v2 用）；没有就退回第一个
        _outputName = _session.OutputMetadata
            .Where(kv => kv.Value.Dimensions.Length == 3 && kv.Value.Dimensions[^1] == FeatureDim)
            .Select(kv => kv.Key)
            .FirstOrDefault() ?? _session.OutputMetadata.Keys.First();
    }

    /// <summary>本块能得到多少帧（16 kHz 输入）。</summary>
    public static int FrameCount(int sampleCount16k) => Math.Max(0, sampleCount16k / 320 - 1);

    /// <summary>
    /// 编码一块 16 kHz 单声道音频，输出 [帧数, 768]，按行优先写入 <paramref name="features"/>。
    /// 返回帧数。
    /// </summary>
    public int Encode(ReadOnlySpan<float> audio16k, float[] features)
    {
        if (audio16k.Length <= 400) return 0;

        var input = new DenseTensor<float>(new[] { 1, audio16k.Length });
        for (var i = 0; i < audio16k.Length; i++) input[0, i] = audio16k[i];

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        var tensor = results.First(r => r.Name == _outputName).AsTensor<float>();

        var frames = tensor.Dimensions[1];
        var dim = tensor.Dimensions[2];
        if (dim != FeatureDim) throw new InvalidOperationException($"编码器输出维度异常：{dim}");

        var index = 0;
        for (var t = 0; t < frames; t++)
            for (var d = 0; d < dim; d++)
                features[index++] = tensor[0, t, d];

        return frames;
    }

    public void Dispose() => _session.Dispose();
}
