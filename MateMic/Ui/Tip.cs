using System.Windows;
using System.Windows.Controls;
using MateMic.Core;

namespace MateMic.Ui;

/// <summary>
/// 把控件的悬浮说明接到文案表上，XAML 里这样写：
///
/// <code>&lt;Button ui:Tip.Key="Track.Remove" /&gt;</code>
///
/// 为什么用附加属性而不是 <c>ToolTip="{DynamicResource 文案.xxx}"</c>：
/// 后者只能"把文字塞进 ToolTip"，**没法表达"这条说明不存在"**——
/// 文案留空时会显示出一个空的提示框。而作者明确要求
/// 「文案里空着的说明就不显示」，所以需要一段代码来判断"空就不设 ToolTip"。
///
/// 语义（与 <see cref="TextCatalog"/> 一致）：
///   · 文案有内容 ⇒ 设为该控件的 ToolTip；
///   · 文案留空   ⇒ <c>ClearValue</c>，控件没有 ToolTip，鼠标悬停什么都不弹；
///   · 标识写错   ⇒ 文案表返回【缺少文案：xxx】，会照常显示出来，便于一眼发现手误。
/// </summary>
public static class Tip
{
    /// <summary>文案标识（对应「文案.txt」里的 <c>[标识]</c>）。</summary>
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(Tip),
            new PropertyMetadata(null, OnKeyChanged));

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);

    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var text = e.NewValue is string key && !string.IsNullOrWhiteSpace(key)
            ? TextCatalog.Get(key)
            : null;

        if (string.IsNullOrWhiteSpace(text))
        {
            // 空文案 = 作者关掉了这条说明；清掉 ToolTip，悬停不弹任何东西
            element.ClearValue(ToolTipService.ToolTipProperty);
            return;
        }

        element.SetValue(ToolTipService.ToolTipProperty, text);
    }
}
