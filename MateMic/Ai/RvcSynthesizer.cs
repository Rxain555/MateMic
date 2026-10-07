using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MateMic.Ai;

/// <summary>
/// RVC 音色模型（合成器）的 ONNX 包装。
///
/// ⚠ 输入名**因导出方式而异**，必须按名字动态匹配，不能写死：
///   · 社区常见导出：`feats` / `p_len` / `pitch` / `pitchf` / `sid`
///   · 妙音工坊这份：`phone` / `phone_lengths` / `pitch` / `nsff0` / `sid`
/// 两者的语义完全一样（内容特征 / 帧数 / 量化基频 / 变换后基频 / 说话人）。
/// 输出也分两种：只有 `audio`，或者 `audio` + `sr`（采样率运行时给出）。
/// </summary>
public sealed class RvcSynthesizer : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _featName, _lenName, _pitchName, _pitchfName, _sidName, _audioName;
    private readonly string? _srName;

    public int SampleRate { get; private set; } = 40000;

    public RvcSynthesizer(string modelPath, bool useGpu = false)
    {
        var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        if (useGpu) options.AppendExecutionProvider_CUDA(0);
        _session = new InferenceSession(modelPath, options);

        var names = _session.InputMetadata.Keys.ToList();
        _featName = Pick(names, "phone", "feats") ?? names[0];
        _lenName = Pick(names, "phone_lengths", "p_len") ?? names[1];
        _pitchName = Pick(names, "pitch") ?? names[2];
        _pitchfName = Pick(names, "nsff0", "pitchf") ?? names[3];
        _sidName = Pick(names, "sid", "ds") ?? names[4];

        var outputs = _session.OutputMetadata;
        _audioName = outputs.Keys.First();
        _srName = outputs.Keys.Skip(1).FirstOrDefault();

        // 元数据里有 samplingRate 就用它；妙音那份没有元数据，靠运行时输出的 sr
        if (_session.ModelMetadata.CustomMetadataMap.TryGetValue("metadata", out var meta) && meta != null)
        {
            var idx = meta.IndexOf("\"samplingRate\"", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var start = meta.IndexOf(':', idx) + 1;
                var end = meta.IndexOfAny(new[] { ',', '}' }, start);
                if (int.TryParse(meta[start..end].Trim(), out var rate) && rate > 0) SampleRate = rate;
            }
        }
    }

    private static string? Pick(IEnumerable<string> names, params string[] candidates)
        => names.FirstOrDefault(n => candidates.Any(c => n.Equals(c, StringComparison.OrdinalIgnoreCase)));

    /// <summary>合成一段音频。feats 为 [frames, 768] 行优先；返回 40 kHz 等模型采样率的波形。</summary>
    public float[] Synthesize(float[] feats, int frames, long[] pitch, float[] pitchf)
    {
        var featTensor = new DenseTensor<float>(new[] { 1, frames, ContentEncoder.FeatureDim });
        feats.AsSpan(0, frames * ContentEncoder.FeatureDim).CopyTo(featTensor.Buffer.Span);

        var pitchTensor = new DenseTensor<long>(new[] { 1, frames });
        pitch.AsSpan(0, frames).CopyTo(pitchTensor.Buffer.Span);

        var pitchfTensor = new DenseTensor<float>(new[] { 1, frames });
        pitchf.AsSpan(0, frames).CopyTo(pitchfTensor.Buffer.Span);

        var lenTensor = new DenseTensor<long>(new[] { 1 });
        lenTensor[0] = frames;
        var sidTensor = new DenseTensor<long>(new[] { 1 });
        sidTensor[0] = 0;

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(_featName, featTensor),
            NamedOnnxValue.CreateFromTensor(_lenName, lenTensor),
            NamedOnnxValue.CreateFromTensor(_pitchName, pitchTensor),
            NamedOnnxValue.CreateFromTensor(_pitchfName, pitchfTensor),
            NamedOnnxValue.CreateFromTensor(_sidName, sidTensor),
        };

        using var results = _session.Run(inputs);
        var audio = results.First(r => r.Name == _audioName).AsTensor<float>().ToArray();

        if (_srName != null)
        {
            var sr = results.First(r => r.Name == _srName).AsTensor<long>();
            if (sr.Length > 0 && sr[0] > 0) SampleRate = (int)sr[0];
        }

        return audio;
    }

    public void Dispose() => _session.Dispose();
}
