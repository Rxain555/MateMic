using MateMic.Core;
using NAudio.Wave;

namespace MateMic.Dsp;

/// <summary>
/// 「混响」模块 —— 从「效果器」里拆出来的独立卡片。
///
/// 为什么要拆：效果器的七个预设是**单选**的（混响 / 延迟 / 和声 / 电话 / 颤音 / 电音 / 炸麦），
/// 同一时间只有一个生效。而混响最常见的用法恰恰是"**和别的效果叠加**、再把空间感单独调"，
/// 塞在单选里做不到（用户 2026-11-03 提出）。
///
/// DSP 刻意复用 NAudio 的 <see cref="NAudio.Effects.ReverbEffect"/> —— 就是效果器里那条混响用的同一个实现，
/// 所以从旧路径切过来听感一致，不会突变。
///
/// 参数语义（界面是 0~100 的百分比，NAudio 要 0~1）：
///   · <b>干湿比</b> Mix —— 混响声占多少；
///   · <b>房间大小</b> RoomSize —— 尾音能拖多久。取 0.2~1.0：太小会变成"短促的金属声"，
///     没有混响该有的空间感，所以下限不落到 0；
///   · <b>阻尼</b> Damping —— 高频衰减快慢，越大越"闷"；
///   · Width 固定 1（单声道链路里没有左右宽度可言）。
/// </summary>
public sealed class ReverbEffect : IAudioEffect
{
    private readonly NAudio.Effects.ReverbEffect _reverb;
    private readonly ReverbSettings _settings;

    public ReverbEffect(WaveFormat format, ReverbSettings settings)
    {
        WaveFormat = format;
        _settings = settings;
        _reverb = new NAudio.Effects.ReverbEffect { Width = 1f };
        _reverb.Configure(format);
        UpdateParameters();
    }

    public string Name => "混响";

    public bool Enabled { get; set; }

    public WaveFormat WaveFormat { get; }

    public void UpdateParameters()
    {
        var mix = Math.Clamp(_settings.Mix, 0f, 100f) / 100f;
        var room = Math.Clamp(_settings.RoomSize, 0f, 100f) / 100f;
        var damping = Math.Clamp(_settings.Damping, 0f, 100f) / 100f;

        _reverb.Mix = mix;
        _reverb.RoomSize = 0.2f + room * 0.8f;
        _reverb.Damping = damping;
        _reverb.Width = 1f;
    }

    public int Read(Span<float> buffer)
    {
        // ReverbEffect 是就地处理（NAudio 的 AudioEffect.Process），与效果器里那条路径一致。
        if (!Enabled) return buffer.Length;

        _reverb.Process(buffer);
        return buffer.Length;
    }
}
