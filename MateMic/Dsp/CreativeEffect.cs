using NAudio.Wave;
using MateMic.Core;

namespace MateMic.Dsp;

/// <summary>
/// 5. 效果器（单例）：混响 / 延迟 / 合唱 / 电音 / 炸麦 五选一，同时只有一个生效。
/// 切换时把旧效果从链路中移除、插入新效果（由引擎负责重建链路）。
/// 「效果强度」映射到各效果的主参数。
///
/// 电音（Robot）使用专用硬调音模块：基频吸附到音阶 + 相位声码器变调 + 电子化音染。
/// 炸麦（Megaphone）使用专用模块：严重削波失真 + 间歇丢字（劣质麦克风听感）。
/// </summary>
public sealed class CreativeEffect : IAudioEffect
{
    private readonly CreativeEffectSettings _settings;

    private NAudio.Effects.AudioEffect? _inner;
    private HardTuneEffect? _hardTune;
    private MegaphoneDistortionEffect? _megaphone;
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
        CreativeEffectKind.Chorus => "合唱",
        CreativeEffectKind.Robot => "电音",
        CreativeEffectKind.Megaphone => "炸麦",
        _ => "混响",
    };

    private void Rebuild()
    {
        _inner?.Reset();
        _inner = null;
        _hardTune = null;
        _megaphone = null;

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

            case CreativeEffectKind.Chorus:
                _inner = new NAudio.Effects.ChorusEffect
                {
                    BaseDelayMs = 18f,
                    DepthMs = 6f,
                    RateHz = 0.8f,
                    Feedback = 0.15f,
                };
                break;

            case CreativeEffectKind.Robot:
                _hardTune = new HardTuneEffect(WaveFormat.SampleRate);
                _hardTune.Configure(WaveFormat);
                break;

            case CreativeEffectKind.Megaphone:
                _megaphone = new MegaphoneDistortionEffect();
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

            case CreativeEffectKind.Chorus:
                if (_inner is NAudio.Effects.ChorusEffect chorus)
                {
                    chorus.Mix = amount;
                    chorus.DepthMs = 3f + amount * 12f;
                    chorus.Feedback = amount * 0.35f;
                }

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

            default:
                if (_inner is NAudio.Effects.ReverbEffect reverb)
                {
                    reverb.Mix = amount;
                    reverb.RoomSize = 0.3f + amount * 0.6f;
                    reverb.Damping = 0.5f - amount * 0.3f;
                }

                break;
        }
    }

    public int Read(Span<float> buffer)
    {
        if (!Enabled || _settings.Kind == null) return buffer.Length;

        switch (_settings.Kind)
        {
            case CreativeEffectKind.Robot:
                _hardTune?.ProcessInPlace(buffer, WaveFormat.SampleRate);
                return buffer.Length;

            case CreativeEffectKind.Megaphone:
                _megaphone?.ProcessInPlace(buffer, WaveFormat.SampleRate);
                return buffer.Length;

            default:
                _inner?.Process(buffer);
                return buffer.Length;
        }
    }
}
