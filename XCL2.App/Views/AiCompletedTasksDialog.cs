using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Workbench;

namespace XCL2.App.Views;

/// <summary>Completed records have their own view and never return to the current task selector.</summary>
internal sealed class AiCompletedTasksDialog : OverlayDialogControl
{
    private sealed record TaskRow(WorkTask Task, string Label);
    private readonly AiAssistantService _service;
    private readonly Action<string> _taskDeleted;
    private readonly ListBox _list = new() { DisplayMemberPath = "Label" };
    private readonly TextBox _details = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(12)
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _open = new() { Content = "查看对话", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _delete = new() { Content = "删除选中任务", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
    private string? _selectedTaskId;

    private AiCompletedTasksDialog(AiAssistantService service, Action<string> taskDeleted)
    {
        _service = service; _taskDeleted = taskDeleted;
        MinWidth = 560; MaxWidth = 960; Height = 530; MaxHeight = 650;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "已完成任务", FontSize = 22, FontWeight = FontWeights.Bold });
        var note = new TextBlock { Text = "打开任务仅查看记录与对话。删除只移除任务记录，聊天、token 用量及变更历史仍会保留。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        heading.Children.Add(note); layout.Children.Add(heading);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        _list.Margin = new Thickness(0, 0, 12, 0);
        _list.SelectionChanged += (_, _) => ShowSelection();
        body.Children.Add(_list); Grid.SetColumn(_details, 1); body.Children.Add(_details);
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(_status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        _open.Click += (_, _) =>
        {
            if (_list.SelectedItem is not TaskRow row) return;
            _selectedTaskId = row.Task.Id; CloseWith(true);
        };
        _delete.Click += (_, _) => DeleteSelection();
        var close = new Button { Content = "关闭", Padding = new Thickness(12, 7, 12, 7), IsCancel = true };
        close.Click += (_, _) => CloseWith(false);
        buttons.Children.Add(_open); buttons.Children.Add(_delete); buttons.Children.Add(close);
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout; RefreshRows();
    }

    private void RefreshRows()
    {
        var selected = (_list.SelectedItem as TaskRow)?.Task.Id;
        var rows = _service.Tasks.List().Where(t => t.Status == "completed")
            .Select(t => new TaskRow(t, $"{t.Title}\n{t.UpdatedUtc.ToLocalTime():g} · {t.Steps.Count(s => s.Status == "completed")}/{t.Steps.Count} 步完成")).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Task.Id == selected) ?? rows.FirstOrDefault();
        if (rows.Count == 0) _status.Text = "暂无已完成任务。";
        ShowSelection();
    }

    private void ShowSelection()
    {
        var task = (_list.SelectedItem as TaskRow)?.Task;
        _delete.IsEnabled = task != null;
        _open.IsEnabled = task != null && _service.ListSessions().Any(s => s.TaskId == task.Id);
        _details.Text = task == null ? "选择左侧任务查看已完成的步骤、证据和备注。" :
            $"{task.Title}\n目标：{task.Goal}\n模式：{task.Mode} · 已完成\n项目：{task.ProjectRoot}\n\n" +
            string.Join("\n\n", task.Steps.Select((s, i) => $"{i + 1}. {s.Title}\n状态：{s.Status}\n证据：{s.Evidence}")) +
            (string.IsNullOrWhiteSpace(task.Notes) ? "" : "\n\n备注：" + task.Notes) +
            (task.Checkpoints.Count == 0 ? "" : "\n\n最近断点：\n" + string.Join("\n", task.Checkpoints.TakeLast(5))) +
            "\n\n继续聊天会保持为普通对话。需要开始新任务时，请在对话里点击“设为任务”。";
    }

    private void DeleteSelection()
    {
        if (_list.SelectedItem is not TaskRow row) return;
        if (!MessageBoxDialog.ShowConfirm($"删除已完成任务“{row.Task.Title}”？\n\n任务步骤、断点和任务关联会被移除。已有聊天、API 用量和文件变更历史会保留。", "删除已完成任务")) return;
        try
        {
            _service.DeleteCompletedTask(row.Task.Id);
            _taskDeleted(row.Task.Id); RefreshRows();
            _status.Text = "任务已删除；已有对话与变更历史已保留。";
        }
        catch (Exception ex)
        {
            try
            {
                if (_service.Tasks.TryGet(row.Task.Id) == null) { _taskDeleted(row.Task.Id); RefreshRows(); }
            }
            catch { /* Keep the original error visible even if a damaged task cannot be reread. */ }
            _status.Text = "删除任务：" + ex.Message;
        }
    }

    internal static string? ShowTasks(AiAssistantService service, Action<string> taskDeleted)
    {
        var dialog = new AiCompletedTasksDialog(service, taskDeleted);
        return dialog.ShowDialog() == true ? dialog._selectedTaskId : null;
    }
}
