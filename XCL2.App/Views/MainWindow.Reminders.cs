using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private void PublishGameDiagnostic(XCL2.App.Models.GameProcessInfo process, string title, string description, Action show)
        => ReminderCenterService.Publish($"game-exit-{process.Pid}-{process.StartedAt.Ticks}", title,
            description, "查看诊断", () => { show(); return Task.CompletedTask; });

    private void InitializeReminderCenter()
    {
        ReminderCenterService.Changed += RefreshReminderBadge;
        ConfigService.Saved += RefreshReminderBadge;
        Closed += (_, _) =>
        {
            ReminderCenterService.Changed -= RefreshReminderBadge;
            ConfigService.Saved -= RefreshReminderBadge;
        };
        RefreshReminderBadge();
    }

    private void RefreshReminderBadge()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshReminderBadge)); return; }
        var count = ReminderCenterService.Items.Count;
        var unread = ReminderCenterService.UnreadCount;
        var visible = ConfigService.Config.ShowReminderBadge && count > 0;
        ReminderBadgeButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ReminderBadgeButton.Content = unread > 0 ? $"提醒 · {unread}" : "提醒";
        ReminderBadgeButton.ToolTip = $"{unread} 条未读提醒，点击查看；不会自动打开或执行操作";
        ToastHost.Margin = new Thickness(0, 0, 20, visible ? 70 : 20);
        if (ReminderPanel.Visibility == Visibility.Visible) RenderReminderCards();
    }

    public void OpenReminderCenter()
    {
        ReminderPanel.Visibility = Visibility.Visible;
        ReminderCenterService.MarkRead();
        RenderReminderCards();
    }

    private void ReminderBadge_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderPanel.Visibility == Visibility.Visible) ReminderPanel.Visibility = Visibility.Collapsed;
        else OpenReminderCenter();
    }
    private void CloseReminderPanel_Click(object sender, RoutedEventArgs e) => ReminderPanel.Visibility = Visibility.Collapsed;
    private void ClearReadReminders_Click(object sender, RoutedEventArgs e) => ReminderCenterService.ClearRead();

    private void RenderReminderCards()
    {
        ReminderCards.Children.Clear();
        foreach (var reminder in ReminderCenterService.Items)
        {
            var body = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var title = new TextBlock { Text = reminder.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var text = new TextBlock { Text = reminder.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 6) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            body.Children.Add(title); body.Children.Add(text);
            var buttons = new WrapPanel();
            if (reminder.Action != null)
            {
                var open = new Button { Content = reminder.ActionLabel ?? "查看", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(9, 4, 9, 4) };
                open.SetResourceReference(StyleProperty, "SecondaryButton");
                open.Click += async (_, _) =>
                {
                    open.IsEnabled = false;
                    ReminderPanel.Visibility = Visibility.Collapsed;
                    try { await reminder.Action(); }
                    catch (Exception ex) { ToastService.ShowWarning("操作未完成：" + ex.Message); }
                    finally { open.IsEnabled = true; }
                };
                buttons.Children.Add(open);
            }
            if (reminder.SecondaryAction != null)
            {
                var secondary = new Button { Content = reminder.SecondaryLabel ?? "更多", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(9, 4, 9, 4) };
                secondary.SetResourceReference(StyleProperty, "SecondaryButton");
                secondary.Click += async (_, _) =>
                {
                    secondary.IsEnabled = false;
                    ReminderPanel.Visibility = Visibility.Collapsed;
                    try { await reminder.SecondaryAction(); }
                    catch (Exception ex) { ToastService.ShowWarning("操作未完成：" + ex.Message); }
                    finally { secondary.IsEnabled = true; }
                };
                buttons.Children.Add(secondary);
            }
            var dismiss = new Button { Content = "忽略", Padding = new Thickness(9, 4, 9, 4) };
            dismiss.SetResourceReference(StyleProperty, "SecondaryButton");
            dismiss.Click += (_, _) => ReminderCenterService.Dismiss(reminder.Key);
            buttons.Children.Add(dismiss); body.Children.Add(buttons); ReminderCards.Children.Add(body);
        }
        if (ReminderCards.Children.Count == 0) ReminderCards.Children.Add(new TextBlock { Text = "没有待查看的提醒。" });
    }
}
