using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MateMic.Core;

namespace MateMic.Ui;

/// <summary>
/// 应用风格的自绘对话框，用来替代系统 MessageBox（蓝色图标、左右留白不均、旧式按钮）。
/// 视觉树全部在构造函数里用代码搭好：启动早期还没有主窗口、App.xaml 资源也可能没加载，
/// 这里刻意不引用任何 App 资源，保证任何时刻都能弹出来。
/// </summary>
public sealed class DialogHost : Window
{
    // 颜色一律取自主题（Ui/ThemeManager.cs），**深浅两种模式都读主题**。
    // 早先浅色下沿用了一组硬编码的"旧主题"色值（0xF3F3F3 / 0x2F80ED…），
    // 结果是对话框与主界面配色不一致（`当前状态.md` 未解决问题 #20），现已统一。
    //
    // 每个 Pick 的第二个参数只是**兜底色值**，仅在主题资源取不到时生效
    // （极端情形：App 资源尚未加载就弹窗，例如启动早期报错）。
    // 兜底色值刻意对齐当前主题的浅色值，这样"没有主题"也不会退回旧观感。
    // 色值一律 0xAARRGGBB——深色主题里边框色是半透明的，不能丢掉 alpha。
    private static uint WindowColor => Pick("CardBrush", 0xFFFFFFFF);
    private static uint LineColor => Pick("DialogBorderBrush", 0xFFD2D7DE);
    private static uint TitleColor => Pick("HeaderSurfaceBrush", 0xFFFAFBFC);
    private static uint TextColor => Pick("TextBrush", 0xFF1C1E22);
    private static uint AccentColor => Pick("AccentBrush", 0xFF2F7DF6);
    private static uint AccentHoverColor => Pick("AccentHoverBrush", 0xFF1D6AE6);
    private static uint AccentPressedColor => Pick("AccentPressedBrush", 0xFF175BC9);
    private static uint ButtonFaceColor => Pick("ControlBrush", 0xFFEEF0F3);
    private static uint ButtonBorderColor => Pick("ControlBorderBrush", 0x18000000);
    private static uint ButtonHoverColor => Pick("ControlHoverBrush", 0xFFE2E6EC);
    private static uint ButtonPressedColor => Pick("ControlPressedBrush", 0xFFD4DAE3);
    private static uint SubtleColor => Pick("SubtleTextBrush", 0xFF636A76);

    private readonly Button? _primary;   // primaryText 传 null 时为 null（无按钮对话框）
    private bool _accepted;

    // ---- 分页（使用指南）----
    /// <summary>分页内容；为空表示这是普通的一次性对话框。</summary>
    private readonly IReadOnlyList<GuideCatalog.Page> _pages = Array.Empty<GuideCatalog.Page>();
    private TextBlock? _bodyBlock;
    /// <summary>正文的滚动宿主：换页时要把它拨回顶部（见 <see cref="UpdatePage"/>）。</summary>
    private ScrollViewer? _bodyScroll;
    private TextBlock? _pageLabel;
    private Button? _prevButton;
    private Button? _nextButton;
    private int _pageIndex;

    private DialogHost(string title, string message, string? secondaryText, string primaryText,
                       IReadOnlyList<GuideCatalog.Page>? pages = null,
                       FrameworkElement? customContent = null,
                       Action? onSecondary = null)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = ResolveUiFont();
        FontSize = 12;

        // 无边框窗口本身是矩形的：把窗口背景做成透明，圆角才真的圆
        // （否则圆角外的四个小角会露出窗口底色，看起来像"圆角没生效"）。
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        _pages = pages ?? Array.Empty<GuideCatalog.Page>();
        var paged = _pages.Count > 0;

        var titleBar = BuildTitleBar(title);

        // 正文用可更新的 TextBlock（分页时要换内容），并套一层滚动：
        // 单页可能写得比较长，没有滚动条窗口会一路撑高、甚至超出屏幕。
        // 正文：默认是可更新的 TextBlock（分页时要换内容）。
        // 传入 customContent 时改用它——"添加 AI 变声"这类需要放下拉框、按钮的对话框
        // 就复用本类的标题栏、描边与按钮样式，不必再手写一个窗口。
        FrameworkElement body;
        if (customContent != null)
        {
            body = customContent;
        }
        else
        {
            _bodyBlock = new TextBlock
            {
                Text = paged ? _pages[0].Body : message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(TextColor),
                FontSize = 12,
                LineHeight = 19,
                // 长文案限制最大宽度，避免窗口拉成一整条
                MaxWidth = 520,
            };

            var scroll = new ScrollViewer
            {
                Content = _bodyBlock,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 380,
            };

            // 滚轮改成逐帧指数平滑（与主界面左栏同一套 Ui\SmoothScroll）：
            // 原生的"一格一跳"用户 2026-10-09 反馈"很生硬"。长文案对话框（风险说明等）一并受益。
            SmoothScroll.SetEnabled(scroll, true);
            _bodyScroll = scroll;

            // 分页（使用指南）时把正文区**钉死成固定尺寸**：
            // 每页长短不一，若让它随内容变化，翻页时整个对话框会重新居中、
            // 底部按钮也跟着在屏幕上跳来跳去，点「下一页」时按钮会从鼠标底下跑掉
            //（用户 2026-10-06 反馈"变来变去、很难受"）。
            if (paged)
            {
                scroll.Width = 520;
                scroll.Height = 360;
            }

            body = scroll;
        }

        var messagePanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(16, 14, 16, 0),
        };
        messagePanel.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (paged)
        {
            // 分页模式下不再需要"取消/确定"，换成翻页 + 关闭
            _prevButton = MakeButton("上一页", primary: false);
            _prevButton.Click += (_, _) => GoToPage(_pageIndex - 1);
            _nextButton = MakeButton("下一页", primary: false);
            _nextButton.Click += (_, _) => GoToPage(_pageIndex + 1);
            buttons.Children.Add(_prevButton);
            buttons.Children.Add(_nextButton);
        }
        else if (secondaryText != null)
        {
            var secondary = MakeButton(secondaryText, primary: false);
            // 传了动作就执行它、不关闭对话框（例如"打开组件文件夹"）；
            // 没传就是常规的"取消"语义。
            secondary.Click += (_, _) =>
            {
                if (onSecondary != null) onSecondary();
                else { _accepted = false; Close(); }
            };
            buttons.Children.Add(secondary);
        }

        // primaryText 传 null 表示**整条对话框不要任何按钮**（进度提示那种纯展示的场合）。
        // 分页模式必须有关闭按钮，所以那时仍然给一个。
        if (paged || primaryText != null)
        {
            _primary = MakeButton(paged ? "关闭" : primaryText!, primary: true);
            _primary.Click += (_, _) => { _accepted = true; Close(); };
            buttons.Children.Add(_primary);
        }
        else
        {
            buttons.Visibility = Visibility.Collapsed;
        }

        _pageLabel = new TextBlock
        {
            Foreground = Brush(SubtleColor),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 底部一行：左边页码、右边按钮（页码为空时自然贴左，不影响普通对话框的观感）
        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(16, 16, 16, 14) };
        DockPanel.SetDock(_pageLabel, Dock.Left);
        footer.Children.Add(_pageLabel);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(titleBar);
        layout.Children.Add(footer);
        layout.Children.Add(messagePanel);

        // 1px 描边：WindowStyle=None 的窗口没有系统边框，靠它勾出可见的边缘
        Content = new Border
        {
            Background = Brush(WindowColor),
            BorderBrush = Brush(LineColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),
            Child = layout,
        };

        if (paged) UpdatePage();

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>切到指定页；越界忽略（按钮在首/末页会禁用，但键盘也能触发，所以这里仍要判）。</summary>
    private void GoToPage(int index)
    {
        if (_pages.Count == 0) return;
        if (index < 0 || index >= _pages.Count) return;

        _pageIndex = index;
        UpdatePage();
    }

    private void UpdatePage()
    {
        if (_pages.Count == 0 || _bodyBlock == null) return;

        var page = _pages[_pageIndex];
        _bodyBlock.Text = page.Body;

        // 换页要回到正文最上面。用户 2026-10-09 反馈："换页不会重置滚动条和内容位置，
        // 我想每次切换新的一页都处在内容最上面"。
        // 必须连平滑滚动的内部记账一起归零 —— 只调 ScrollToVerticalOffset(0) 的话，
        // 逐帧回调还记着旧目标，下一帧就把位置拉回去（见 SmoothScroll.ResetToTop）。
        SmoothScroll.ResetToTop(_bodyScroll);

        if (_pageLabel != null)
            _pageLabel.Text = $"第 {_pageIndex + 1} / {_pages.Count} 页 · {page.Title}";

        // 按钮模板没有 disabled 态，用透明度表达"到头了"
        if (_prevButton != null)
        {
            _prevButton.IsEnabled = _pageIndex > 0;
            _prevButton.Opacity = _pageIndex > 0 ? 1 : 0.45;
        }

        if (_nextButton != null)
        {
            _nextButton.IsEnabled = _pageIndex < _pages.Count - 1;
            _nextButton.Opacity = _pageIndex < _pages.Count - 1 ? 1 : 0.45;
        }
    }

    /// <summary>
    /// 解析界面字体：① App 资源里的 AppFont（主界面用的同一个）→ ② 程序集内嵌的 pack URI → ③ 系统字体。
    ///
    /// ⚠ 为什么不能只写相对 URI：**在代码里构造 <see cref="FontFamily"/> 时没有 XAML 的基 URI**，
    /// "/Assets/Fonts/#MiSans Light" 这种相对路径解析不了，WPF 会**静默**回落到系统字体 ——
    /// 表现就是"主界面字体换了、弹窗没换"（2026-10-03 用户实测发现）。
    /// 因此这里改用绝对 pack URI，并把最终用到的字体写进日志，以后不必靠肉眼判断。
    /// </summary>
    private static FontFamily ResolveUiFont()
    {
        try
        {
            if (Application.Current?.TryFindResource("AppFont") is FontFamily fromResources
                && fromResources.FamilyNames.Count > 0)
            {
                Core.Log.Info("对话框字体：使用 AppFont（MiSans）。");
                return fromResources;
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warn("对话框字体：读取 AppFont 失败，改用内嵌字体。" + ex.Message);
        }

        try
        {
            var embedded = new FontFamily("pack://application:,,,/Assets/Fonts/#MiSans Light");
            if (embedded.FamilyNames.Count > 0)
            {
                Core.Log.Info("对话框字体：使用内嵌 MiSans。");
                return embedded;
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warn("对话框字体：加载内嵌字体失败，改用系统字体。" + ex.Message);
        }

        Core.Log.Warn("对话框字体：回退到系统字体（MiSans 不可用）。");
        return new FontFamily("Microsoft YaHei UI, Segoe UI");
    }


    /// <summary>单按钮提示：确定。</summary>
    public static void Info(Window? owner, string title, string message)        => Show(owner, title, message, null, "确定");

    /// <summary>风险提示：和 Info 行为一致，单独留一个入口让调用点读起来更明白。</summary>
    public static void Warn(Window? owner, string title, string message)
        => Show(owner, title, message, null, "知道了");

    /// <summary>错误提示：关闭。</summary>
    public static void Error(Window? owner, string title, string message)
        => Show(owner, title, message, null, "关闭");

    /// <summary>确认框：主按钮是「继续」，只有点它才返回 true（Esc 一律当取消）。</summary>
    public static bool Confirm(Window? owner, string title, string message)
        => Show(owner, title, message, "取消", "继续");

    /// <summary>
    /// 按钮文案自己定的确认框。
    /// 风险说明这类"点错了后果很重"的地方，把主按钮写成一句完整的话
    /// （而不是笼统的「继续」），用户得真的读一遍才点得下去。
    /// </summary>
    public static bool Ask(Window? owner, string title, string message,
                           string secondaryText, string primaryText)
        => Show(owner, title, message, secondaryText, primaryText);

    /// <summary>是否框：主按钮是「是」。</summary>
    public static bool YesNo(Window? owner, string title, string message)
        => Show(owner, title, message, "否", "是");

    /// <summary>
    /// 显示一个**自定义内容**的对话框（下拉框、按钮、拖放区都能放），
    /// 复用本类的标题栏、1px 描边、圆角与底部按钮样式 —— 不要另写窗口。
    /// 主按钮返回 true。
    /// </summary>
    public static bool ShowCustom(Window? owner, string title, FrameworkElement content,
                                  string? primaryText = "关闭",
                                  string? secondaryText = null, Action? onSecondary = null)
    {
        var dialog = CreateCustom(title, content, primaryText, secondaryText, onSecondary);
        if (owner != null) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog._accepted;
    }

    /// <summary>
    /// 构造一个自定义内容对话框但**不以模态显示**。
    /// 供自检使用：ShowDialog 会阻塞调用线程，截图流程需要自己 Show() 再抓图。
    /// </summary>
    internal static DialogHost CreateCustom(string title, FrameworkElement content,
                                            string? primaryText = "关闭",
                                            string? secondaryText = null, Action? onSecondary = null)
        => new(title, string.Empty, secondaryText, primaryText,
               pages: null, customContent: content, onSecondary: onSecondary);

    /// <summary>
    /// 分页显示使用指南。页数与每页内容都是**内置**的（仓库 Assets\使用指南.txt，随程序集编译进来）。
    /// 单页过长时正文区会自动出现滚动条；左右方向键也能翻页。
    /// </summary>
    public static void ShowGuide(Window? owner, string title, IReadOnlyList<GuideCatalog.Page> pages)
    {
        try
        {
            if (pages.Count == 0)
            {
                Info(owner, title, "（使用指南暂时是空的：请检查程序内置的说明文件）");
                return;
            }

            ShowGuideCore(owner, title, pages);
        }
        catch (Exception ex)
        {
            // 与 Info/Warn 同样的理由：这个对话框可能在异常处理路径上被调用，
            // 自己再抛异常会把程序拖进"异常套异常"，最坏情况就是弹不出来。
            Core.Log.Error("使用指南显示失败", ex);
        }
    }

    private static void ShowGuideCore(Window? owner, string title, IReadOnlyList<GuideCatalog.Page> pages)
    {
        var dialog = new DialogHost(title, string.Empty, null, "关闭", pages);

        var target = ResolveOwner(owner, dialog);
        if (target != null)
        {
            try
            {
                dialog.Owner = target;
            }
            catch (InvalidOperationException)
            {
                target = null;
            }
        }

        if (target == null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
    }

    private static bool Show(Window? owner, string title, string message, string? secondaryText, string primaryText)
    {
        try
        {
            return ShowCore(owner, title, message, secondaryText, primaryText);
        }
        catch (Exception ex)
        {
            // 这个对话框会被"未处理的界面异常"处理器调用，它自己再抛异常会把程序拖进
            // 异常套异常的循环。最坏情况就是弹不出来，绝不能在这里炸。
            Core.Log.Error("自绘对话框显示失败", ex);
            return false;
        }
    }

    private static bool ShowCore(Window? owner, string title, string message, string? secondaryText, string primaryText)
    {
        var dialog = new DialogHost(title, message, secondaryText, primaryText);

        var target = ResolveOwner(owner, dialog);
        if (target != null)
        {
            try
            {
                dialog.Owner = target;
            }
            catch (InvalidOperationException)
            {
                // 属主刚好在关闭中时赋值会抛异常：退化成无属主显示，总比弹不出来好
                target = null;
            }
        }

        // 无属主时 CenterOwner 会退化到系统默认位置，所以显式改成屏幕居中
        if (target == null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();

        // 关掉模态对话框后，把属主窗口从"最小化"恢复过来并拿回焦点。
        // 现象：点对话框的确认按钮后整个程序像是被最小化了
        //（2026-10-08 用户报："在提示重启对话框点确认会最小化程序"）。
        // 成因在 WPF 模态窗口与属主窗口的状态联动上；与其去猜内部时序，
        // 不如关掉之后无条件纠正一次 —— 用户点的是"确认"，本意绝不是"最小化"。
        if (target is { WindowState: WindowState.Minimized })
            target.WindowState = WindowState.Normal;
        if (target is { IsVisible: true })
            target.Activate();

        return dialog._accepted;
    }

    /// <summary>挑一个真正可见、且不是对话框自己的属主窗口；都没有就返回 null，绝不抛异常。</summary>
    private static Window? ResolveOwner(Window? requested, Window dialog)
    {
        if (CanOwn(requested, dialog)) return requested;
        var main = Application.Current?.MainWindow;
        return CanOwn(main, dialog) ? main : null;
    }

    private static bool CanOwn(Window? candidate, Window dialog)
        => candidate is { IsVisible: true } && !ReferenceEquals(candidate, dialog);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc 永远是「取消 / 关闭」；Enter 直接打主按钮，键盘操作不必先去找焦点
        if (e.Key == Key.Escape)
        {
            _accepted = false;
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
        _primary?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        else if (_pages.Count > 0 && e.Key == Key.Left)
        {
            e.Handled = true;
            GoToPage(_pageIndex - 1);
        }
        else if (_pages.Count > 0 && e.Key == Key.Right)
        {
            e.Handled = true;
            GoToPage(_pageIndex + 1);
        }
    }

    private Border BuildTitleBar(string title)
    {
        var bar = new Border
        {
            Background = Brush(TitleColor),
            Height = 30,
            // 顶部两个圆角与外框的 6px 对齐（减去 1px 描边），否则标题栏的直角会盖掉圆角
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(TextColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            },
        };

        // 没有系统标题栏，整条自己拖；快速点击时按键可能已松开，DragMove 会抛异常，忽略即可
        bar.MouseLeftButtonDown += (_, _) =>
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 松开太快，不拖就行
            }
        };
        return bar;
    }

    private static Button MakeButton(string text, bool primary)
        => new()
        {
            Content = text,
            Background = Brush(primary ? AccentColor : ButtonFaceColor),
            BorderBrush = Brush(primary ? AccentColor : ButtonBorderColor),
            BorderThickness = new Thickness(1),
            Foreground = Brush(primary ? ButtonFaceColor : TextColor),
            Cursor = Cursors.Hand,
            FontSize = 12,
            MinWidth = 76,
            Margin = new Thickness(8, 0, 0, 0),
            // 内边距由模板里的 Border 提供，这里不再叠加 Button 自己的 Padding
            Padding = new Thickness(0),
            Template = CreateButtonTemplate(
                primary ? AccentHoverColor : ButtonHoverColor,
                primary ? AccentPressedColor : ButtonPressedColor),
        };

    /// <summary>扁平圆角按钮模板：底色/描边跟着按钮属性走，只有 hover / pressed 分主次两套。</summary>
    private static ControlTemplate CreateButtonTemplate(uint hoverColor, uint pressedColor)
    {
        var face = new FrameworkElementFactory(typeof(Border), "Bd");
        face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        face.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        face.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        face.SetValue(Border.PaddingProperty, new Thickness(14, 5, 14, 5));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = face };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush(hoverColor), "Bd"));
        template.Triggers.Add(hover);

        // 放在 hover 之后，按住时以 pressed 颜色为准
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush(pressedColor), "Bd"));
        template.Triggers.Add(pressed);

        return template;
    }

    /// <summary>0xAARRGGBB → 冻结画刷（可在模板触发器之间共享，不需要额外开销）。</summary>
    private static SolidColorBrush Brush(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 取当前主题的颜色（深浅两种模式一视同仁）。
    /// 资源缺失时回退到传入的兜底色值——回退逻辑本身在 <see cref="ThemeManager.Argb"/> 里，
    /// 这里保留一层同名包装只为让调用点读起来明确。
    /// </summary>
    private static uint Pick(string key, uint fallback) => ThemeManager.Argb(key, fallback);
}
