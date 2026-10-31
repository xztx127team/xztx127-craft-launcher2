using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XCL2.App.Services;

public static class ScrollWheelBehavior
{
    private static readonly List<WeakReference<ScrollViewer>> ActiveScrollers = new();

    static ScrollWheelBehavior()
    {
        AnimationSettingsService.Changed += () =>
        {
            if (AnimationSettingsService.IsEnabled(AnimationKind.SmoothScroll)) return;
            var active = ActiveScrollers.ToArray();
            ActiveScrollers.Clear();
            foreach (var reference in active)
            {
                if (!reference.TryGetTarget(out var viewer)) continue;
                if (viewer.Dispatcher.CheckAccess()) FinishScroll(viewer);
                else if (!viewer.Dispatcher.HasShutdownStarted)
                    viewer.Dispatcher.BeginInvoke(new Action(() => FinishScroll(viewer)));
            }
        };
    }
    /// <summary>100% 灵敏度时单次滚轮"咔哒"一格对应的基准滚动像素距离。
    /// 旧版本固定使用 180px；现在默认灵敏度为 90%，实际默认步长约 162px，
    /// 仍明显快于 WPF 原生滚动，但比旧版稍微收敛一点。</summary>
    public const double BaseWheelStepPixels = 180;

    public const int MinSensitivityPercent = 50;
    public const int MaxSensitivityPercent = 200;
    public const int DefaultSensitivityPercent = 90;

    private static int _sensitivityPercent = DefaultSensitivityPercent;

    /// <summary>当前全局滚轮灵敏度百分比。</summary>
    public static int SensitivityPercent => _sensitivityPercent;

    /// <summary>当前实际使用的单格像素步长。</summary>
    public static double WheelStepPixels => BaseWheelStepPixels * _sensitivityPercent / 100.0;

    public static int ClampSensitivityPercent(int percent) =>
        Math.Clamp(percent, MinSensitivityPercent, MaxSensitivityPercent);

    /// <summary>设置全局滚轮灵敏度。设置页拖动滑块时可直接调用做实时预览。</summary>
    public static void SetSensitivityPercent(int percent) =>
        _sensitivityPercent = ClampSensitivityPercent(percent);

    public static readonly DependencyProperty EnableFastWheelProperty =
        DependencyProperty.RegisterAttached(
            "EnableFastWheel",
            typeof(bool),
            typeof(ScrollWheelBehavior),
            new PropertyMetadata(false, OnEnableFastWheelChanged));

    public static void SetEnableFastWheel(DependencyObject element, bool value) =>
        element.SetValue(EnableFastWheelProperty, value);

    public static bool GetEnableFastWheel(DependencyObject element) =>
        (bool)element.GetValue(EnableFastWheelProperty);

    public static readonly DependencyProperty ContainWheelProperty =
        DependencyProperty.RegisterAttached(
            "ContainWheel",
            typeof(bool),
            typeof(ScrollWheelBehavior),
            new PropertyMetadata(false, OnContainWheelChanged));

    public static void SetContainWheel(DependencyObject element, bool value) =>
        element.SetValue(ContainWheelProperty, value);

    public static bool GetContainWheel(DependencyObject element) =>
        (bool)element.GetValue(ContainWheelProperty);

    /// <summary>
    /// 抽屉/展开式设置区域可以把 ContainWheel 设为 true：鼠标位于该区域时，滚轮事件只留在
    /// 该局部交互区，不再向外层长页面继续传递。这样用户还在抽屉里选颜色/调参数时，
    /// 不会因为滚轮把整个设置页一起带走。外层 PreviewMouseWheel 会先识别该边界并放行，
    /// 到冒泡阶段再由边界把事件截住，因此内部真正需要滚动的子控件仍有机会先处理事件。
    /// </summary>
    private static void OnContainWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue)
            element.MouseWheel += ContainedElement_MouseWheel;
        else
            element.MouseWheel -= ContainedElement_MouseWheel;
    }

    private static void ContainedElement_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
    }

    private static void OnEnableFastWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;

        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
            sv.Unloaded += ScrollViewer_Unloaded;
        }
        else
        {
            sv.PreviewMouseWheel -= ScrollViewer_PreviewMouseWheel;
            sv.Unloaded -= ScrollViewer_Unloaded;
            FinishScroll(sv, sv.VerticalOffset);
        }
    }

    private static void ScrollViewer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer) FinishScroll(viewer, viewer.VerticalOffset);
    }

    /// <summary>记录"这次连续滚动最终应该停在哪个像素偏移"，把同一串快速滚轮事件的多次
    /// Delta 累加到同一个目标值上，而不是每格都独立起播一个新动画、互相打断重来。</summary>
    private static readonly DependencyProperty TargetOffsetProperty =
        DependencyProperty.RegisterAttached("TargetOffset", typeof(double), typeof(ScrollWheelBehavior),
            new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty ScrollGenerationProperty =
        DependencyProperty.RegisterAttached("ScrollGeneration", typeof(int), typeof(ScrollWheelBehavior),
            new PropertyMetadata(0));

    private static void FinishScroll(ScrollViewer viewer, double? offset = null)
    {
        var target = offset ?? (double)viewer.GetValue(TargetOffsetProperty);
        if (double.IsNaN(target)) target = viewer.VerticalOffset;
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        viewer.SetValue(ScrollGenerationProperty, (int)viewer.GetValue(ScrollGenerationProperty) + 1);
        viewer.SetCurrentValue(ScrollViewerOffsetHelper.VerticalOffsetProperty, target);
        viewer.BeginAnimation(ScrollViewerOffsetHelper.VerticalOffsetProperty, null);
        viewer.ScrollToVerticalOffset(target);
        viewer.ClearValue(TargetOffsetProperty);
        ActiveScrollers.RemoveAll(reference => !reference.TryGetTarget(out var active) || ReferenceEquals(active, viewer));
    }

    private static void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.Delta == 0) return;

        if (e.OriginalSource is DependencyObject popupSource && IsInsideOpenPopup(popupSource, sv))
        {
            return;
        }

        // 抽屉/展开式局部区域声明了 ContainWheel 时，外层 ScrollViewer 不允许抢这次滚轮。
        // 这里只 return、不设 Handled，给区域内部控件先处理；若内部没有处理，冒泡到该区域本身
        // 时 ContainedElement_MouseWheel 会截断，确保事件不会继续滚动整个外层页面。
        if (e.OriginalSource is DependencyObject containedSource &&
            IsInsideContainWheelBoundary(containedSource, sv))
        {
            return;
        }

        // 修复"下拉框展开时滚动页面，弹出的选项列表跟丢在原地、跟真正的下拉框错位分开"：
        // 根因见下面 DoubleAnimation 那段——ComboBox 的下拉 Popup 是独立的顶层窗口，靠监听
        // PlacementTarget 的 LayoutUpdated 事件同步重新定位；同步的 ScrollToVerticalOffset
        // 每次都会立刻触发一次 LayoutUpdated，Popup 跟得上，但这里为了解决"滚轮滑不动"改成了
        // DoubleAnimation 动画过渡滚动，动画插值帧由渲染线程直接推动，不保证每帧都可靠触发
        // Popup 重新定位，于是出现"页面滚动画面已经在动了，下拉框的浮层却停在旧位置没跟上"
        // 这种视觉分离。原生控件的通行做法本来就是"一滚动就该收起下拉框"，所以这里在真正
        // 开始滚动之前，先把视觉树里任何还展开着的 ComboBox 下拉收起来，从根源上避免这种
        // 半途而废的错位状态出现，而不是事后去修正 Popup 的位置。
        CloseAnyOpenComboBoxDropdown(sv);

        if (e.OriginalSource is DependencyObject source && HasScrollableAncestorBefore(source, sv, e.Delta))
        {
            return;
        }

        // 连续滚动累加最终目标，但新动画始终从真实可见偏移开始。
        // ScrollToVerticalOffset 仍在 UI 线程触发布局；短时过渡和可关闭的平滑滚动
        // 减少密集设置页面的布局压力，不把它误当成渲染线程上的廉价位移动画。
        var current = double.IsNaN((double)sv.GetValue(TargetOffsetProperty))
            ? sv.VerticalOffset
            : (double)sv.GetValue(TargetOffsetProperty);

        var steps = e.Delta / 120.0;
        var target = current - steps * WheelStepPixels;

        // 提前 Clamp，避免动画播到一半才发现越界又反向纠正，出现"回弹"的观感。
        target = Math.Max(0, Math.Min(target, sv.ScrollableHeight));

        sv.SetValue(TargetOffsetProperty, target);

        var intensity = AnimationSettingsService.Intensity(AnimationKind.SmoothScroll);
        if (intensity <= 0 || Math.Abs(target - sv.VerticalOffset) < 0.5)
        {
            FinishScroll(sv, target);
            e.Handled = true;
            return;
        }

        var generation = (int)sv.GetValue(ScrollGenerationProperty) + 1;
        sv.SetValue(ScrollGenerationProperty, generation);
        ActiveScrollers.RemoveAll(reference => !reference.TryGetTarget(out var active) || ReferenceEquals(active, sv));
        ActiveScrollers.Add(new WeakReference<ScrollViewer>(sv));
        // 降低强度时减少需要插值的距离；滚动终点和时长仍独立于视觉强度。
        var from = sv.VerticalOffset + (target - sv.VerticalOffset) * (1 - intensity);
        sv.SetCurrentValue(ScrollViewerOffsetHelper.VerticalOffsetProperty, target);
        var animation = new DoubleAnimation
        {
            From = from,
            To = target,
            Duration = AnimationSettingsService.Duration(160, AnimationKind.SmoothScroll),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };
        animation.Completed += (_, _) =>
        {
            if ((int)sv.GetValue(ScrollGenerationProperty) == generation) FinishScroll(sv, target);
        };

        sv.BeginAnimation(ScrollViewerOffsetHelper.VerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        e.Handled = true;
    }

    /// <summary>递归遍历视觉树，找到当前展开着(IsDropDownOpen=true)的 ComboBox 就把它收起来。
    /// 设置页这类长列表页面上同一时间通常最多只有一个下拉框是展开状态，但这里不假设"最多一个"，
    /// 找到的每一个都会收起——万一有嵌套/自定义控件内部各自持有一个 ComboBox，也能一并处理，
    /// 不会因为遗漏第二个而留下同样的错位问题。</summary>
    private static void CloseAnyOpenComboBoxDropdown(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBox { IsDropDownOpen: true } combo)
            {
                combo.IsDropDownOpen = false;
            }
            CloseAnyOpenComboBoxDropdown(child);
        }
    }

    /// <summary>判断这次滚轮事件的原始来源(source)是不是身处某个当前展开着的 Popup（下拉列表、
    /// 展开式面板等）内部、且该 Popup 是挂在 outer 该外层 ScrollViewer 下面某个控件上的。
    /// 判断依据：Popup 内容单独渲染在自己的呈现源(PresentationSource)里，只是逻辑父级仍然
    /// 指回主窗口这一侧的 PlacementTarget——所以只要 source 的呈现源跟 outer 的呈现源不是
    /// 同一个，就说明 source 当前正处在某个展开的 Popup 内容里面，而不是主窗口可视树本身。
    /// 用 PresentationSource 判断而不是单纯沿视觉树 GetParent 往上爬，是因为视觉树在 Popup
    /// 边界处本来就是断开的（爬不回 outer），没法像 IsInsideContainWheelBoundary 那样直接
    /// 复用同一种"往上找"的写法。</summary>
    private static bool IsInsideOpenPopup(DependencyObject source, ScrollViewer outer)
    {
        if (source is not Visual sourceVisual) return false;

        var sourceRoot = PresentationSource.FromVisual(sourceVisual);
        var outerRoot = PresentationSource.FromVisual(outer);

        // 两者呈现源不同，说明 source 位于一个独立呈现的 Popup 内容树里（下拉列表、
        // 展开式浮层等），而不是主窗口本身这一支可视树上。
        return sourceRoot != null && !ReferenceEquals(sourceRoot, outerRoot);
    }

    private static bool IsInsideContainWheelBoundary(DependencyObject source, ScrollViewer outer)
    {
        var current = source;
        while (current != null && !ReferenceEquals(current, outer))
        {
            if (GetContainWheel(current))
                return true;

            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>从事件真正的来源(originalSource)往上找，直到碰到外层该 ScrollViewer(outer)为止——
    /// 途中如果先遇到一个自己内部还能再滚(没滚到顶/底)的可滚动控件，就返回 true，表示这次滚轮
    /// 应该交给那个内层控件自己处理，外层不要抢。ListBox/ListView/ComboBox 等控件的默认模板里
    /// 都内嵌了一个 ScrollViewer，所以只需要找"内层 ScrollViewer"即可，不需要特判具体控件类型。</summary>
    private static bool HasScrollableAncestorBefore(DependencyObject source, ScrollViewer outer, int wheelDelta)
    {
        var current = source;
        while (current != null && !ReferenceEquals(current, outer))
        {
            if (current is ScrollViewer inner && !ReferenceEquals(inner, outer))
            {
                // wheelDelta > 0 是向上滚：只要还没到顶（VerticalOffset > 0）就还有空间可滚。
                // wheelDelta < 0 是向下滚：只要还没到底（ScrollableHeight - VerticalOffset 还有余量）。
                var canScrollUp = inner.VerticalOffset > 0.0;
                var canScrollDown = inner.VerticalOffset < inner.ScrollableHeight;
                if ((wheelDelta > 0 && canScrollUp) || (wheelDelta < 0 && canScrollDown))
                {
                    return true;
                }
                // 内层已经滚到头，滚不动了——不 return，继续往上找（让事件最终交给外层处理，
                // 实现"内层滚到底后自动带动外层继续滚"的效果）。
            }

            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}

/// <summary>
/// ScrollViewer.VerticalOffset 本身是只读属性，没法直接作为动画目标（DoubleAnimation 需要
/// 一个可写的依赖属性）。这里用一个"影子"依赖属性，PropertyChanged 回调里转调用
/// ScrollToVerticalOffset，间接实现"能被动画驱动的滚动偏移"——这是 WPF 里给 ScrollViewer
/// 做平滑滚动动画的标准写法。
/// </summary>
internal static class ScrollViewerOffsetHelper
{
    public static readonly DependencyProperty VerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "VerticalOffset",
            typeof(double),
            typeof(ScrollViewerOffsetHelper),
            new PropertyMetadata(0.0, OnVerticalOffsetChanged));

    private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
            sv.ScrollToVerticalOffset((double)e.NewValue);
    }
}
