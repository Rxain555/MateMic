namespace MateMic.Core;

/// <summary>
/// EQ 均衡器的频段定义与各预设的增益表。
///
/// **为什么放在 Core 而不是 Dsp**：配置层（<see cref="ToneSettings.Gains"/>）要知道频段数量与
/// 预设曲线，而 Dsp 是引用 Core 的 —— 表放这里两边都能用，也不会形成 Core → Dsp 的反向依赖。
///
/// 频段取 ISO 标准、每段间隔一个倍频程：31 / 62 / 125 / 250 / 500 / 1k / 2k / 4k / 8k / 16k Hz。
/// 10 段是这个界面宽度下能排得下的最大段数（每段约占 34 DIP）。
/// </summary>
public static class EqPreset
{
    /// <summary>各段中心频率（Hz）。索引即推子顺序，从低到高。</summary>
    public static readonly float[] Frequencies = { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    public static int BandCount => Frequencies.Length;

    public const float MinGainDb = -12f;
    public const float MaxGainDb = 12f;

    /// <summary>
    /// 每段的 Q。相邻段相隔一个倍频程时，Q≈1.41 正好让相邻段在 -3 dB 处相接，
    /// 既不出现"缝隙"也不会互相糊成一片（图形均衡器的常用取值）。
    /// </summary>
    public const float BandQ = 1.41f;

    /// <summary>推子下方的频率标签。</summary>
    public static string LabelOf(int index) => index switch
    {
        0 => "31",
        1 => "62",
        2 => "125",
        3 => "250",
        4 => "500",
        5 => "1k",
        6 => "2k",
        7 => "4k",
        8 => "8k",
        9 => "16k",
        _ => "?",
    };

    /// <summary>平坦（全 0 dB）。</summary>
    public static float[] Flat() => new float[BandCount];

    /// <summary>取某个预设的 10 段增益。**返回副本**，调用方可以随便改。</summary>
    public static float[] GainsOf(ToneStyle style) => (float[])Table(style).Clone();

    /// <summary>是否等于平坦响应（全 0 dB 视为"不处理"，模块据此退出处理链）。</summary>
    public static bool IsFlat(float[]? gains)
    {
        if (gains == null || gains.Length != BandCount) return true;
        foreach (var gain in gains)
        {
            if (Math.Abs(gain) > 0.01f) return false;
        }

        return true;
    }

    /// <summary>把任意长度/为 null 的增益表规整成合法的 10 段表（不合法一律当平坦）。</summary>
    public static float[] Normalize(float[]? gains)
        => gains != null && gains.Length == BandCount ? (float[])gains.Clone() : Flat();

    /// <summary>
    /// 各预设的 10 段增益（dB）。由原来那套 3 段滤波（Peaking + Shelf）的频响
    /// **采样到 10 个中心频率**得到，听感尽量贴近原来的预设；
    /// 但结构由"3 段混合滤波"统一成"10 段 peaking"后，细节必然有差别。
    ///
    /// 增益量级刻意给得比教科书值大（原来只有 ±1.5–3 dB，实测用户根本听不出区别），
    /// 现在维持 ±4–8 dB 的可感知区间。
    /// </summary>
    private static float[] Table(ToneStyle style) => style switch
    {
        // 清亮：抬 4–8 kHz 的空气感，压低 300 Hz 以下的浑浊
        ToneStyle.Bright => new[] { -3f, -3f, -3f, -3.5f, -1f, 0f, 2f, 6f, 5f, 3f },

        // 沉稳：抬 125–250 Hz 的厚度，削掉刺耳的高频
        ToneStyle.Warm => new[] { 0f, 2f, 5f, 5f, 2f, 0f, -1f, -3f, -7f, -7f },

        // 深邃：加重低频，明显衰减 1–4 kHz 的存在感
        ToneStyle.Deep => new[] { 4f, 7f, 8f, 5f, 1f, -3f, -7f, -6f, -6f, -6f },

        // 尖锐：抬 4–16 kHz 的清晰度
        ToneStyle.Sharp => new[] { 0f, 0f, 0f, 0f, 0f, 1f, 4f, 7f, 8f, 6f },

        // 空灵：极高频 +3，轻抬 2–8 kHz，衰减 500 Hz 一带
        ToneStyle.Ethereal => new[] { 0f, 0f, 0f, -1f, -2f, -1f, 0f, 1f, 2f, 3f },

        // 自然：平坦（不改变音色）
        _ => Flat(),
    };
}
