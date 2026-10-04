using NAudio.Wave;
using MateMic.Core;
using Signalsmith;

namespace MateMic.Dsp;

/// <summary>
/// 变声（DSP 层）：**性别因子（CDF 音高映射）+ 共振峰搬移 + 干湿比**。
///
/// 一、为什么用"CDF 音高映射"，而不是一刀切半音移位
///   男女声的差别不只是"均值高几个半音"：
///     · 基频**均值**不同（男 ~120 Hz、女 ~210 Hz）；
///     · 基频**离散度**也不同（女性语调起伏通常更大）。
///   只做整体移位，会把源说话人的起伏幅度一起搬过去 —— 说话平的人变成"平的女声"，
///   说得夸张的人变成"夸张的女声"，听起来就是变声器。
///   做法（属性转换文献里的经典套路；正态假设下等价于在半音域做 z 分数变换）：
///       目标 = 源均值 + 性别偏移 + (源 − 源均值) × (目标离散度 / 源离散度)
///   即：**均值按滑条移动，离散度归一化到目标性别的典型值**。
///   源均值/离散度**在线估计**（只统计可信浊音帧），因此自动适配每个说话人 ——
///   低音男声会自动多升一点、本来就偏高的男声会少升一点，最终分布都落到目标性别上。
///
/// 二、音高与共振峰的分工
///   · 音高：每块按上面的映射实时计算（**保留说话人的语调**，这是自然度的关键）；
///   · 共振峰：按"性别偏移"整体搬移（**不跟随逐帧音高**）——这一层才是音色变性的部分。
///
/// 三、引擎
///   音高与共振峰都由 Signalsmith Stretch（官方 MIT）完成。实测：变调偏差 ±7 音分、
///   共振峰可独立搬移（只动共振峰时基频不动）、CPU 约 0.9% 实时预算、
///   内部块 2880 样本（约 60 ms 延迟）。
///
/// ⚠ 调用顺序坑（实测）：必须先调一次预设再 `Configure`，单独 `Configure` 会得到近乎静音的输出。
/// </summary>
public sealed class VoiceChangerEffect : IAudioEffect
{
    /// <summary>回调块长上限，与 DynamicChain 的 scratch 一致。</summary>
    private const int MaxBlock = 8192;

    /// <summary>Signalsmith 内部块长（48 kHz 下 2880 样本 ≈ 60 ms）。</summary>
    private const int InternalBlockSamples = 2880;

    /// <summary>基频分析窗（样本）。<see cref="PitchDetector"/> 需要整窗。</summary>
    private const int F0Window = 1024;

    /// <summary>性别滑条 ±100 对应的均值偏移（半音）。±7 半音 ≈ 120 Hz ↔ 210 Hz 的量级。</summary>
    private const float GenderRangeSemitones = 7f;

    /// <summary>在线统计的时间常数（越小越跟手、越大越稳）。</summary>
    private const double StatsAlpha = 0.02;

    /// <summary>变调量平滑系数（每块逼近比例）：避免逐块跳变撕裂波形。</summary>
    private const float ShiftSmoothing = 0.25f;

    private readonly VoiceChangerSettings _settings;
    private readonly Stretch _stretch;
    private readonly PitchDetector _f0Detector;
    private readonly float[] _dry = new float[MaxBlock];
    private readonly float[] _input = new float[MaxBlock];
    private readonly float[] _output = new float[MaxBlock];
    private readonly float[] _f0Buffer = new float[F0Window];

    private int _f0Fill;
    private int _logCountdown = 48000;

    /// <summary>在线估计的说话人基频均值（半音，相对 55 Hz）。NaN = 还没估出来。</summary>
    private double _srcMeanSemi = double.NaN;

    /// <summary>在线估计的基频方差（半音²）。</summary>
    private double _srcVarSemi = 4.0;

    private long _srcCount;
    private float _appliedTranspose = float.NaN;
    private float _appliedFormant = float.NaN;
    private float _smoothedShift;
    private bool _hasShift;

    public VoiceChangerEffect(WaveFormat format, VoiceChangerSettings settings)
    {
        WaveFormat = format;
        _settings = settings;

        _f0Detector = new PitchDetector(format.SampleRate, windowSize: F0Window);

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

    /// <summary>最近一次估计到的源基频（Hz）。0 = 当前无可信浊音。诊断用。</summary>
    public float LastSourceFrequency { get; private set; }

    /// <summary>在线估计的说话人基频均值（半音，相对 55 Hz）。诊断用。</summary>
    public double SourceMeanSemitones => _srcMeanSemi;

    /// <summary>在线估计的说话人基频标准差（半音）。诊断用。</summary>
    public double SourceStdSemitones => _srcVarSemi > 0 ? Math.Sqrt(_srcVarSemi) : 0;

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

        var manual = Math.Clamp(_settings.Semitones, -12f, 12f);
        var gender = Math.Clamp(_settings.GenderFactor, -100f, 100f) / 100f;
        var genderSemitones = gender * GenderRangeSemitones;

        // 共振峰 = 手动偏移 + 性别偏移（音色变性由它负责，不跟随逐帧音高）
        var formant = Math.Clamp(Math.Clamp(_settings.FormantSemitones, -12f, 12f) + genderSemitones, -12f, 12f);

        var genderActive = Math.Abs(gender) > 0.01f;
        var pitchActive = genderActive || Math.Abs(manual) > 0.01f;
        var formantActive = Math.Abs(formant) > 0.01f;
        if (!pitchActive && !formantActive) return buffer.Length;

        var mix = Math.Clamp(_settings.Mix, 0f, 100f) / 100f;
        if (mix < 0.999f) work.CopyTo(_dry);

        // ---- 1) 更新在线基频统计（性别映射需要它）----
        if (genderActive) UpdateSourcePitch(work);

        // ---- 2) 算出本块要用的变调量 ----
        var shift = manual;
        if (genderActive && _srcCount >= 10 && !double.IsNaN(_srcMeanSemi) && LastSourceFrequency > 0)
        {
            var srcSemi = 12.0 * Math.Log2(LastSourceFrequency / 55.0);
            var srcStd = SourceStdSemitones;

            // 目标离散度：性别偏移越大，越往目标性别的典型起伏靠
            var targetStd = 1.9 + 0.6 * Math.Abs(gender);

            // 源离散度过小时（说话很平）不要放大到离谱
            var scale = srcStd > 0.3 ? Math.Clamp(targetStd / srcStd, 0.5, 2.5) : 1.0;

            var mapped = _srcMeanSemi + genderSemitones + (srcSemi - _srcMeanSemi) * scale;
            shift = (float)(mapped - srcSemi) + manual;
        }

        shift = Math.Clamp(shift, -12f, 12f);

        // 平滑（首次直接取用）
        _smoothedShift = _hasShift ? _smoothedShift + (shift - _smoothedShift) * ShiftSmoothing : shift;
        _hasShift = true;

        // ---- 3) 写进引擎（参数有变化才调原生库，且在音频线程上调）----
        if (float.IsNaN(_appliedTranspose) || Math.Abs(_smoothedShift - _appliedTranspose) > 0.005f)
        {
            _stretch.SetTransposeSemitones(_smoothedShift, 0f);
            _appliedTranspose = _smoothedShift;
        }

        if (float.IsNaN(_appliedFormant) || Math.Abs(formant - _appliedFormant) > 0.005f)
        {
            // compensatePitch = true：搬共振峰时补偿音高，实测不会影响已设定的变调量
            _stretch.SetFormantSemitones(formant, true);
            _appliedFormant = formant;
        }

        // 诊断日志（约每 5 秒一条）：把在线统计与最终变调量打出来，便于核对映射是否在工作
        _logCountdown -= count;
        if (_logCountdown <= 0)
        {
            _logCountdown = WaveFormat.SampleRate * 5;
            if (genderActive && _srcCount >= 10)
            {
                Log.Info($"变声（CDF 映射）：源基频均值 {_srcMeanSemi:0.00} 半音"
                          + $"（≈{55 * Math.Pow(2, _srcMeanSemi / 12):0} Hz），标准差 {SourceStdSemitones:0.00} 半音，"
                          + $"性别偏移 {genderSemitones:+0.0;-0.0;0} 半音 → 本块变调 {_smoothedShift:+0.00;-0.00;0.00} 半音，"
                          + $"共振峰 {formant:+0.0;-0.0;0} 半音");
            }
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

    /// <summary>
    /// 攒满一窗就估一次基频，并用指数滑动平均维护说话人的均值/方差（只统计可信浊音帧）。
    /// 时间常数约 1 秒量级：既能适配说话人，又不会被个别帧带跑。
    /// </summary>
    private void UpdateSourcePitch(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _f0Buffer[_f0Fill++] = sample;
            if (_f0Fill < F0Window) continue;
            _f0Fill = 0;

            var f0 = _f0Detector.Detect(_f0Buffer);
            if (f0 <= 0 || _f0Detector.LastClarity < 0.35f) continue;

            LastSourceFrequency = f0;
            var semi = 12.0 * Math.Log2(f0 / 55.0);

            if (double.IsNaN(_srcMeanSemi))
            {
                _srcMeanSemi = semi;
                _srcVarSemi = 4.0;      // 初值：约 2 半音的标准差
            }
            else
            {
                var delta = semi - _srcMeanSemi;
                _srcMeanSemi += StatsAlpha * delta;
                _srcVarSemi = (1 - StatsAlpha) * (_srcVarSemi + StatsAlpha * delta * delta);
            }

            _srcCount++;
        }
    }
}
