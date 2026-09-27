using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MateMic.Ui;

/// <summary>
/// 应用风格的自绘对话框，用来替代系统 MessageBox（蓝色图标、左右留白不均、旧式按钮）。
/// 视觉树全部在构造函数里用代码搭好：启动早期还没有主窗口、App.xaml 资源也可能没加载，
/// 这里刻意不引用任何 App 资源，保证任何时刻都能弹出来。
/// </summary>
public sealed class DialogHost : Window
{
    // 取自 Ui/Theme.xaml 的调色板，硬编码是为了不依赖尚未加载的应用资源
    private const uint WindowColor = 0xF3F3F3;  // WindowBrush
    private const uint LineColor = 0xC9C9C9;    // 卡片描边
    private const uint TitleColor = 0xE9E9E9;   // 分组容器底色
    private const uint TextColor = 0x1F1F1F;    // TextBrush
    private const uint AccentColor = 0x2F80ED;  // AccentBrush
    private const uint AccentHoverColor = 0x1E6FD9;
    private const uint AccentPressedColor = 0x1A62C2;
    private const uint ButtonFaceColor = 0xFFFFFF;
    private const uint ButtonBorderColor = 0xCCCCCC;
    private const uint ButtonHoverColor = 0xF2F2F2;
    private const uint ButtonPressedColor = 0xE8E8E8;

    private readonly Button _primary;
    private bool _accepted;

    private DialogHost(string title, string message, string? secondaryText, string primaryText)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 12;

        // 无边框窗口本身是矩形的：把窗口背景做成透明，圆角才真的圆
        // （否则圆角外的四个小角会露出窗口底色，看起来像"圆角没生效"）。
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        var titleBar = BuildTitleBar(title);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 16, 16, 14),
        };

        if (secondaryText != null)
        {
            var secondary = MakeButton(secondaryText, primary: false);
            secondary.Click += (_, _) => { _accepted = false; Close(); };
            buttons.Children.Add(secondary);
        }

        _primary = MakeButton(primaryText, primary: true);
        _primary.Click += (_, _) => { _accepted = true; Close(); };
        buttons.Children.Add(_primary);

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBar, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(titleBar);
        layout.Children.Add(buttons);
        layout.Children.Add(BuildMessage(message));

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

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>单按钮提示：确定。</summary>
    public static void Info(Window? owner, string title, string message)
        => Show(owner, title, message, null, "确定");

    /// <summary>风险提示：和 Info 行为一致，单独留一个入口让调用点读起来更明白。</summary>
    public static void Warn(Window? owner, string title, string message)
        => Show(owner, title, message, null, "知道了");

    /// <summary>错误提示：关闭。</summary>
    public static void Error(Window? owner, string title, string message)
        => Show(owner, title, message, null, "关闭");

    /// <summary>确认框：主按钮是「继续」，只有点它才返回 true（Esc 一律当取消）。</summary>
    public static bool Confirm(Window? owner, string title, string message)
        => Show(owner, title, message, "取消", "继续");

    /// <summary>是否框：主按钮是「是」。</summary>
    public static bool YesNo(Window? owner, string title, string message)
        => Show(owner, title, message, "否", "是");

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
            _primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

    /// <summary>正文左右留白必须对称（16,14,16,0），这是用户明确反馈的问题点。</summary>
    private static StackPanel BuildMessage(string message)
        => new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(16, 14, 16, 0),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush(TextColor),
                    FontSize = 12,
                    LineHeight = 19,
                    // 风险说明这类长文案限制最大宽度，避免窗口拉成一整条
                    MaxWidth = 520,
                },
            },
        };

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

    /// <summary>0xRRGGBB → 冻结画刷（可在模板触发器之间共享，不需要额外开销）。</summary>
    private static SolidColorBrush Brush(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
