using NAudio.Wave;
using MateMic.Core;

namespace MateMic.Dsp;

/// <summary>
/// 1. 噪声门：低于阈值的信号静音（NAudio.Effects.GateEffect 做实际 DSP，
/// 这里负责参数映射与平滑，阈值/释放时间在运行中可变）。
/// </summary>
public sealed class NoiseGateEffect : IAudioEffect
{
    private readonly NAudio.Effects.GateEffect _gate = new();
    private readonly NoiseGateSettings _settings;

    public NoiseGateEffect(WaveFormat format, NoiseGateSettings settings)
    {
        _settings = settings;
        WaveFormat = format;
        _gate.AttackMs = 1f;
        _gate.HoldMs = 40f;
        _gate.HysteresisDb = 3f;
        _gate.RangeDb = -80f;
        _gate.Ratio = 50f;
        _gate.Configure(format);
        UpdateParameters();
    }

    public string Name => "噪声门";

    public bool Enabled { get; set; } = true;

    public WaveFormat WaveFormat { get; }

    public float ThresholdDb => _settings.ThresholdDb;

    public void UpdateParameters()
    {
        _gate.ThresholdDb = _settings.ThresholdDb;
        _gate.ReleaseMs = _settings.ReleaseMs;
    }

    public int Read(Span<float> buffer)
    {
        var read = buffer.Length;
        if (Enabled) _gate.Process(buffer);
        return read;
    }
}
