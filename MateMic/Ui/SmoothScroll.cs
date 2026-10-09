using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MateMic.Ui;

/// <summary>
/// 鼠标滚轮平滑滚动。
///
/// 为什么不用 DoubleAnimation：第一版是"每滚一格起一段 180 ms 动画"，观感不顺 ——
/// 每格都会从当前位置重新起一段动画，ease-out 在每个格子末尾都减速一次，于是变成
/// "一顿一顿地挪"。现在改成**逐帧指数平滑**（挂在 CompositionTarget.Rendering 上）：
/// 每帧把当前位置朝目标推进固定比例，滚轮只负责改目标值，因此连续滚动是一条连续的曲线。
///
/// 另外：拖动滚动条时会取消平滑（否则动画会把位置拉回旧目标，跟用户"抢滚动条"）。
/// </summary>
public static class SmoothScroll
{
    /// <summary>一格滚轮滚动的像素数。</summary>
    private const double StepPixels = 72.0;

    /// <summary>每帧朝目标逼近的比例：越大越"跟手"，越小越"绵"。0.28 约 4~5 帧走完大半。</summary>
    private const double FrameFactor = 0.28;

    /// <summary>小于这个距离就直接落定，避免无限逼近。</summary>
    private const double SettleThreshold = 0.4;

    private static readonly HashSet<ScrollViewer> Animating = new();

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    /// <summary>当前渲染位置（自己记账，不依赖 VerticalOffset 的更新时机）。</summary>
    private static readonly DependencyProperty CurrentProperty =
        DependencyProperty.RegisterAttached("Current", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0));

    /// <summary>目标位置（滚轮累加到它上面）。</summary>
    private static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached("Target", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;

        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        viewer.PreviewMouseDown -= OnPreviewMouseDown;
        if (e.NewValue is true)
        {
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            viewer.PreviewMouseDown += OnPreviewMouseDown;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.ScrollableHeight <= 0) return;

        e.Handled = true;   // 接管滚轮，避免默认的瞬间跳转

        if (!Animating.Contains(viewer))
        {
            // 新一轮滚动：以当前真实位置为起点
            viewer.SetValue(CurrentProperty, viewer.VerticalOffset);
            viewer.SetValue(TargetProperty, viewer.VerticalOffset);
        }

        var target = (double)viewer.GetValue(TargetProperty);
        target = Math.Clamp(target - Math.Sign(e.Delta) * StepPixels, 0, viewer.ScrollableHeight);
        viewer.SetValue(TargetProperty, target);
        Start(viewer);
    }

    /// <summary>按住滚动条（或它的上下翻页区）时取消平滑，交给系统原生行为。</summary>
    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (!IsInsideScrollBar(e.OriginalSource as DependencyObject)) return;

        Stop(viewer);
        viewer.SetValue(CurrentProperty, viewer.VerticalOffset);
        viewer.SetValue(TargetProperty, viewer.VerticalOffset);
    }

    private static bool IsInsideScrollBar(DependencyObject? node)
    {
        for (var i = 0; i < 8 && node != null; i++)
        {
            if (node is ScrollBar) return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private static void Start(ScrollViewer viewer)
    {
        if (!Animating.Add(viewer)) return;

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler == null) return;

            var current = (double)viewer.GetValue(CurrentProperty);
            var target = (double)viewer.GetValue(TargetProperty);
            var delta = target - current;

            if (Math.Abs(delta) < SettleThreshold)
            {
                viewer.ScrollToVerticalOffset(target);
                viewer.SetValue(CurrentProperty, target);
                CompositionTarget.Rendering -= handler;
                Animating.Remove(viewer);
                return;
            }

            var next = current + delta * FrameFactor;
            viewer.ScrollToVerticalOffset(next);
            viewer.SetValue(CurrentProperty, next);
        };

        CompositionTarget.Rendering += handler;
    }

    private static void Stop(ScrollViewer viewer)
    {
        // 逐帧回调自己会摘掉；这里只需要让它"立刻落定"：把目标对齐当前值即可
        viewer.SetValue(TargetProperty, (double)viewer.GetValue(CurrentProperty));
    }

    /// <summary>
    /// 立刻回到最顶端，并把内部记账一起归零（分页/换内容时用）。
    ///
    /// ⚠ 必须**同时**重置 Current 与 Target：只调 <c>ScrollToVerticalOffset(0)</c> 的话，
    /// 逐帧回调还记着旧目标，下一帧就把位置拉回去了。两个值都置 0 之后，
    /// 回调会判定"已落定"、顺手把自己摘掉。
    /// </summary>
    public static void ResetToTop(ScrollViewer? viewer)
    {
        if (viewer == null) return;
        viewer.SetValue(CurrentProperty, 0.0);
        viewer.SetValue(TargetProperty, 0.0);
        viewer.ScrollToVerticalOffset(0);
    }
}
