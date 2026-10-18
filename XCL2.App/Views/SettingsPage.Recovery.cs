using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private void InitializeRecoverySettings(AppConfig cfg)
    {
        ReminderBadgeCheck.IsChecked = cfg.ShowReminderBadge;
        LaunchHealthCheck.IsChecked = cfg.EnableLaunchHealthCheck;
        SelectComboByTag(LaunchHealthModeCombo, cfg.IntegrityCheckMode == IntegrityCheckMode.Strict ? "Strict" : "Simple");
        RememberDownloadsCheck.IsChecked = cfg.RememberInterruptedDownloads;
        ModSnapshotCheck.IsChecked = cfg.SnapshotBeforeModUpdates;
        ModSnapshotCountSlider.Value = Math.Clamp(cfg.ModUpdateSnapshotRetentionCount, 1, 100);
        ModSnapshotSpaceSlider.Value = Math.Clamp(cfg.ModUpdateSnapshotMaxMb, 64, 16384);
    }
    private void SaveRecoverySettings(AppConfig cfg)
    {
        cfg.ShowReminderBadge = ReminderBadgeCheck.IsChecked == true;
        cfg.EnableLaunchHealthCheck = LaunchHealthCheck.IsChecked == true;
        cfg.IntegrityCheckMode = TagOf(LaunchHealthModeCombo) == "Strict" ? IntegrityCheckMode.Strict : IntegrityCheckMode.Simple;
        cfg.RememberInterruptedDownloads = RememberDownloadsCheck.IsChecked == true;
        cfg.SnapshotBeforeModUpdates = ModSnapshotCheck.IsChecked == true;
        cfg.ModUpdateSnapshotRetentionCount = (int)Math.Clamp(ModSnapshotCountSlider.Value, 1, 100);
        cfg.ModUpdateSnapshotMaxMb = (int)Math.Clamp(ModSnapshotSpaceSlider.Value, 64, 16384);
    }
    private void OpenReminders_Click(object sender, RoutedEventArgs e) => _owner.OpenReminderCenter();
    private void OpenModSnapshots_Click(object sender, RoutedEventArgs e) => _owner.OpenModUpdateSnapshots();
}
