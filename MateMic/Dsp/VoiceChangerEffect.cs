using NAudio.Effects;
using NAudio.Wave;
using MateMic.Core;

namespace MateMic.Dsp;

/// <summary>
/// 变声（DSP 层）：**变调 + 音色倾斜 + 干湿比**。
///
/// 位置：AI 降噪**之后**（见 <c>AudioEngine.UpdateAllParameters</c> 的链路顺序）——
/// 先得到干净语音，再做音色变换；噪声会显著影响变调与音色处理的质量。
///
/// 设计取向：
///   · **零模型、零下载、零延迟代价**（除了变调器自身的约 40 ms 固有延迟）；
///   · 因此它是"装完即用"的那一层兜底，也是将来 AI 变声模块的**地基**
///     （音高检测 / 变调 / 分块流式三者共用）；
///   · 只做两个真正有用的方向（男→女、女→男）+ 三个滑条，不做花哨预设
///     （机器人 / 电话音这类"特效"不属于变声，属于效果器，用户已明确不要）。
///
/// ⚠ 诚实的边界：这里的「音色」是**频谱倾斜**（低架 + 2.6 kHz 存在感 + 高架），
/// 不是真正的**共振峰搬移**。只搬音高会得到"花栗鼠/怪兽"听感，加一点频谱倾斜能明显改善，
/// 但要做得像"换了个体型"，需要真正的共振峰搬移（重采样 + 反向变调补偿）——列为下一步。
/// </summary>
public sealed class VoiceChangerEffect : IAudioEffect
{
    /// <summary>回调块长上限，与 DynamicChain 的 scratch 一致；超出部分本模块不处理（引擎不会给这么大）。</summary>
    private const int MaxBlock = 8192;

    private readonly VoiceChangerSettings _settings;
    private readonly PitchShifter _shifter;
    private readonly float[] _dry = new float[MaxBlock];
    private readonly NAudio.Effects.Equalizer _timbre;
    private readonly EqualizerBand[] _bands;

    public VoiceChangerEffect(WaveFormat format, VoiceChangerSettings settings)
    {
        WaveFormat = format;
        _settings = settings;
        _shifter = new PitchShifter(format.SampleRate);

        _bands = new[]
        {
            EqualizerBand.LowShelf(320f, 0.7f, 0f),      // 厚度
            EqualizerBand.Peaking(2600f, 1.0f, 0f),      // 存在感（"年轻/明亮"最敏感的一段）
            EqualizerBand.HighShelf(5200f, 0.7f, 0f),    // 空气感
        };
        _timbre = new NAudio.Effects.Equalizer(_bands);
        _timbre.Configure(format);

        UpdateParameters();
    }

    public string Name => "变声";

    public bool Enabled { get; set; }

    public WaveFormat WaveFormat { get; }

    public void UpdateParameters()
    {
        _shifter.Semitones = Math.Clamp(_settings.Semitones, -12f, 12f);

        // 音色：把 −50~+50 映射成 −1~+1，再分配到三个频段
        var t = Math.Clamp(_settings.Timbre, -50f, 50f) / 50f;
        _bands[0].GainDb = -3f * t;
        _bands[1].GainDb = 5f * t;
        _bands[2].GainDb = 4f * t;
        _timbre.Update();
    }

    public int Read(Span<float> buffer)
    {
        if (buffer.Length == 0) return 0;

        var count = Math.Min(buffer.Length, MaxBlock);
        var work = buffer[..count];

        var pitchActive = Math.Abs(_settings.Semitones) > 0.01f;
        var timbreActive = Math.Abs(_settings.Timbre) > 0.5f;
        var mix = Math.Clamp(_settings.Mix, 0f, 100f) / 100f;

        if (!pitchActive && !timbreActive) return buffer.Length;

        // 干声要先留一份：变调器是就地处理的，混音时原声已经没了
        if (mix < 0.999f) work.CopyTo(_dry);

        if (pitchActive) _shifter.Process(work);
        if (timbreActive) _timbre.Process(work);

        if (mix < 0.999f)
        {
            for (var i = 0; i < count; i++)
                work[i] = _dry[i] * (1f - mix) + work[i] * mix;
        }

        return buffer.Length;
    }
}
