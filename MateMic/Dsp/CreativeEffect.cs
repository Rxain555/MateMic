using NAudio.Dsp;
using NAudio.Wave;
using MateMic.Core;

namespace MateMic.Dsp;

/// <summary>
/// 5. 效果器（单例）：混响 / 延迟 / 和声 / 电话 / 颤音 / 电音 / 炸麦 七选一，同时只有一个生效。
/// 切换时把旧效果从链路中移除、插入新效果（由引擎负责重建链路）。
/// 「效果强度」映射到各效果的主参数。
///
/// 电音（Robot）使用专用硬调音模块：基频吸附到音阶 + 变调 + 电子化音染。
/// 炸麦（Megaphone）使用专用模块：严重削波失真 + 间歇丢字（劣质麦克风听感）。
/// 和声（Harmony）用变调器叠一个纯五度声部 —— 这是原来「合唱」想做的事：
///   合唱只是把原声轻微失谐再叠加，单声道链路上价值很低，已于 2026-10-03 换掉。
/// 电话（Telephone）与颤音（Tremolo）是轻量效果，直接在 <see cref="Read"/> 里逐样本处理。
/// </summary>
public sealed class CreativeEffect : IAudioEffect
{
    /// <summary>与 DynamicChain 的 scratch 同级的块长上限。</summary>
    private const int MaxBlock = 8192;

    /// <summary>和声叠加的音程：+7 半音 = 纯五度。比三度更"安全"（大小调都能用）。</summary>
    private const float HarmonyInterval = 7f;

    private readonly CreativeEffectSettings _settings;

    /// <summary>和声混音用的干声副本（变调器是就地处理的，原声要先留一份）。</summary>
    private readonly float[] _dry = new float[MaxBlock];

    private NAudio.Effects.AudioEffect? _inner;
    private HardTuneEffect? _hardTune;
    private MegaphoneDistortionEffect? _megaphone;
    private PitchShifter? _harmony;
    private BiQuadFilter? _phoneHighPass;
    private BiQuadFilter? _phoneLowPass;
    private double _tremoloPhase;
    private CreativeEffectKind? _appliedKind;

    public CreativeEffect(WaveFormat format, CreativeEffectSettings settings)
    {
        WaveFormat = format;
        _settings = settings;
        UpdateParameters();
    }

    public string Name => "效果器";

    public bool Enabled { get; set; }

    public WaveFormat WaveFormat { get; }

    /// <summary>当前选中的效果类型；null 表示尚未选择。</summary>
    public CreativeEffectKind? Kind => _settings.Kind;

    public static string KindName(CreativeEffectKind kind) => kind switch
    {
        CreativeEffectKind.Delay => "延迟",
        CreativeEffectKind.Harmony => "和声",
        CreativeEffectKind.Robot => "电音",
        CreativeEffectKind.Megaphone => "炸麦",
        CreativeEffectKind.Telephone => "电话",
        CreativeEffectKind.Tremolo => "颤音",
        _ => "混响",
    };

    private void Rebuild()
    {
        _inner?.Reset();
        _inner = null;
        _hardTune = null;
        _megaphone = null;
        _harmony = null;
        _phoneHighPass = null;
        _phoneLowPass = null;
        _tremoloPhase = 0;

        if (_settings.Kind == null) return;

        switch (_settings.Kind)
        {
            case CreativeEffectKind.Delay:
                _inner = new NAudio.Effects.DelayEffect
                {
                    DelayMs = 260f,
                    Feedback = 0.35f,
                    Damping = 0.3f,
                };
                break;

            case CreativeEffectKind.Harmony:
                _harmony = new PitchShifter(WaveFormat.SampleRate) { Semitones = HarmonyInterval };
                break;

            case CreativeEffectKind.Robot:
                _hardTune = new HardTuneEffect(WaveFormat.SampleRate);
                _hardTune.Configure(WaveFormat);
                break;

            case CreativeEffectKind.Megaphone:
                _megaphone = new MegaphoneDistortionEffect();
                break;

            case CreativeEffectKind.Telephone:
                // 300–3400 Hz：电话/对讲机的经典带宽
                _phoneHighPass = BiQuadFilter.HighPassFilter(WaveFormat.SampleRate, 300f, 0.8f);
                _phoneLowPass = BiQuadFilter.LowPassFilter(WaveFormat.SampleRate, 3400f, 0.8f);
                break;

            case CreativeEffectKind.Tremolo:
                // 逐样本调制，无需内部状态机
                break;

            default:
                _inner = new NAudio.Effects.ReverbEffect
                {
                    RoomSize = 0.6f,
                    Damping = 0.4f,
                    Width = 1f,
                };
                break;
        }

        _inner?.Configure(WaveFormat);
        Log.Info($"效果器已切换为 {KindName(_settings.Kind.Value)}");
    }

    public void UpdateParameters()
    {
        if (_settings.Kind != _appliedKind)
        {
            Rebuild();
            _appliedKind = _settings.Kind;
        }

        if (_settings.Kind == null) return;

        var amount = Math.Clamp(_settings.Amount, 0f, 100f) / 100f;
        switch (_settings.Kind)
        {
            case CreativeEffectKind.Delay:
                if (_inner is NAudio.Effects.DelayEffect delay)
                {
                    delay.Mix = amount * 0.65f;
                    delay.Feedback = 0.2f + amount * 0.5f;
                }

                break;

            case CreativeEffectKind.Harmony:
                // 和声的量在 Read 里按干湿混合处理，这里不需要额外参数
                break;

            case CreativeEffectKind.Robot:
                if (_hardTune != null)
                {
                    // 强度越大：吸附越快（越"电"）、共鸣越强
                    _hardTune.RetuneSpeed = 35f + amount * 65f;
                    _hardTune.HarmonyAmount = 0.1f + amount * 0.4f;
                }

                break;

            case CreativeEffectKind.Megaphone:
                if (_megaphone != null)
                {
                    // 强度只调"失真有多狠、压得有多扁"
                    _megaphone.CompressorRatio = 8f + amount * 14f;              // 8–22
                    _megaphone.CompressorThresholdDb = -20f - amount * 12f;      // -20 → -32 dBFS
                    _megaphone.DriveDb = 10f + amount * 12f;                     // 10–22 dB
                    _megaphone.MakeupGainDb = 6f + amount * 6f;                  // 6–12 dB
                    _megaphone.BitDepth = 5 + (int)MathF.Round((1f - amount) * 7f);   // 5–12 bit
                    _megaphone.HighPassHz = 260f + (1f - amount) * 140f;         // 260–400 Hz
                    _megaphone.LowPassHz = 2200f + (1f - amount) * 1800f;        // 2.2–4 kHz
                    _megaphone.NoiseAmount = 0.35f + amount * 0.45f;
                    _megaphone.RoarAmount = 0.25f + amount * 0.55f;              // 说话时的呼呼风声
                    _megaphone.Mix = 0.75f + amount * 0.25f;
                }

                break;

            case CreativeEffectKind.Telephone:
            case CreativeEffectKind.Tremolo:
                // 逐样本处理，参数在 Read 里按 amount 实时换算
                break;

            default:
                if (_inner is NAudio.Effects.ReverbEffect reverb)
                {
                    reverb.Mix = amount;
                    reverb.RoomSize = 0.3f + amount * 0.6f;
                    reverb.Damping = 0.5f - amount * 0.3f;
                    reverb.Width = 1f;
                }

                break;
        }
    }

    public int Read(Span<float> buffer)
    {
        if (!Enabled || _settings.Kind == null || buffer.Length == 0) return buffer.Length;

        var amount = Math.Clamp(_settings.Amount, 0f, 100f) / 100f;

        switch (_settings.Kind)
        {
            case CreativeEffectKind.Robot:
                _hardTune?.ProcessInPlace(buffer, WaveFormat.SampleRate);
                return buffer.Length;

            case CreativeEffectKind.Megaphone:
                _megaphone?.ProcessInPlace(buffer, WaveFormat.SampleRate);
                return buffer.Length;

            case CreativeEffectKind.Harmony:
                return ProcessHarmony(buffer, amount);

            case CreativeEffectKind.Telephone:
                return ProcessTelephone(buffer, amount);

            case CreativeEffectKind.Tremolo:
                return ProcessTremolo(buffer, amount);

            default:
                _inner?.Process(buffer);
                return buffer.Length;
        }
    }

    /// <summary>和声：把整块变调成纯五度声部，再与原声混合。</summary>
    private int ProcessHarmony(Span<float> buffer, float amount)
    {
        if (_harmony == null) return buffer.Length;

        var count = Math.Min(buffer.Length, MaxBlock);
        var work = buffer[..count];

        work.CopyTo(_dry);          // 干声先留一份（变调是就地处理的）
        _harmony.Process(work);     // work 现在是"五度声部"

        // 原声略退一点，避免两个声部叠加后总电平明显变高
        var dryGain = 1f - 0.4f * amount;
        for (var i = 0; i < count; i++)
            work[i] = _dry[i] * dryGain + work[i] * amount * 0.9f;

        return buffer.Length;
    }

    /// <summary>电话：300–3400 Hz 带通 + tanh 轻饱和（窄带后能量掉得多，用驱动补回来）。</summary>
    private int ProcessTelephone(Span<float> buffer, float amount)
    {
        if (_phoneHighPass == null || _phoneLowPass == null) return buffer.Length;

        var drive = 2.0f + amount * 3f;
        for (var i = 0; i < buffer.Length; i++)
        {
            var v = _phoneLowPass.Transform(_phoneHighPass.Transform(buffer[i]));
            buffer[i] = (float)Math.Tanh(v * drive) * 0.85f;
        }

        return buffer.Length;
    }

    /// <summary>颤音：5 Hz 周期音量起伏，强度 = 起伏有多深。</summary>
    private int ProcessTremolo(Span<float> buffer, float amount)
    {
        var depth = 0.25f + amount * 0.75f;
        var step = 2 * Math.PI * 5.0 / WaveFormat.SampleRate;

        for (var i = 0; i < buffer.Length; i++)
        {
            var mod = 1f - depth * 0.5f * (1f + (float)Math.Sin(_tremoloPhase));
            buffer[i] *= mod;

            _tremoloPhase += step;
            if (_tremoloPhase > 2 * Math.PI) _tremoloPhase -= 2 * Math.PI;
        }

        return buffer.Length;
    }
}
