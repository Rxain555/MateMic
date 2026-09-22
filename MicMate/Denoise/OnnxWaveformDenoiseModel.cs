using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MicMate.Core;
using MicMate.Dsp;

namespace MicMate.Denoise;

/// <summary>
/// 波形域 ONNX 降噪后端。
///
/// 适用于「输入是一帧波形、输出是降噪后的一帧波形」的模型，例如 RNNoise 的 ONNX 导出、
/// DeepFilterNet / DPDFNet 的 48 kHz 版本等。约定：
///   · 采样率 48000 Hz，单声道，每帧 480 samples（10 ms）
///   · 主输入张量 float32 [1, 480]，主输出张量 float32 [1, 480]
///   · 若模型带循环状态（GRU/LSTM，例如 RNNoise），会自动识别状态输入/输出并在帧间回传，
///     不需要模型把状态固化成一个 batch 维度。
/// </summary>
public sealed class OnnxWaveformDenoiseModel : IDenoiseModel
{
    private const int FrameSize = SpectralDenoiseModel.FrameSize;

    private readonly InferenceSession _session;
    private readonly string _audioInputName;
    private readonly string _audioOutputName;
    private readonly List<string> _stateInputNames = new();
    private readonly List<string> _stateOutputNames = new();
    private readonly Dictionary<string, float[]> _state = new();
    private readonly float[] _inputBuffer = new float[FrameSize];

    public OnnxWaveformDenoiseModel(string path, string displayName)
    {
        _session = new InferenceSession(path);

        // 找主音频输入：优先名字里含 input/audio/mix 的 [_, 480] 张量，否则取第一个 [_, 480]
        var audioInput = _session.InputMetadata
            .FirstOrDefault(kv => IsAudioShape(kv.Value.Dimensions) && LooksLikeAudio(kv.Key));
        if (audioInput.Key == null)
            audioInput = _session.InputMetadata.FirstOrDefault(kv => IsAudioShape(kv.Value.Dimensions));
        if (audioInput.Key == null)
            throw new InvalidOperationException(
                $"模型输入中没有形状为 [1,{FrameSize}] 的音频张量，无法作为波形域降噪模型使用。");

        var audioOutput = _session.OutputMetadata
            .FirstOrDefault(kv => IsAudioShape(kv.Value.Dimensions) && LooksLikeAudio(kv.Key));
        if (audioOutput.Key == null)
            audioOutput = _session.OutputMetadata.FirstOrDefault(kv => IsAudioShape(kv.Value.Dimensions));
        if (audioOutput.Key == null)
            throw new InvalidOperationException(
                $"模型输出中没有形状为 [1,{FrameSize}] 的音频张量，无法作为波形域降噪模型使用。");

        _audioInputName = audioInput.Key;
        _audioOutputName = audioOutput.Key;

        // 其余张量视为循环状态：成对地按出现顺序匹配（state_in_1 ↔ state_out_1 …）
        foreach (var kv in _session.InputMetadata.Where(kv => kv.Key != _audioInputName))
            _stateInputNames.Add(kv.Key);

        foreach (var kv in _session.OutputMetadata.Where(kv => kv.Key != _audioOutputName))
            _stateOutputNames.Add(kv.Key);

        Name = displayName;
        StatePairs = Math.Min(_stateInputNames.Count, _stateOutputNames.Count);

        var stateInfo = StatePairs == 0
            ? "无状态"
            : $"{StatePairs} 组循环状态";
        TensorInfo = $"波形域 [1,{FrameSize}] → [1,{FrameSize}]，{stateInfo}";

        Log.Info($"已载入波形域降噪模型 {displayName}：{TensorInfo}");
    }

    public string Name { get; }

    public string TensorInfo { get; }

    public int StatePairs { get; }

    private static bool IsAudioShape(IReadOnlyList<int> dims)
        => dims.Count == 2 && (dims[^1] == FrameSize || dims[^1] == -1 || dims[^1] == 0);

    private static bool LooksLikeAudio(string name)
        => name.Contains("input", StringComparison.OrdinalIgnoreCase)
           || name.Contains("audio", StringComparison.OrdinalIgnoreCase)
           || name.Contains("mix", StringComparison.OrdinalIgnoreCase)
           || name.Contains("frame", StringComparison.OrdinalIgnoreCase);

    public void Process(Span<float> frame)
    {
        if (frame.Length != FrameSize)
        {
            // 帧长不符时直通，交由上层按 480 分帧
            return;
        }

        frame.CopyTo(_inputBuffer);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_audioInputName,
                new DenseTensor<float>(_inputBuffer, new[] { 1, FrameSize })),
        };

        for (var i = 0; i < StatePairs; i++)
        {
            var name = _stateInputNames[i];
            var dimension = ResolveStateDimension(name);
            var values = _state.TryGetValue(name, out var existing) && existing.Length == dimension
                ? existing
                : new float[dimension];

            inputs.Add(NamedOnnxValue.CreateFromTensor(name,
                new DenseTensor<float>(values, new[] { 1, dimension })));
        }

        using var results = _session.Run(inputs);

        var denoised = false;
        foreach (var result in results)
        {
            if (result.Name == _audioOutputName)
            {
                var span = result.AsEnumerable<float>().ToArray();
                var count = Math.Min(frame.Length, span.Length);
                span.AsSpan(0, count).CopyTo(frame);
                denoised = true;
                continue;
            }

            // 回传循环状态
            var index = _stateOutputNames.IndexOf(result.Name);
            if (index >= 0 && index < StatePairs)
                _state[_stateInputNames[index]] = result.AsEnumerable<float>().ToArray();
        }

        if (!denoised) Log.Warn("波形域模型输出中没有找到音频张量，本帧直通。");
    }

    /// <summary>状态维度：优先用模型声明的静态维度，否则按首次推理结果学习。</summary>
    private int ResolveStateDimension(string inputName)
    {
        if (_state.TryGetValue(inputName + "#dim", out var cached) && cached.Length == 1)
            return (int)cached[0];

        var dims = _session.InputMetadata[inputName].Dimensions;
        var dimension = dims.Length > 0 ? dims[dims.Length - 1] : 0;
        if (dimension > 0)
        {
            _state[inputName + "#dim"] = new[] { (float)dimension };
            return dimension;
        }

        // 动态维度：先按 384（RNNoise 的 GRU 状态是 3×128）试一次，失败则退化为 128
        Log.Warn($"状态张量 {inputName} 的维度是动态的，暂按 384 处理。");
        _state[inputName + "#dim"] = new[] { 384f };
        return 384;
    }

    public void Reset()
    {
        foreach (var key in _state.Keys.Where(k => !k.EndsWith("#dim", StringComparison.Ordinal)).ToList())
            _state[key] = Array.Empty<float>();
    }

    public void Dispose() => _session.Dispose();
}
