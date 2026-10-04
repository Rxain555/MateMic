using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MateMic.Core;

namespace MateMic.Ai;

/// <summary>
/// RVC 音色模型（合成器）的 ONNX 包装。
///
/// 输入约定（与社区 ONNX 音色一致，按名字匹配，缺失时按形状兜底）：
///   feats  [1, T, 768]   float   —— 内容编码器特征（2 倍插值到 100 fps 之后）
///   p_len  [1]           int64   —— 帧数
///   pitch  [1, T]        int64   —— 基频粗量化值（1..255）
///   pitchf [1, T]        float   —— 变换后的基频（Hz）
///   sid    [1]           int64   —— 说话人编号（单音色模型填 0）
/// 输出：audio [N] float，采样率由模型元数据里的 samplingRate 决定（常见 32k/40k/48k）。
/// </summary>
public sealed class RvcSynthesizer : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _featsName;
    private readonly string _pitchName;
    private readonly string _pitchfName;
    private readonly string _pLenName;
    private readonly string _sidName;
    private readonly string _outputName;

    public RvcSynthesizer(string modelPath, bool useGpu = true)
    {
        _session = OnnxSessionFactory.Create(modelPath, useGpu);
        SampleRate = ReadSampleRate(_session);
        SpeakerCount = 1;

        var inputs = _session.InputMetadata;
        _featsName = Match(inputs, "feats") ?? inputs.Keys.First();
        _pLenName = Match(inputs, "p_len") ?? inputs.Keys.First();
        _pitchName = Match(inputs, "pitch") ?? inputs.Keys.First();
        _pitchfName = Match(inputs, "pitchf") ?? inputs.Keys.First();
        _sidName = Match(inputs, "sid") ?? inputs.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();

        Log.Info($"AI：音色模型采样率 {SampleRate} Hz，输入 {string.Join("/", inputs.Keys)}");
    }

    /// <summary>音色模型的输出采样率。</summary>
    public int SampleRate { get; }

    public int SpeakerCount { get; }

    private static string? Match(IReadOnlyDictionary<string, NodeMetadata> inputs, string name)
        => inputs.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>建一个 [1] 的 int64 张量（DenseTensor 的 (int[], bool) 重载很容易被误匹配，这里收口）。</summary>
    private static DenseTensor<long> Scalar(long value)
    {
        var tensor = new DenseTensor<long>(new[] { 1 });
        tensor[0] = value;
        return tensor;
    }
    /// <summary>从 ONNX 自定义元数据里取 samplingRate（VCClient 导出的模型都带这一项）。</summary>
    private static int ReadSampleRate(InferenceSession session)
    {
        try
        {
            if (session.ModelMetadata.CustomMetadataMap.TryGetValue("metadata", out var json) && json != null)
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("samplingRate", out var rate) && rate.TryGetInt32(out var value))
                    return value;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"AI：解析音色模型元数据失败（{ex.Message}），按 40000 Hz 处理");
        }

        return 40000;
    }

    /// <summary>
    /// 合成一块音频。<paramref name="feats"/> 是 [frames, 768] 行优先。
    /// </summary>
    public float[] Synthesize(float[] feats, int frames, long[] pitch, float[] pitchf)
    {
        var featsTensor = new DenseTensor<float>(new[] { 1, frames, ContentEncoder.FeatureDim });
        var index = 0;
        for (var t = 0; t < frames; t++)
            for (var d = 0; d < ContentEncoder.FeatureDim; d++)
                featsTensor[0, t, d] = feats[index++];

        var pitchTensor = new DenseTensor<long>(new[] { 1, frames });
        var pitchfTensor = new DenseTensor<float>(new[] { 1, frames });
        for (var t = 0; t < frames; t++)
        {
            pitchTensor[0, t] = pitch[t];
            pitchfTensor[0, t] = pitchf[t];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_featsName, featsTensor),
            NamedOnnxValue.CreateFromTensor(_pLenName, Scalar((long)frames)),
            NamedOnnxValue.CreateFromTensor(_pitchName, pitchTensor),
            NamedOnnxValue.CreateFromTensor(_pitchfName, pitchfTensor),
            NamedOnnxValue.CreateFromTensor(_sidName, Scalar(0)),
        };

        using var results = _session.Run(inputs);
        var audio = results.First(r => r.Name == _outputName).AsTensor<float>();
        var output = new float[audio.Length];
        var i = 0;
        foreach (var value in audio) output[i++] = value;
        return output;
    }

    public void Dispose() => _session.Dispose();
}
