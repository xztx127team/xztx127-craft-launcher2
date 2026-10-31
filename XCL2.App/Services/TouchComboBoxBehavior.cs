using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace XCL2.App.Services;

/// <summary>编辑表格底部的下拉框按所在显示器可用空间展开，浮层自己负责触摸滚动。</summary>
public static class TouchComboBoxBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(TouchComboBoxBehavior), new PropertyMetadata(false, OnChanged));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ComboBox combo) return;
        combo.DropDownOpened -= OnOpened;
        if (args.NewValue is true) combo.DropDownOpened += OnOpened;
    }

    private static void OnOpened(object? sender, EventArgs args)
    {
        if (sender is not ComboBox combo || !combo.IsVisible
            || combo.Template.FindName("PART_Popup", combo) is not Popup popup) return;
        var top = combo.PointToScreen(new Point(0, 0));
        var bottom = combo.PointToScreen(new Point(0, combo.ActualHeight));
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)top.X, (int)top.Y));
        var dpi = VisualTreeHelper.GetDpi(combo).DpiScaleY;
        var above = Math.Max(0, (top.Y - screen.WorkingArea.Top) / dpi - 12);
        var below = Math.Max(0, (screen.WorkingArea.Bottom - bottom.Y) / dpi - 12);
        var desired = Math.Min(280, combo.Items.Count * 42 + 8);
        var upwards = below < desired && above > below;
        combo.MaxDropDownHeight = Math.Max(24, Math.Min(280, upwards ? above : below));
        popup.PlacementTarget = combo;
        popup.Placement = upwards ? PlacementMode.Top : PlacementMode.Bottom;
        if (popup.Child is UIElement content) TransparencyAnimationService.Play(content);
    }
}
