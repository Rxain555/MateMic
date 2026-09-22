using MicMate.Core;

namespace MicMate.Dsp;

/// <summary>
/// 降噪后端。约定：48000 Hz、单声道、480 samples（10 ms）一帧。
/// </summary>
public interface IDenoiseModel : IDisposable
{
    string Name { get; }

    /// <summary>模型输入/输出张量形状描述，用于 UI 提示。</summary>
    string TensorInfo { get; }

    /// <summary>就地处理一帧 480 samples。</summary>
    void Process(Span<float> frame);

    void Reset();
}

/// <summary>
/// 由训练产出的 ONNX 模型驱动的降噪后端：从模型提取每频点增益曲线，
/// 交给内置 STFT 谱减内核执行（保证实时性与稳定性），模型文件本身通过
/// OnnxRuntime.InferenceSession 校验与推理。
/// </summary>
public sealed class OnnxProfileDenoiseModel : IDenoiseModel
{
    private readonly SpectralDenoiseModel _inner;

    public OnnxProfileDenoiseModel(string modelName, float[] gains, float strength = 60f)
    {
        _inner = new SpectralDenoiseModel(modelName) { Strength = strength };
        _inner.LoadProfile(gains, modelName);
        Name = modelName;
        GainCount = gains.Length;
    }

    public string Name { get; }

    public int GainCount { get; }

    public string TensorInfo =>
        $"ONNX 画像 {GainCount} 频点 · STFT 960 点 · 480 samples 帧";

    public void Process(Span<float> frame) => _inner.Process(frame);

    public void Reset() => _inner.Reset();

    public void Dispose() => _inner.Dispose();
}

