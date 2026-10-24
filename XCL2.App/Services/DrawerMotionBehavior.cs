using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>抽屉首帧布局后淡入；关闭时清理动画，快速反复开关不会留下透明内容。</summary>
public static class DrawerMotionBehavior
{
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
        if (sender is Popup { Child: UIElement content }) UiAnimationHelper.PopupFadeIn(content);
    }

    private static void OnPopupClosed(object? sender, EventArgs args)
    {
        if (sender is Popup { Child: UIElement content }) UiAnimationHelper.Reset(content);
    }
}
