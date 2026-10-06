using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MateMic.Ui;

/// <summary>
/// 深色 / 浅色配色的**单一来源**。
///
/// 为什么配色写在 C# 而不是 XAML 里：
///   主题键有一百多处引用，全部改成 <c>DynamicResource</c> 之后，换肤就等价于
///   "把 <c>Application.Resources</c> 里的画刷换掉"。两套配色平铺在代码里，
///   一眼就能对照着调；如果拆成两个 XAML 字典再在运行时合并/卸载，
///   反而要处理"哪一份生效、卸载有没有残留"的问题。
///
/// 与 <c>Ui/Theme.Modern.xaml</c> 的分工：
///   · XAML 里的配色是**浅色默认值**——在 <see cref="Apply"/> 被调用之前，
///     界面已经是正确的浅色（也是主题回退到 Legacy 时的兜底）；
///   · 本类在启动时（以及用户切换深色模式时）覆写这些键。
///   两边的浅色值必须保持一致，改配色时两个地方都要看。
///
/// 亚克力：窗口材质生效时用半透明画刷（桌面模糊透上来），
///   不支持材质时用不透明画刷兜底。因此 <see cref="Apply"/> 需要知道材质是否生效。
/// </summary>
public static class ThemeManager
{
    /// <summary>当前是否为深色配色。供对话框等"运行时取色"的地方使用。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>
    /// 把指定配色写进资源字典。调用方通常是 <c>MainWindow</c>：
    /// 构造时（此时材质还没开，传 false）与材质确定之后（OnLoaded）各调一次。
    /// </summary>
    public static void Apply(ResourceDictionary resources, bool dark, bool acrylicActive)
    {
        IsDark = dark;

        var palette = dark ? Dark(acrylicActive) : Light(acrylicActive);
        foreach (var (key, hex) in palette)
        {
            resources[key] = FrozenBrush(hex);
        }

        // 强调色的 Color 版本（个别地方按 Color 取用）
        resources["AccentColor"] = dark ? Rgb("#4C93F5") : Rgb("#2F7DF6");

        // 阴影：深色底上改用黑色投影。浅色底上用中性灰（用纯黑会显脏）。
        var shadowColor = dark ? "#FF000000" : "#FF7A8290";
        resources["CardShadow"] = Shadow(shadowColor, blur: 12, depth: 1.5, opacity: dark ? 0.5 : 0.13);
        resources["ControlShadow"] = Shadow(shadowColor, blur: 6, depth: 1, opacity: dark ? 0.42 : 0.12);
        resources["TopBarShadow"] = Shadow(shadowColor, blur: 16, depth: 3, opacity: dark ? 0.55 : 0.14);
    }

    /// <summary>
    /// 按资源键取色，返回 <c>0xAARRGGBB</c>（对话框那套自绘控件按整数传色值，且需要 alpha，
    /// 因为深色主题里的边框色是半透明白）。资源缺失或非深色模式时返回 <paramref name="fallbackArgb"/>。
    /// </summary>
    public static uint Argb(string key, uint fallbackArgb)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            var c = brush.Color;
            return (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
        }

        return fallbackArgb;
    }

    // ================================================================ 浅色

    private static Dictionary<string, string> Light(bool acrylic) => new()
    {
        // 窗口与卡片面
        ["WindowBrush"] = acrylic ? "#F2FFFFFF" : "#F5F6F8",
        ["AcrylicWindowTintBrush"] = acrylic ? "#F2FFFFFF" : "#F5F6F8",
        ["AcrylicTopBarBrush"] = acrylic ? "#F7FFFFFF" : "#FFFFFF",
        ["AcrylicGroupBrush"] = acrylic ? "#F2FFFFFF" : "#FFFFFF",
        ["AcrylicCardBrush"] = "#FFFFFFFF",
        ["CardBrush"] = "#FFFFFFFF",
        ["CardGradientBrush"] = "#FFFFFFFF",
        ["WindowSurfaceBrush"] = "#F5F6F8",
        ["HeaderSurfaceBrush"] = "#FAFBFC",
        ["GroupHighlightBrush"] = acrylic ? "#59FFFFFF" : "#00FFFFFF",
        ["RowAltBrush"] = "#0FFFFFFF",
        ["LevelMaskBrush"] = acrylic ? "#F0F1F3" : "#EFEFEF",

        // 强调色
        ["AccentBrush"] = "#2F7DF6",
        ["AccentHoverBrush"] = "#1D6AE6",
        ["AccentPressedBrush"] = "#175BC9",
        ["AccentSoftBrush"] = "#E8F1FE",
        ["AccentSoftHoverBrush"] = "#D8E7FD",

        // 文字
        ["TextBrush"] = "#1C1E22",
        ["SubtleTextBrush"] = "#636A76",
        ["DisabledBrush"] = "#AAB0BA",
        ["PlaceholderBrush"] = "#9A9A9A",

        // 控件面
        ["ControlBrush"] = "#EEF0F3",
        ["ControlHoverBrush"] = "#E2E6EC",
        ["ControlPressedBrush"] = "#D4DAE3",
        ["ControlDisabledBrush"] = "#F5F6F8",
        ["TrackBrush"] = "#E7EAEF",
        ["ScrollThumbBrush"] = "#C6CBD3",
        ["PopupSurfaceBrush"] = "#FFFFFFFF",
        // 悬浮说明框（ToolTip）：比卡片更"浮起"一点，靠圆角 + 细边框 + 投影与卡片区分
        ["TooltipSurfaceBrush"] = "#FFFFFFFF",
        ["TooltipBorderBrush"] = "#1A000000",
        ["InputBoxBrush"] = "#EDF0F4",
        ["InputBoxDisabledBrush"] = "#4DFFFFFF",
        ["SwitchTrackBrush"] = "#D8DCE2",
        ["SliderFillBrush"] = "#4C93F5",
        ["ChipBrush"] = "#E9ECF0",
        ["ChipSelectedBrush"] = "#2F7DF6",
        ["PrimaryDisabledBrush"] = "#4D9AA3B2",
        // 播放列表行的"鼠标悬浮"高亮：中性淡色，与「当前播放项」的强调色底、
        // 「播放进度」的强调色条三者可区分（三者会同时叠在同一行上）
        ["RowHoverBrush"] = "#0F000000",

        // 边框
        ["CardBorderBrush"] = "#16000000",
        ["HairlineBrush"] = "#12000000",
        ["GroupBorderBrush"] = "#0D000000",
        ["RegionBackgroundBrush"] = "#07000000",
        ["ControlBorderBrush"] = "#18000000",
        ["ControlHoverBorderBrush"] = "#2A000000",
        // 自绘对话框（DialogHost）的边框：它是无边框窗口**唯一**的轮廓，
        // 不能像卡片边框那样淡到几乎看不见，所以单列一个键。
        ["DialogBorderBrush"] = "#FFD2D7DE",

        // 图形（自绘的箭头 / 叉 / 标题栏按钮）
        ["ComboArrowBrush"] = "#6B7280",
        ["ExpanderArrowBrush"] = "#8A93A3",
        ["RemoveGlyphBrush"] = "#9AA3B0",
        ["IconGlyphBrush"] = "#5A5A5A",
        ["CaptionGlyphBrush"] = "#4A4F57",
        ["CaptionGlyphHoverBrush"] = "#1C1E22",
        ["CaptionHoverBrush"] = "#14000000",
        ["CaptionPressedBrush"] = "#24000000",

        // 数据可视化
        ["SpectrumSlotBrush"] = "#E6E9EE",
        ["LevelNeedleBrush"] = "#2F2F2F",

        // 状态条
        ["WarningBrush"] = "#F2FFF8E1",
        ["WarningBorderBrush"] = "#4DE0B93C",
        ["WarningTextBrush"] = "#6B5400",
    };

    // ================================================================ 深色

    /// <summary>
    /// 深色配色：以中性偏冷的深灰为底（不是纯黑，纯黑配白字对比过强、久看刺眼），
    /// 卡片比窗口底色**更亮**来表达"浮起"（浅色主题里靠"更白"表达同一件事）。
    /// 强调色在深底上加深了亮度，保证 4.5:1 以上的对比度。
    /// </summary>
    private static Dictionary<string, string> Dark(bool acrylic) => new()
    {
        // 窗口与卡片面
        ["WindowBrush"] = acrylic ? "#F2181A1F" : "#FF1A1C21",
        ["AcrylicWindowTintBrush"] = acrylic ? "#F2181A1F" : "#FF1A1C21",
        ["AcrylicTopBarBrush"] = acrylic ? "#F7202329" : "#FF202329",
        ["AcrylicGroupBrush"] = acrylic ? "#F21E2126" : "#FF1E2126",
        ["AcrylicCardBrush"] = "#FF24272D",
        ["CardBrush"] = "#FF24272D",
        ["CardGradientBrush"] = "#FF24272D",
        ["WindowSurfaceBrush"] = "#FF1A1C21",
        ["HeaderSurfaceBrush"] = "#FF1F2228",
        ["GroupHighlightBrush"] = acrylic ? "#14FFFFFF" : "#00FFFFFF",
        ["RowAltBrush"] = "#0AFFFFFF",
        ["LevelMaskBrush"] = "#2A2E35",

        // 强调色（深底上提亮一档）
        ["AccentBrush"] = "#4C93F5",
        ["AccentHoverBrush"] = "#67A6F8",
        ["AccentPressedBrush"] = "#3B84E6",
        ["AccentSoftBrush"] = "#2E4C93F5",
        ["AccentSoftHoverBrush"] = "#454C93F5",

        // 文字
        ["TextBrush"] = "#E6E9ED",
        ["SubtleTextBrush"] = "#A3AAB5",
        ["DisabledBrush"] = "#6C737E",
        ["PlaceholderBrush"] = "#7C838E",

        // 控件面
        ["ControlBrush"] = "#FF2C3138",
        ["ControlHoverBrush"] = "#FF363C44",
        ["ControlPressedBrush"] = "#FF40474F",
        ["ControlDisabledBrush"] = "#FF262A30",
        ["TrackBrush"] = "#FF3A4048",
        ["ScrollThumbBrush"] = "#FF4C525B",
        ["PopupSurfaceBrush"] = "#FF2A2E35",
        // 悬浮说明框：深色下比卡片（#24272D）**更亮**，与浅色下"更白"表达同一件事——浮起
        ["TooltipSurfaceBrush"] = "#FF31363F",
        ["TooltipBorderBrush"] = "#26FFFFFF",
        ["InputBoxBrush"] = "#FF2C3138",
        ["InputBoxDisabledBrush"] = "#FF1E2228",
        ["SwitchTrackBrush"] = "#FF454B54",
        ["SliderFillBrush"] = "#4C93F5",
        ["ChipBrush"] = "#FF2C3138",
        ["ChipSelectedBrush"] = "#4C93F5",
        ["PrimaryDisabledBrush"] = "#4D6C737E",
        ["RowHoverBrush"] = "#14FFFFFF",

        // 边框：深色下改用"极淡的白"收边，而不是黑
        ["CardBorderBrush"] = "#14FFFFFF",
        ["HairlineBrush"] = "#12FFFFFF",
        ["GroupBorderBrush"] = "#0FFFFFFF",
        ["RegionBackgroundBrush"] = "#08FFFFFF",
        ["ControlBorderBrush"] = "#1AFFFFFF",
        ["ControlHoverBorderBrush"] = "#33FFFFFF",
        ["DialogBorderBrush"] = "#FF3E444D",

        // 图形
        ["ComboArrowBrush"] = "#A3AAB5",
        ["ExpanderArrowBrush"] = "#98A0AC",
        ["RemoveGlyphBrush"] = "#97A0AC",
        ["IconGlyphBrush"] = "#A3AAB5",
        ["CaptionGlyphBrush"] = "#C6CCD5",
        ["CaptionGlyphHoverBrush"] = "#FFFFFF",
        ["CaptionHoverBrush"] = "#1FFFFFFF",
        ["CaptionPressedBrush"] = "#2EFFFFFF",

        // 数据可视化
        ["SpectrumSlotBrush"] = "#FF33383F",
        ["LevelNeedleBrush"] = "#FFF0F2F5",

        // 状态条：深底上的琥珀色提示
        ["WarningBrush"] = "#FF2E2A20",
        ["WarningBorderBrush"] = "#66E0B93C",
        ["WarningTextBrush"] = "#EFD9A0",
    };

    // ================================================================ 工具

    private static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush(Rgb(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 投影。深色主题里投影颜色是黑的、透明度更高，浅色里是中性灰、很淡。
    /// 每次换肤都新建实例（<c>Effect</c> 是 Freezable，不能改已冻结的属性）。
    /// </summary>
    private static DropShadowEffect Shadow(string color, double blur, double depth, double opacity)
    {
        var effect = new DropShadowEffect
        {
            BlurRadius = blur,
            ShadowDepth = depth,
            Direction = 270,
            Color = Rgb(color),
            Opacity = opacity,
            RenderingBias = RenderingBias.Quality,
        };
        effect.Freeze();
        return effect;
    }
}
