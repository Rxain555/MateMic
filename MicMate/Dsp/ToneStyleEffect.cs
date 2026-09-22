using NAudio.Wave;
using MicMate.Core;

namespace MicMate.Dsp;

/// <summary>
/// 4. 音色风格：六选一的预设 EQ 曲线（项目书 3.2.4 的频率/增益/Q 值），
/// 使用 NAudio.Effects.Equalizer 串联峰值滤波器实现，切换预设时点击无爆音。
/// </summary>
public sealed class ToneStyleEffect : IAudioEffect
{
    private readonly ToneSettings _settings;
    private readonly NAudio.Effects.Equalizer _equalizer;
    private readonly NAudio.Effects.EqualizerBand[] _bands;

    private ToneStyle? _appliedStyle;

    public ToneStyleEffect(WaveFormat format, ToneSettings settings)
    {
        WaveFormat = format;
        _settings = settings;
        _bands = BuildBands(settings.Style ?? ToneStyle.Natural);
        _equalizer = new NAudio.Effects.Equalizer(_bands);
        _equalizer.Configure(format);
        UpdateParameters();
    }

    public string Name => "音色风格";

    public bool Enabled { get; set; }

    public WaveFormat WaveFormat { get; }

    /// <summary>当前选中的音色风格；null 表示尚未选择。</summary>
    public ToneStyle? Style => _settings.Style;

    public void UpdateParameters()
    {
        if (_settings.Style == _appliedStyle) return;

        // 未选择时按平坦响应处理，模块本身由引擎从处理链中移除
        var preset = BuildBands(_settings.Style ?? ToneStyle.Natural);
        for (var i = 0; i < _bands.Length && i < preset.Length; i++)
        {
            _bands[i].Type = preset[i].Type;
            _bands[i].Frequency = preset[i].Frequency;
            _bands[i].Q = preset[i].Q;
            _bands[i].GainDb = preset[i].GainDb;
            _bands[i].ShelfSlope = preset[i].ShelfSlope;
        }

        _equalizer.Update();
        _appliedStyle = _settings.Style;
        Log.Debug($"音色风格：{(_settings.Style.HasValue ? StyleName(_settings.Style.Value) : "未选择")}");
    }

    public int Read(Span<float> buffer)
    {
        if (!Enabled) return buffer.Length;

        // 未选择音色风格或选择「自然」= 平坦响应（bypass）
        if (_settings.Style == null || _settings.Style == ToneStyle.Natural) return buffer.Length;

        _equalizer.Process(buffer);
        return buffer.Length;
    }

    public static string StyleName(ToneStyle style) => style switch
    {
        ToneStyle.Bright => "清亮",
        ToneStyle.Warm => "沉稳",
        ToneStyle.Deep => "深邃",
        ToneStyle.Sharp => "尖锐",
        ToneStyle.Ethereal => "空灵",
        _ => "自然",
    };

    /// <summary>
    /// 音色风格预设的频响特征。
    /// 增益整体做得比"教科书值"更大：早期只给 ±1.5–3 dB，实测改变量仅 0.026–0.051,
    /// 用户根本听不出区别（对比效果器是 0.16–0.30）。现在提到 ±4–8 dB 保证可感知。
    /// </summary>
    private static NAudio.Effects.EqualizerBand[] BuildBands(ToneStyle style) => style switch
    {
        // 提亮：大幅提升 3–6 kHz 空气感，压低低频浑浊
        ToneStyle.Bright => new[]
        {
            NAudio.Effects.EqualizerBand.Peaking(4500f, 0.9f, 7f),
            NAudio.Effects.EqualizerBand.Peaking(300f, 1.0f, -4f),
            NAudio.Effects.EqualizerBand.LowShelf(120f, -3f),
        },
        // 沉稳：提升 100–250 Hz 厚度，削掉刺耳高频
        ToneStyle.Warm => new[]
        {
            NAudio.Effects.EqualizerBand.Peaking(175f, 0.8f, 6f),
            NAudio.Effects.EqualizerBand.Peaking(6000f, 0.9f, -6f),
            NAudio.Effects.EqualizerBand.HighShelf(9000f, -4f),
        },
        // 深邃：加重 80–150 Hz，明显衰减 1–3 kHz 的存在感
        ToneStyle.Deep => new[]
        {
            NAudio.Effects.EqualizerBand.Peaking(115f, 0.9f, 8f),
            NAudio.Effects.EqualizerBand.Peaking(2000f, 0.9f, -7f),
            NAudio.Effects.EqualizerBand.HighShelf(8000f, -4f),
        },
        // 锐利：抬 4–8 kHz 与 2–4 kHz 的清晰度
        ToneStyle.Sharp => new[]
        {
            NAudio.Effects.EqualizerBand.Peaking(6000f, 1.0f, 8f),
            NAudio.Effects.EqualizerBand.Peaking(3000f, 1.1f, 4f),
            NAudio.Effects.EqualizerBand.HighShelf(10000f, 3f),
        },
        // 提升 8–12 kHz（+3 dB），轻微提升 2–5 kHz，衰减 300–800 Hz
        ToneStyle.Ethereal => new[]
        {
            NAudio.Effects.EqualizerBand.Peaking(10000f, 1.0f, 3f),
            NAudio.Effects.EqualizerBand.Peaking(3500f, 1.0f, 1f),
            NAudio.Effects.EqualizerBand.Peaking(550f, 0.9f, -2f),
        },
        // 自然：平坦响应（bypass）
        _ => new[]
        {
            NAudio.Effects.EqualizerBand.Peaking(1000f, 1f, 0f),
        },
    };
}
