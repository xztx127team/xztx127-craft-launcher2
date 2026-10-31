using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Runtime.CompilerServices;

namespace XCL2.App.Services;

/// <summary>抽屉首帧布局后淡入；关闭时清理动画，快速反复开关不会留下透明内容。</summary>
public static class DrawerMotionBehavior
{
    private sealed class PopupCycle { public FrameworkElement? Content; }
    private static readonly ConditionalWeakTable<Popup, PopupCycle> PopupCycles = new();
    private static bool _registered;

    /// <summary>为程序创建和页面专用的 Popup 补齐动画；显式模板动画仍优先，避免叠加。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                // Popup 本身未必在主窗口的可视树中；其 Child 的 Loaded 每次挂到 HWND 都可靠触发。
                if (sender is not FrameworkElement child || LogicalTreeHelper.GetParent(child) is not Popup popup) return;
                if (popup.ReadLocalValue(EnabledProperty) is false) return;
                if (!GetEnabled(popup)) SetEnabled(popup, true);
                if (popup.IsOpen) ShowPopupOnce(popup);
            }));
    }
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(DrawerMotionBehavior), new PropertyMetadata(false, OnEnabledChanged));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is Expander expander)
        {
            expander.Expanded -= OnExpanded;
            expander.Collapsed -= OnCollapsed;
            if ((bool)args.NewValue)
            {
                expander.Expanded += OnExpanded;
                expander.Collapsed += OnCollapsed;
            }
        }
        else if (target is Popup popup)
        {
            popup.Opened -= OnPopupOpened;
            popup.Closed -= OnPopupClosed;
            if ((bool)args.NewValue)
            {
                popup.Opened += OnPopupOpened;
                popup.Closed += OnPopupClosed;
                if (popup.IsOpen) ShowPopupOnce(popup);
            }
        }
    }

    private static void OnExpanded(object sender, RoutedEventArgs args)
    {
        // Expanded 是路由事件，内层抽屉不能让外层整块重新淡入。
        if (sender is not Expander expander || !ReferenceEquals(args.OriginalSource, expander)) return;
        expander.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!expander.IsExpanded || !expander.IsLoaded || !GetEnabled(expander)) return;
            if (expander.Template?.FindName("ExpandSite", expander) is FrameworkElement content
                && !UiAnimationHelper.HasActivePageAncestor(content))
                UiAnimationHelper.PopupFadeIn(content);
        }));
    }

    private static void OnCollapsed(object sender, RoutedEventArgs args)
    {
        if (sender is Expander expander && ReferenceEquals(args.OriginalSource, expander)
            && expander.Template?.FindName("ExpandSite", expander) is UIElement content)
            UiAnimationHelper.Reset(content);
    }

    private static void OnPopupOpened(object? sender, EventArgs args)
    {
        if (sender is Popup popup) ShowPopupOnce(popup);
    }

    private static void OnPopupClosed(object? sender, EventArgs args)
    {
        if (sender is not Popup popup) return;
        PopupCycles.GetValue(popup, _ => new PopupCycle()).Content = null;
        if (popup.Child is UIElement content) UiAnimationHelper.Reset(content);
    }

    private static void ShowPopupOnce(Popup popup)
    {
        if (popup.Child is not FrameworkElement content) return;
        var cycle = PopupCycles.GetValue(popup, _ => new PopupCycle());
        if (ReferenceEquals(cycle.Content, content)) return;
        cycle.Content = content;
        // 非透明 HWND 淡化内容会露出黑底；例如密码临时查看框，保留其原生呈现。
        if (!popup.AllowsTransparency) return;
        // ToolTip / ContextMenu 的隐式模板和 ComboBox 下拉模板已经逐次播放动画。
        if (content is ToolTip or ContextMenu || HasExplicitPopupMotion(content)) return;
        UiAnimationHelper.PopupFadeIn(content);
    }

    private static bool HasExplicitPopupMotion(DependencyObject content)
    {
        if (PageTransitionBehavior.GetPopupFadeOnVisible(content)) return true;
        if (content is not Visual) return false;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(content); i++)
            if (HasExplicitPopupMotion(VisualTreeHelper.GetChild(content, i))) return true;
        return false;
    }
}
