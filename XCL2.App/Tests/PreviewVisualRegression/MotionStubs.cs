using System.Windows;
namespace XCL2.App.Services;
// Animation timing is covered by PopupAnimationRegression. These are only the
// attached-property contracts needed to load the real visual resource dictionary.
public static class DrawerMotionBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(DrawerMotionBehavior), new PropertyMetadata(false));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
}
public static class PageTransitionBehavior
{
    public static readonly DependencyProperty PopupFadeOnVisibleProperty = DependencyProperty.RegisterAttached("PopupFadeOnVisible", typeof(bool), typeof(PageTransitionBehavior), new PropertyMetadata(false));
    public static bool GetPopupFadeOnVisible(DependencyObject value) => (bool)value.GetValue(PopupFadeOnVisibleProperty);
    public static void SetPopupFadeOnVisible(DependencyObject value, bool enabled) => value.SetValue(PopupFadeOnVisibleProperty, enabled);
}
