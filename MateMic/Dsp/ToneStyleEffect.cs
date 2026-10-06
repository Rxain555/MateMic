using NAudio.Wave;
using MateMic.Core;

namespace MateMic.Dsp;

/// <summary>
/// 4. EQ 均衡器（原「音色风格」）：10 段 peaking 滤波器串成的图形均衡器。
///
/// 与旧版的区别：旧版是"六选一的固定 3 段滤波预设"，没有任何可调项；
/// 现在统一成 <see cref="EqPreset.BandCount"/> 段固定中心频率的 peaking，
/// 预设只是"整表替换各段增益"，因此**预设与手动调节走的是同一条处理路径**，
/// 不会出现"选预设好听、手动一动就变味"的割裂。
///
/// 增益来源单一：一律读 <see cref="ToneSettings.Gains"/>，本类不自己维护预设状态。
/// </summary>
public sealed class ToneStyleEffect : IAudioEffect
{
    private readonly ToneSettings _settings;
    private readonly NAudio.Effects.Equalizer _equalizer;

    /// <summary>
    /// 必须按 <see cref="EqPreset.BandCount"/> 段分配：NAudio 的 Equalizer 是**按引用**
    /// 持有这个数组的，长度一旦定下就再也不会变，而 UpdateParameters 是逐段复制的。
    /// （早期版本用"预设的段数"建数组，未选择预设时只得到 1 段，
    /// 于是之后切换任何预设都只有第 1 段生效——这个坑在旧版踩过一次。）
    /// </summary>
    private readonly NAudio.Effects.EqualizerBand[] _bands;

    /// <summary>上一次真正写入滤波器的增益表，用来避免每帧重复 Update。</summary>
    private float[] _applied = EqPreset.Flat();

    public ToneStyleEffect(WaveFormat format, ToneSettings settings)
    {
        WaveFormat = format;
        _settings = settings;

        _bands = new NAudio.Effects.EqualizerBand[EqPreset.BandCount];
        for (var i = 0; i < _bands.Length; i++)
        {
            // 各段必须是**独立实例**：Equalizer 按引用持有数组，共用实例会让后写的段覆盖前面的段
            _bands[i] = NAudio.Effects.EqualizerBand.Peaking(EqPreset.Frequencies[i], EqPreset.BandQ, 0f);
        }

        _equalizer = new NAudio.Effects.Equalizer(_bands);
        _equalizer.Configure(format);
        UpdateParameters();
    }

    public string Name => "EQ 均衡器";

    public bool Enabled { get; set; }

    public WaveFormat WaveFormat { get; }

    /// <summary>当前各段增益（dB）的只读快照，供诊断日志使用。</summary>
    public IReadOnlyList<float> Gains => _applied;

    public void UpdateParameters()
    {
        var gains = EqPreset.Normalize(_settings.Gains);

        if (Same(_applied, gains)) return;

        for (var i = 0; i < _bands.Length; i++)
        {
            _bands[i].Type = NAudio.Effects.EqualizerBandType.Peaking;
            _bands[i].Frequency = EqPreset.Frequencies[i];
            _bands[i].Q = EqPreset.BandQ;
            _bands[i].GainDb = gains[i];
        }

        _equalizer.Update();
        _applied = gains;
        Log.Debug("EQ 均衡器：" + Describe(gains));
    }

    public int Read(Span<float> buffer)
    {
        if (!Enabled) return buffer.Length;

        // 全 0 dB = 平坦响应，直接直通（引擎也会把本模块移出处理链，这里是第二道保险）
        if (EqPreset.IsFlat(_applied)) return buffer.Length;

        _equalizer.Process(buffer);
        return buffer.Length;
    }

    /// <summary>预设名（界面与日志共用），未知值按「自然」处理。</summary>
    public static string StyleName(ToneStyle style) => style switch
    {
        ToneStyle.Bright => "清亮",
        ToneStyle.Warm => "沉稳",
        ToneStyle.Deep => "深邃",
        ToneStyle.Sharp => "尖锐",
        ToneStyle.Ethereal => "空灵",
        _ => "自然",
    };

    /// <summary>增益表 -> 简短文字（只列出非零段，日志里比一排 0 好读）。</summary>
    private static string Describe(float[] gains)
    {
        var parts = new List<string>();
        for (var i = 0; i < gains.Length; i++)
        {
            if (Math.Abs(gains[i]) < 0.01f) continue;
            parts.Add($"{EqPreset.LabelOf(i)}Hz {(gains[i] > 0 ? "+" : string.Empty)}{gains[i]:0.#}dB");
        }

        return parts.Count == 0 ? "平坦（直通）" : string.Join("，", parts);
    }

    private static bool Same(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 0.001f) return false;
        }

        return true;
    }
}
