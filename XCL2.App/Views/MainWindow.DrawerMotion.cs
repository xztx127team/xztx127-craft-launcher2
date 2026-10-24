using System.Windows.Controls;
using System.Windows.Media.Animation;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private void ToggleSidebarWithMotion()
    {
        var from = SidebarColumn.ActualWidth;
        SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        ApplySidebarCollapsedState(!_sidebarCollapsed);
        if (_compactWindowLayout)
        {
            UiAnimationHelper.FadeIn(SidebarAreaBorder, kind: AnimationKind.Sidebar);
            return;
        }
        if (!AnimationSettingsService.IsEnabled(AnimationKind.Sidebar)) return;
        var to = SidebarColumn.Width.Value;
        SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, new GridLengthAnimation
        {
            From = from,
            To = to,
            Duration = AnimationSettingsService.Duration(190, AnimationKind.Sidebar),
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
        UiAnimationHelper.FadeIn(SidebarAreaBorder, kind: AnimationKind.Sidebar);
    }
}
