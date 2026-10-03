using NAudio.Wave;
using MateMic.Core;
using Signalsmith;

namespace MateMic.Dsp;

/// <summary>
/// 变声（DSP 层）：**变调 + 共振峰独立搬移 + 干湿比**。
///
/// 用 Signalsmith Stretch（官方 MIT；C# 包装 `SignalsmithStretch-CS`，自带 win-x64 原生库约 136 KB）。
/// 选它的实测依据（见 `tools\dev\StretchProbe`）：
///   · 变调偏差 ±7 音分内；
///   · **共振峰可独立搬移** —— 只动共振峰时基频完全不动（这是自研 PSOLA 与
///     SmbPitchShifter 都做不到的：前者音高未收敛，后者共振峰跟着音高跑 → "只是变了个音"）；
///   · CPU 仅占实时预算约 0.9%（SmbPitchShifter 约 5.5%）；
///   · 延迟约等于内部块长，取 960 样本（20 ms），比 SmbPitchShifter 固有的约 37 ms 更低。
///
/// ⚠ **调用顺序坑（实测）**：必须先调一次预设，再 `Configure` 改块长。
///   单独 `Configure`（不先调预设）会得到近乎静音的输出（实测 RMS 0.008、音高测量 2570 音分）。
///
/// 位置：AI 降噪**之后**（先拿到干净语音再做音色变换）。
/// </summary>
public sealed class VoiceChangerEffect : IAudioEffect
{
    /// <summary>回调块长上限，与 DynamicChain 的 scratch 一致。</summary>
    private const int MaxBlock = 8192;

    /// <summary>内部块长（48 kHz 下 960 样本 = 20 ms）。延迟与质量的实际折中点。</summary>
    private const int InternalBlockSamples = 960;

    private readonly VoiceChangerSettings _settings;
    private readonly Stretch _stretch;
    private readonly float[] _dry = new float[MaxBlock];
    private readonly float[] _input = new float[MaxBlock];
    private readonly float[] _output = new float[MaxBlock];

    private float _appliedSemitones = float.NaN;
    private float _appliedFormant = float.NaN;

    public VoiceChangerEffect(WaveFormat format, VoiceChangerSettings settings)
    {
        WaveFormat = format;
        _settings = settings;

        _stretch = new Stretch();
        _stretch.PresetDefault(1, format.SampleRate, false);   // 见类注释：必须先预设
        _stretch.Configure(1, InternalBlockSamples, InternalBlockSamples / 4, false);
    }

    public string Name => "变声";

    public bool Enabled { get; set; }

    public WaveFormat WaveFormat { get; }

    /// <summary>本模块引入的额外延迟（毫秒）。诊断用。</summary>
    public double LatencyMs
        => (_stretch.InputLatency() + _stretch.OutputLatency()) * 1000.0 / WaveFormat.SampleRate;

    /// <summary>
    /// 参数写入点在音频线程（见 <see cref="Read"/>）：原生库不是线程安全的，
    /// 不能从 UI 线程直接调它的 Set* 方法。这里刻意留空。
    /// </summary>
    public void UpdateParameters()
    {
    }

    public int Read(Span<float> buffer)
    {
        if (buffer.Length == 0) return 0;

        var count = Math.Min(buffer.Length, MaxBlock);
        var work = buffer[..count];

        var semitones = Math.Clamp(_settings.Semitones, -12f, 12f);
        var formant = Math.Clamp(_settings.FormantSemitones, -12f, 12f);
        var pitchActive = Math.Abs(semitones) > 0.01f;
        var formantActive = Math.Abs(formant) > 0.01f;
        if (!pitchActive && !formantActive) return buffer.Length;

        var mix = Math.Clamp(_settings.Mix, 0f, 100f) / 100f;

        // 参数有变化时才调原生库（在音频线程上调，避免与 Process 竞争）
        if (semitones != _appliedSemitones)
        {
            _stretch.SetTransposeSemitones(semitones, 0f);
            _appliedSemitones = semitones;
        }

        if (formant != _appliedFormant)
        {
            // compensatePitch = true：搬共振峰时补偿音高，保证"只动共振峰"
            _stretch.SetFormantSemitones(formant, true);
            _appliedFormant = formant;
        }

        if (mix < 0.999f) work.CopyTo(_dry);   // 干湿比要混回去，原声先留一份

        work.CopyTo(_input);
        _stretch.Process(_input.AsSpan(0, count), _output.AsSpan(0, count));
        _output.AsSpan(0, count).CopyTo(work);

        if (mix < 0.999f)
        {
            for (var i = 0; i < count; i++)
                work[i] = _dry[i] * (1f - mix) + work[i] * mix;
        }

        return buffer.Length;
    }
}
