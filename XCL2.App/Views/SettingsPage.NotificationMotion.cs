using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private readonly Dictionary<AnimationKind, Slider> _notificationDurationControls = new();

    private void ClearNotificationDurationControls()
    {
        foreach (var slider in _notificationDurationControls.Values) UnregisterName(slider.Name);
        _notificationDurationControls.Clear();
    }

    private void AddNotificationDurationControls(Grid row, AnimationKind kind, double durationSeconds)
    {
        if (kind is not (AnimationKind.Popup or AnimationKind.Toast)) return;
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            Text = "动画时长（秒）", FontSize = 12, Margin = new Thickness(20, 8, 0, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var duration = new Slider
        {
            Name = $"Animation_{kind}_Duration", Width = 220, Minimum = 0.05, Maximum = 2,
            TickFrequency = 0.05, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
            Value = durationSeconds, Margin = new Thickness(0, 8, 0, 6),
            ToolTip = "单次入场或退场的时长；默认 0.2 秒，独立于全局动画速度。"
        };
        AutomationProperties.SetName(duration, kind == AnimationKind.Toast ? "消息通知动画时长" : "弹窗动画时长");
        var value = new TextBlock
        {
            Text = $"{duration.Value:0.00} s", Margin = new Thickness(8, 8, 0, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        duration.ValueChanged += (_, args) =>
        {
            value.Text = $"{args.NewValue:0.00} s";
            if (_suppressDirtyTracking) return;
            PreviewAnimationSettings();
            OnSettingsEdited();
            QueueAnimationPreview(kind);
        };
        RegisterName(duration.Name, duration);
        _notificationDurationControls[kind] = duration;
        Grid.SetRow(label, 1);
        Grid.SetRow(duration, 1);
        Grid.SetColumn(duration, 1);
        Grid.SetRow(value, 1);
        Grid.SetColumn(value, 2);
        Grid.SetColumnSpan(value, 2);
        row.Children.Add(label);
        row.Children.Add(duration);
        row.Children.Add(value);
    }
}
