using NAudio.Wave;
using MicMate.Core;

namespace MicMate.Dsp;

/// <summary>
/// 4. 音色风格：六选一的预设 EQ 曲线（项目书 3.2.4 的频率/增益/Q 值），
/// 使用 NAudio.Effects.Equalizer 串联峰值滤波器实现，切换预设时点击无爆音。
/// </summary>
public sealed class ToneStyleEffect : IAudioEffect
{
    /// <summary>预设固定使用的滤波器段数（所有预设都是 3 段）。</summary>
    private const int BandCount = 3;

    private readonly ToneSettings _settings;
    private readonly NAudio.Effects.Equalizer _equalizer;
    private readonly NAudio.Effects.EqualizerBand[] _bands;

    private ToneStyle? _appliedStyle;

    public ToneStyleEffect(WaveFormat format, ToneSettings settings)
    {
        WaveFormat = format;
        _settings = settings;

        // 必须按 BandCount 段分配：NAudio 的 Equalizer 是**按引用**持有这个数组的，
        // 长度一旦定下就再也不会变，而 UpdateParameters 是按 _bands.Length 复制的。
        // 早期版本用 `BuildBands(settings.Style ?? Natural)` 建数组，未选择风格时
        // 只得到 1 段（Natural 是平坦响应，只写了 1 个滤波器），于是之后切换任何预设
        // 都只有第 1 段生效——「沉稳」丢掉 6 kHz 衰减和 9 kHz 高架，「清亮」丢掉低频衰减。
        _bands = BuildFlatBands();
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
        for (var i = 0; i < _bands.Length; i++)
        {
            // 预设段数与分配段数不一致时，多出来的段一律置平（增益 0），
            // 绝不能让上一条预设的增益残留下来
            var band = i < preset.Length ? preset[i] : FlatBand;

            _bands[i].Type = band.Type;
            _bands[i].Frequency = band.Frequency;
            _bands[i].Q = band.Q;
            _bands[i].GainDb = band.GainDb;
            _bands[i].ShelfSlope = band.ShelfSlope;
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
        _ => BuildFlatBands(),
    };

    /// <summary>平坦响应（增益 0）。既是「未选择 / 自然」的响应，也是数组长度的基准。</summary>
    private static NAudio.Effects.EqualizerBand[] BuildFlatBands()
    {
        // 必须是**各自独立**的实例：Equalizer 按引用持有这个数组，而 UpdateParameters
        // 是逐个写 _bands[i] 的；若三段共用一个对象，后写的段会覆盖前面的段，
        // 结果三个滤波器全变成最后一段（实测：整个均衡器等于没有作用）。
        var bands = new NAudio.Effects.EqualizerBand[BandCount];
        for (var i = 0; i < bands.Length; i++)
            bands[i] = NAudio.Effects.EqualizerBand.Peaking(1000f, 1f, 0f);
        return bands;
    }

    /// <summary>只读的平坦段：仅用于填充，绝不写回（<see cref="UpdateParameters"/> 只从预设里读）。</summary>
    private static readonly NAudio.Effects.EqualizerBand FlatBand =
        NAudio.Effects.EqualizerBand.Peaking(1000f, 1f, 0f);
}
