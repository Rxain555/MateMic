using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MateMic.Core;

namespace MateMic.Ui;

/// <summary>
/// 托盘右键菜单：Windows 11 风格的自绘菜单（参考 TranslucentTB 的 MenuFlyout 观感）。
///
/// 为什么自绘而不是用系统菜单：
///   · WinForms 的 <c>ContextMenuStrip</c> 是 ToolStrip 渲染（方角、左侧图标栏、旧配色），
///     做不出圆角与亚克力；
///   · 系统原生 <c>TrackPopupMenu</c> 菜单虽然也是圆角（实测 Win11 确实会），
///     但它的颜色完全由系统决定，**跟不上本应用的「深色模式」开关**。
/// 自绘则能直接复用主题画刷，浅色/深色与主界面完全一致。
///
/// 交互对齐系统菜单的习惯：
///   · 点菜单外 / 切到别的窗口 → 关闭（<see cref="OnDeactivated"/>）
///   · Esc → 关闭；上下键 → 在菜单项之间移动；Enter / 空格 → 触发
///   · 菜单从光标位置弹出，底部托盘处默认**向上**展开，贴边时自动夹回工作区
/// </summary>
public partial class TrayMenuWindow : Window
{
    /// <summary>当前正在显示的菜单：同时只允许一个，重复右键不会叠出多个。</summary>
    private static TrayMenuWindow? _open;

    /// <summary>防止"关闭"与"执行动作"互相触发（关闭会引发 Deactivated）。</summary>
    private bool _closing;

    /// <summary>低层鼠标钩子句柄；菜单打开期间才装着，Zero 表示没装。</summary>
    private IntPtr _mouseHook = IntPtr.Zero;

    /// <summary>必须持有委托实例：否则会被 GC 回收，钩子回调直接崩（与 Core\ShortcutKeyCapture 同理）。</summary>
    private LowLevelMouseProc? _mouseProc;

    /// <summary>点了「显示主窗口」。</summary>
    public event EventHandler? ShowMainRequested;

    /// <summary>点了「开关音频处理」。</summary>
    public event EventHandler? ToggleProcessingRequested;

    /// <summary>点了「退出 MateMic」。</summary>
    public event EventHandler? ExitRequested;

    private TrayMenuWindow(bool processingEnabled)
    {
        InitializeComponent();
        ToggleCheck.Visibility = processingEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 在鼠标当前位置弹出菜单。再次调用会先关掉上一个。
    /// </summary>
    /// <param name="processingEnabled">总开关当前状态：决定「开关音频处理」是否显示勾。</param>
    /// <remarks>
    /// **刻意不设 Owner**：程序「关闭到托盘」时主窗口是隐藏状态，
    /// 而 WPF 会把属于隐藏窗口的附属窗口一起隐藏 —— 那样从托盘弹菜单会什么都看不到。
    /// 置顶（Topmost）已经保证了层级，退出时由 <see cref="CloseOpen"/> 收尾。
    /// </remarks>
    public static TrayMenuWindow Open(bool processingEnabled)
    {
        CloseOpen();

        var menu = new TrayMenuWindow(processingEnabled);
        _open = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, menu)) _open = null;
        };
        menu.ShowNearCursor();
        return menu;
    }

    /// <summary>关掉正在显示的菜单（没有就什么都不做）。</summary>
    public static void CloseOpen()
    {
        var open = _open;
        _open = null;
        try { open?.Close(); } catch { /* 忽略 */ }
    }

    /// <summary>在光标处定位并淡入。定位前先量尺寸，避免窗口先在默认位置闪一下。</summary>
    private void ShowNearCursor()
    {
        GetCursorPos(out var cursor);

        Show();
        UpdateLayout();

        // 屏幕坐标是物理像素，WPF 的 Left/Top 是设备无关单位，跨缩放时必须换算
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = ActualWidth;
        var height = ActualHeight;
        var x = cursor.X / dpi.DpiScaleX;
        var y = cursor.Y / dpi.DpiScaleY;

        // 托盘一般在屏幕底部：上方放得下就向上展开（贴着任务栏往上长）
        var area = SystemParameters.WorkArea;
        const double gap = 6;
        var openUpward = y - height - gap >= area.Top;

        var left = x - 8;
        var top = openUpward ? y - height - gap : y + gap;

        // 贴边时夹回工作区，不允许菜单跑出屏幕
        left = Math.Clamp(left, area.Left + 4, Math.Max(area.Left + 4, area.Right - width - 4));
        top = Math.Clamp(top, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - height - 4));
        Left = left;
        Top = top;

        // 淡入 + 从光标方向轻微滑入（140ms，系统菜单也是这个量级的短动画）
        Slide.Y = openUpward ? 8 : -8;
        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(Slide.Y, 0, duration) { EasingFunction = ease });

        // 打开时**不**预选任何一项（与系统菜单一致）：高亮只在鼠标移入或按上下键之后出现。
        // 否则菜单一出现就有一项处于"选中"状态，顺手按个回车就把那一项触发了。
        Focus();

        // 托盘右键的输入属于本进程，因此这里可以正当地把菜单设为前台窗口。
        // **只有成为前台窗口，点别处时才会收到 Deactivated** —— 否则菜单会赖着不走
        // （2026-10-03 用户报的"点菜单外不消失"）。拿不到前台权限时由鼠标钩子兜底。
        var hwnd = new WindowInteropHelper(this).Handle;
        var toForeground = SetForegroundWindow(hwnd);
        InstallMouseHook();
        Log.Info($"托盘菜单：已显示（{width:0}×{height:0}，置前台={toForeground}，IsActive={IsActive}）");
    }

    private void OnDeactivated(object? sender, EventArgs e) => CloseOnce();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseOnce();
                e.Handled = true;
                break;

            case Key.Up:
                MoveFocus(-1);
                e.Handled = true;
                break;

            case Key.Down:
                MoveFocus(1);
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Space:
                // WPF 的 Button 默认只认空格，菜单习惯上 Enter 也该触发
                if (Keyboard.FocusedElement is Button focused)
                {
                    focused.RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>上下键在菜单项之间移动焦点（越过两端则循环）。</summary>
    private void MoveFocus(int delta)
    {
        var items = new List<Button> { ShowItem, ToggleItem, ExitItem };
        var index = items.FindIndex(b => b.IsKeyboardFocused);
        if (index < 0) index = delta > 0 ? -1 : 0;
        var next = ((index + delta) % items.Count + items.Count) % items.Count;
        items[next].Focus();
    }

    private void OnShowClick(object sender, RoutedEventArgs e) => Invoke(ShowMainRequested);

    private void OnToggleClick(object sender, RoutedEventArgs e) => Invoke(ToggleProcessingRequested);

    private void OnExitClick(object sender, RoutedEventArgs e) => Invoke(ExitRequested);

    /// <summary>
    /// 先关菜单再执行动作：动作可能弹对话框（会被 Deactivated 连带关掉菜单）
    /// 或者直接退出程序，顺序反了会留下一个关不掉的空菜单。
    /// </summary>
    private void Invoke(EventHandler? handler)
    {
        if (_closing) return;
        CloseOnce();
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("托盘菜单动作执行失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 只关一次。<see cref="Window.Close"/> 在窗口已进入关闭流程时再调用会抛
    /// <see cref="InvalidOperationException"/> —— 实测：点菜单项时 Close 会引发 Deactivated，
    /// 那条路径又会调一次 Close（日志里表现为"未处理的界面异常"）。
    /// </summary>
    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        RemoveMouseHook();
        try { Close(); } catch { /* 已经在关闭流程里，忽略 */ }
    }

    /// <summary>兜底再收一次钩子：窗口若因别的原因关闭（例如程序退出），也不能把钩子留在这。</summary>
    protected override void OnClosed(EventArgs e)
    {
        RemoveMouseHook();
        base.OnClosed(e);
    }

    // ---------------------------------------------------------------- 点菜单外即关（兜底路径）
    //
    // 为什么不能只靠 Deactivated：菜单未必拿得到前台权限 —— 那时它永远不会失活，
    // 点别处也就永远不关。低层鼠标钩子只"观察"不"拦截"（不会吞掉那次点击），
    // 点到菜单矩形之外就关，与系统菜单的行为一致。

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct HookPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public HookPoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

    private void InstallMouseHook()
    {
        try
        {
            _mouseProc = OnLowLevelMouse;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                Log.Warn("托盘菜单：鼠标钩子安装失败，点菜单外关闭将只依赖窗口失活。");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("托盘菜单：安装鼠标钩子异常：" + ex.Message);
        }
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_mouseHook); } catch { /* 忽略 */ }
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
    }

    private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                if (!IsInsideMenu(info.Point))
                {
                    // 钩子回调里不要直接关窗口（此刻还在消息处理中），交给 Dispatcher 收尾
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_closing) return;
                        Log.Info("托盘菜单：点到菜单外，关闭。");
                        CloseOnce();
                    }));
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsInsideMenu(HookPoint point)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;
        return point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);
}
