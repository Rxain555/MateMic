using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MateMic.Core;

namespace MateMic.Ui;

/// <summary>
/// 窗口外观：亚克力（Acrylic）背景材质 + 深色标题栏。
///
/// ⚠ **颜色不在这里**：深浅两套配色与所有画刷的写入口都在 <see cref="ThemeManager"/>。
/// 本文件只负责 DWM 层面的窗口属性（材质、圆角、边框、深浅渲染），不碰任何界面配色。
///
/// 做法是 Windows 11 的"系统背景材质"（DwmSetWindowAttribute +
/// DWMWA_SYSTEMBACKDROP_TYPE）。它把桌面内容采样、模糊后作为窗口背景合成，
/// 属于系统级合成，比 WPF 里用 <c>AllowsTransparency</c> 自己糊一层要省电，
/// 也不会破坏 ClearType 与硬件加速（AllowsTransparency 会强制软件渲染路径）。
///
/// **失败必须能安全退化**：Windows 10 与部分远程桌面/虚拟显卡环境不支持该属性，
/// 调用会失败。此时关掉透明（IsAcrylicActive = false），
/// 主题里的半透明画刷会叠在窗口默认的不透明背景上，界面依然完整可用，
/// 只是没有磨砂效果——绝不出现"因为没材质所以窗口变全透明/看不见"的情况。
/// </summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmWindowCornerPreferenceRound = 2;

    /// <summary>
    /// DWMWA_COLOR_NONE：让系统用默认颜色。
    /// **这个值很关键**：Windows 11 会用窗口边框色去渲染标题栏的最小化/最大化/关闭三个按钮，
    /// 一旦边框色是深色，三个按钮就被染成深色、贴在深色标题栏上看不见
    /// （用户报的"最小化按钮不见了"就是这么来的）。
    /// </summary>
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE 的取值。</summary>
    private const int BackdropAuto = 0;
    private const int BackdropNone = 1;
    private const int BackdropMainWindow = 2;   // Mica
    private const int BackdropTransientWindow = 3;  // Acrylic
    private const int BackdropTabbedWindow = 4;  // Mica Alt

    private enum DwmWindowAttribute
    {
        UseImmersiveDarkMode = DwmwaUseImmersiveDarkMode,
        WindowCornerPreference = DwmwaWindowCornerPreference,
        BorderColor = DwmwaBorderColor,
        CaptionColor = DwmwaCaptionColor,
        SystemBackdropType = DwmwaSystemBackdropType,
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute,
        ref int value, int size);

    /// <summary>本次运行是否成功启用了亚克力材质。主题据此决定用半透明还是不透明画刷。</summary>
    public static bool IsAcrylicActive { get; private set; }

    /// <summary>
    /// 只更新"窗口按深色还是浅色渲染"这一个属性（DWMWA_USE_IMMERSIVE_DARK_MODE）。
    /// 用户切换深色模式时调用：界面配色换了，窗口边框与阴影要跟着换。
    /// 窗口句柄还没建好时静默跳过（此时 ApplyAcrylic 稍后会统一设置）。
    /// </summary>
    public static void SetImmersiveDarkMode(Window window, bool dark)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmWindowAttribute.UseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn("设置窗口深浅渲染属性失败（不影响使用）：" + ex.Message);
        }
    }

    /// <summary>
    /// 给窗口套上亚克力材质。必须在窗口有句柄之后调用（SourceInitialized / Loaded 均可）。
    /// </summary>
    /// <returns>true = 材质已启用；false = 环境不支持，已退化为不透明。</returns>
    public static bool ApplyAcrylic(Window window, bool darkUi = false)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                Log.Warn("窗口句柄尚未创建，亚克力材质未启用。");
                IsAcrylicActive = false;
                return false;
            }

            // 圆角：与主题里的 CornerRadius 观感一致（Windows 11 由系统负责裁剪窗口外框）
            var corners = DwmWindowCornerPreferenceRound;
            DwmSetWindowAttribute(handle, DwmWindowAttribute.WindowCornerPreference, ref corners, sizeof(int));

            // 边框与标题栏一律交回系统默认色。
            // 踩过的坑：给窗口设了深色边框色之后，Windows 11 会拿它去画标题栏的
            // 最小化/关闭按钮，结果按钮和背景糊成一片、看起来像"按钮消失了"。
            // 这两个属性显式设成 DWMWA_COLOR_NONE，保证按钮用系统默认前景色。
            var none = DwmColorNone;
            DwmSetWindowAttribute(handle, DwmWindowAttribute.BorderColor, ref none, sizeof(int));
            DwmSetWindowAttribute(handle, DwmWindowAttribute.CaptionColor, ref none, sizeof(int));

            // 窗口按深色还是浅色渲染：DWM 用它决定窗口边框、阴影与非客户区文字的颜色。
            // 本程序的标题栏是自绘的，所以它主要影响边框与阴影——
            // 深色界面配一圈浅色边框会很割裂，因此这里跟随界面自身的深浅。
            var darkMode = darkUi ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmWindowAttribute.UseImmersiveDarkMode, ref darkMode, sizeof(int));

            // 亚克力（Acrylic = 3）。Mica（2）更省电但只对"不透明背景"有完整表现，
            // 半透明卡片叠在 Mica 上会显得发灰，因此这里选亚克力。
            var backdrop = BackdropTransientWindow;
            var hr = DwmSetWindowAttribute(handle, DwmWindowAttribute.SystemBackdropType, ref backdrop, sizeof(int));

            if (hr == 0)
            {
                IsAcrylicActive = true;
                Log.Info("已启用亚克力（Acrylic）窗口材质。");
                return true;
            }

            Log.Warn($"系统不支持亚克力材质（DwmSetWindowAttribute=0x{hr:X8}），" +
                     "已退化为不透明背景。该特性需要 Windows 11 22H2 及以上版本。");
            IsAcrylicActive = false;
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("启用亚克力材质失败（已退化为不透明背景）：" + ex.Message);
            IsAcrylicActive = false;
            return false;
        }
    }

    /// <summary>Windows 11 22H2（build 22621）起才支持 DWMWA_SYSTEMBACKDROP_TYPE。</summary>
    public static bool IsBackdropSupported => Environment.OSVersion.Version.Build >= 22621;

    // ---- 隐藏窗口的兜底手段（开机自启收托盘用，见 MainWindow.BeginHideToTray） ----

    private const int SwHide = 0;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>
    /// 窗口在**系统层面**是否真的可见（直接读 HWND 的 WS_VISIBLE 位）。
    ///
    /// ⚠ 不能用 WPF 的 <see cref="Window.IsVisible"/> 来核对这件事：那个属性只是窗口自己的
    /// 依赖属性状态，`Hide()` 一调用它就变成 false，而 HWND 上的 WS_VISIBLE 位可能还没清掉。
    /// 排查"开机自启后留下一个纯黑窗口"时，正是这个区别让问题一度看起来像"已经藏好了"。
    /// </summary>
    public static bool IsWindowReallyVisible(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            return handle != IntPtr.Zero && IsWindowVisible(handle);
        }
        catch (Exception ex)
        {
            Log.Warn("读取窗口真实可见性失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 用 Win32 直接隐藏窗口，**只作为 WPF 侧 Hide() 失效时的兜底**。
    ///
    /// ⚠ 它会绕过 WPF 的可见性状态机，因此必须在 `Window.Hide()` 之后调用：
    /// 先让 WPF 把内部状态改掉，再补这一刀把 HWND 的 WS_VISIBLE 真正清掉。
    /// 反过来单独用会让 WPF 认为窗口还开着，后续 `Show()` 会直接返回、窗口再也显示不出来。
    /// </summary>
    public static bool ForceHide(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;
            return ShowWindow(handle, SwHide);
        }
        catch (Exception ex)
        {
            Log.Warn("强制隐藏窗口失败：" + ex.Message);
            return false;
        }
    }

    // ---- 标题栏按钮自检用的窗口样式位 ----
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsExLayered = 0x00080000L;

    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpFramechanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private static long GetWindowStyle(IntPtr hwnd)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlStyle).ToInt64() : GetWindowLong32(hwnd, GwlStyle);

    private static long GetWindowExStyle(IntPtr hwnd)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64() : GetWindowLong32(hwnd, GwlExStyle);

    private static void SetWindowStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GwlStyle, new IntPtr(style));
        else SetWindowLong32(hwnd, GwlStyle, unchecked((int)style));
    }

    /// <summary>
    /// 让窗口"只能最小化"：**保留最小化按钮、去掉最大化按钮、不能拉伸**。
    ///
    /// ⚠ 当前**没有被调用**。标题栏已经改成自绘（`WindowChrome` + `WindowStyle="None"`），
    /// 最小化/关闭按钮由我们自己在 XAML 里画，不再依赖系统标题栏，所以这里不再需要它。
    ///
    /// 之所以留着：它是"回到系统标题栏"那条路的完整方案，将来若因为某些原因
    /// （例如需要系统级的窗口贴靠面板）想改回系统标题栏，直接用这个方法即可。
    ///
    /// 附上踩过的两个坑：
    ///   ① WPF 的 <c>ResizeMode="NoResize"</c> 会把 <c>WS_MINIMIZEBOX</c> 和
    ///      <c>WS_MAXIMIZEBOX</c> **一起**清掉，于是标题栏上连最小化按钮都不画，
    ///      只剩关闭按钮（用户报的"最小化不见了"就是这个）。实测样式位确认。
    ///   ② **光在运行时改样式不够**：窗口已经带着"可最大化"的样式被创建过，
    ///      DWM 的标题栏按创建时的状态绘制，事后清掉 WS_MAXIMIZEBOX 仍然会画出
    ///      一个能点的最大化按钮（截图实测）。所以 XAML 必须用
    ///      <c>ResizeMode="CanMinimize"</c> 创建，它天生就是"有最小化、没有最大化"。
    /// </summary>
    public static void ApplyMinimizeOnlyChrome(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                Log.Warn("设置窗口按钮时句柄为空，跳过。");
                return;
            }

            var style = GetWindowStyle(hwnd);

            // 期望状态：最小化在、最大化不在、不可拉伸
            var desired = (style | WsMinimizeBox) & ~WsMaximizeBox & ~WsThickFrame;
            if (desired != style)
            {
                SetWindowStyle(hwnd, desired);

                // 样式改了要让非客户区重算一次
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNomove | SwpNosize | SwpNozorder | SwpFramechanged);
            }

            Log.Info("窗口按钮：已确保「保留最小化、去掉最大化、不可拉伸」。");
        }
        catch (Exception ex)
        {
            Log.Warn("调整窗口按钮样式失败（不影响使用）：" + ex.Message);
        }
    }

    /// <summary>
    /// 记录并核对窗口自身的按钮样式位。
    ///
    /// 标题栏是**自绘**的，但样式位依然要紧 —— 它决定任务栏与 DWM 怎么对待这个窗口：
    ///   · `WS_MINIMIZEBOX` **必须有**（XAML 里 `ResizeMode="CanMinimize"` 才会有）：
    ///     少了它，点任务栏图标不会最小化；
    ///   · `WS_CAPTION` **必须有**（`WindowStyle="SingleBorderWindow"` 才会有）：
    ///     少了它，**最小化/还原就没有 DWM 过渡动画**（窗口"一下就没了"）。
    ///     可见边框由 WindowChrome 的 CaptionHeight=0 去掉，所以带 WS_CAPTION 也不会露出系统标题栏；
    ///   · `WS_MAXIMIZEBOX` 必须没有 —— 固定尺寸的前提；
    ///   · `WS_THICKFRAME` 必须没有 —— 不允许拉伸。
    /// </summary>
    public static void LogCaptionButtonState(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                Log.Warn("窗口样式自检：窗口句柄为空。");
                return;
            }

            var style = GetWindowStyle(hwnd);
            var hasCaption = (style & WsCaption) == WsCaption;
            var hasMinimize = (style & WsMinimizeBox) != 0;
            var hasMaximize = (style & WsMaximizeBox) != 0;
            var resizable = (style & WsThickFrame) != 0;
            var exStyle = GetWindowExStyle(hwnd);
            var layered = (exStyle & WsExLayered) != 0;

            Log.Info($"窗口样式自检：WS_CAPTION={hasCaption}（应为 True，最小化/还原的 DWM 动画靠它；" +
                     $"可见边框由 WindowChrome 去掉），最小化按钮={hasMinimize}（应为 True），" +
                     $"最大化按钮={hasMaximize}（应为 False），可拉伸边框={resizable}（应为 False）");
            Log.Info($"窗口扩展样式自检：分层窗口(WS_EX_LAYERED)={layered}（应为 False，分层窗口没有最小化动画）。");

            if (hasMaximize) Log.Warn("窗口样式自检：窗口仍可最大化，与「固定尺寸」的设定不符。");
            if (!hasCaption)
            {
                Log.Warn("窗口样式自检：缺少 WS_CAPTION —— 最小化/还原将没有 DWM 过渡动画" +
                         "（MainWindow.xaml 的 WindowStyle 应为 SingleBorderWindow）。");
            }
            if (!hasMinimize)
            {
                Log.Warn("窗口样式自检：缺少 WS_MINIMIZEBOX —— 点任务栏按钮将无法最小化" +
                         "（MainWindow.xaml 的 ResizeMode 应为 CanMinimize）。");
            }
            if (layered)
            {
                Log.Warn("窗口样式自检：窗口是分层窗口（WS_EX_LAYERED）—— 这类窗口没有最小化/还原动画。");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("窗口样式自检失败：" + ex.Message);
        }
    }

    // 配色已全部移交 Ui/ThemeManager.cs：
    //   · 原来的 IsSystemDarkMode() 只用来决定标题栏文字深浅，现在改由用户自己的
    //     「深色模式」开关决定（见 MainWindow.OnDarkModeToggled）；
    //   · 原来的 PublishToResources() 只处理"亚克力是否生效"那几支画刷，
    //     现在由 ThemeManager.Apply(resources, dark, acrylicActive) 一并处理。
}