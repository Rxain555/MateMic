using NAudio.Wave;
using MateMic.Core;
using Signalsmith;

namespace MateMic.Dsp;

/// <summary>
/// 变声（DSP 层）：**变调 + 共振峰独立搬移 + 干湿比**。
///
/// 用 Signalsmith Stretch（官方 MIT；C# 包装 `SignalsmithStretch-CS`，自带 win-x64 原生库约 136 KB）。
/// 实测结论（`tools\dev\StretchProbe`）：
///   · **变调与共振峰互不干扰** —— 用纯正弦 + DFT 主频测：变调 +6 时无论共振峰取 0/+4/−4，
///     主频都是 +6.1 半音；而共振峰取 +4 且变调为 0 时主频完全不动。
///     （早先"两者会互相干扰"的结论是**测试信号造成的假象**：脉冲串经非线性频率映射后
///      不再是谐波信号，自相关测频随即失效。判据错了，不是库错了。）
///   · 变调偏差 ±7 音分内；CPU 约 0.9% 实时预算；延迟约等于内部块长。
///
/// 块长的取舍：块越小延迟越低、但频率分辨率越差、音质越"怪"。
/// 这里取 **2880 样本（60 ms）**：延迟与音质的折中（默认预设是 5760/120 ms，音质最好但延迟大）。
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

    /// <summary>内部块长（48 kHz 下 2880 样本 = 60 ms）。延迟与音质的折中点。</summary>
    private const int InternalBlockSamples = 2880;

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
        if (mix < 0.999f) work.CopyTo(_dry);   // 干湿比要混回去，原声先留一份

        // 参数有变化时才调原生库（在音频线程上调，避免与 Process 竞争）
        if (semitones != _appliedSemitones)
        {
            _stretch.SetTransposeSemitones(semitones, 0f);
            _appliedSemitones = semitones;
        }

        if (formant != _appliedFormant)
        {
            // compensatePitch = true：搬共振峰时补偿音高，实测不会影响已设定的变调量
            _stretch.SetFormantSemitones(formant, true);
            _appliedFormant = formant;
        }

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
