using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    private void RefreshTaskUi()
    {
        if (TaskSummaryText == null) return;
        if (_config.WorkMode == XCL2.App.Models.AiWorkMode.Chat)
        {
            TaskSelectionBar.Visibility = Visibility.Collapsed;
            RefreshPlanUi(null);
            TaskSummaryText.Text = "纯聊天 · 不创建任务或 Plan，不使用工作目录和命令权限。";
            TaskTrackingButton.Content = "纯聊天";
            TaskTrackingButton.IsEnabled = false;
            ResumeTaskButton.IsEnabled = false;
            ChangeHistoryButton.IsEnabled = !_sending;
            return;
        }
        RefreshTaskSelection();
        try
        {
            var task = _service?.GetTask(_session);
            RefreshPlanUi(task);
            TaskSummaryText.Text = task == null ? (_session.TaskTrackingEnabled
                ? "任务对话 · 发送后创建任务并保存步骤。可点击“设为非任务”改为普通聊天。"
                : "普通对话 · 不自动创建或更新任务。需要保存新的任务时，点击“设为任务”。") :
                $"{task.Title} · {TaskStatusLabel(task.Status)} · {task.Steps.Count(s => s.Status == "completed")}/{task.Steps.Count} 步完成 · {task.Mode}\n" +
                string.Join("\n", task.Steps.Select((s, i) => $"{i + 1}. [{s.Status}] {s.Title}")) +
                (string.IsNullOrWhiteSpace(task.NextAction) ? "" : "\n下一步：" + task.NextAction) +
                (task.Status == "completed" ? "\n任务已完成；继续聊天不会把它放回当前任务列表。" : "");
            TaskTrackingButton.Content = _session.TaskTrackingEnabled ? "设为非任务" : "设为任务";
            TaskTrackingButton.IsEnabled = !_sending && !_newSessionDecisionPending;
            ResumeTaskButton.IsEnabled = !_sending && !_newSessionDecisionPending && task?.Status is not ("completed" or "archived");
            ChangeHistoryButton.IsEnabled = !_sending;
        }
        catch (Exception ex)
        {
            RefreshPlanUi(null, "读取任务计划失败：" + ex.Message);
            TaskSummaryText.Text = "读取任务断点失败：" + ex.Message + "\n聊天仍会保留并按普通对话继续；点击“设为任务”可开始新任务。";
            TaskTrackingButton.Content = _session.TaskTrackingEnabled ? "设为非任务" : "设为任务";
            TaskTrackingButton.IsEnabled = !_sending && !_newSessionDecisionPending;
            ResumeTaskButton.IsEnabled = false;
            ChangeHistoryButton.IsEnabled = !_sending;
        }
    }
    private void ResumeTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _sending || _config.WorkMode == XCL2.App.Models.AiWorkMode.Chat) return;
        try
        {
            var task = _service.GetTask(_session);
            if (task?.Status is "completed" or "archived") task = null;
            task ??= _service.Tasks.List().FirstOrDefault(t => t.Status is not ("completed" or "archived"));
            if (task == null) { MessageBoxDialog.ShowInfo("暂无已保存任务。", "继续任务"); return; }
            SwitchToSession(FindRunningSession(task.Id) ?? _service.ResumeTask(task.Id));
            InputTextBox.Text = "从保存的断点继续任务，先核对当前项目文件和已完成步骤。";
            InputTextBox.Focus();
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "继续任务失败"); }
    }
    private void TaskTrackingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _sending || _newSessionDecisionPending
            || _config.WorkMode == XCL2.App.Models.AiWorkMode.Chat) return;
        try
        {
            _service.SetTaskTracking(_session, !_session.TaskTrackingEnabled);
            RefreshTaskUi();
            RefreshHistory();
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "更改任务状态失败"); }
    }
    private void CompletedTasksButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _newSessionDecisionPending) return;
        try
        {
            var id = AiCompletedTasksDialog.ShowTasks(_service, taskId =>
            {
                // Keep idle in-memory conversations consistent with the cleaned disk snapshots.
                foreach (var session in _unsavedSessions.Values.Append(_session).Distinct())
                    if (session.TaskId == taskId)
                    {
                        session.TaskId = null;
                        session.TaskTrackingEnabled = false;
                    }
                RefreshTaskUi();
                RefreshHistory();
            });
            if (id == null) return;
            var session = _service.ListSessions().FirstOrDefault(s => s.TaskId == id);
            if (session == null)
            {
                MessageBoxDialog.ShowInfo("此任务没有保留的对话，任务步骤仍可在已完成任务窗口查看。", "查看已完成任务");
                return;
            }
            SwitchToSession(_service.LoadSession(session.Id) ?? session);
            RefreshTaskUi();
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "已完成任务"); }
    }
    private void ChangeHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _sending) return;
        try
        {
            var ids = _service.GetTask(_session)?.ChangeIds ?? new List<string>();
            // Also recover durable commits if the process stopped before a chat checkpoint.
            ids = ids.Concat(_session.Messages.SelectMany(m => m.Activities).Where(a => a.ChangeId != null).Select(a => a.ChangeId!)).Distinct().ToList();
            ids = ids.Concat(AiFileAccessService.Changes.List().Where(c => c.State is "Applied" or "Applying" or "RollingBack").Select(c => c.Id)).Distinct().ToList();
            AiWorkbenchDialogs.History(Window.GetWindow(this), ids);
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "变更历史"); }
    }
    private bool _suppressTaskSelection;
    private sealed record TaskSelectionItem(string Id, string Label);

    private void RefreshTaskSelection()
    {
        if (TaskSelectionBar == null) return;
        if (_config.WorkMode == XCL2.App.Models.AiWorkMode.Chat)
        { TaskSelectionBar.Visibility = Visibility.Collapsed; return; }
        TaskSelectionBar.Visibility = _config.ShowTaskSelectionBar ? Visibility.Visible : Visibility.Collapsed;
        if (!_config.ShowTaskSelectionBar) return;
        _suppressTaskSelection = true;
        try
        {
            var items = new List<TaskSelectionItem>();
            var currentTask = _service?.GetTask(_session);
            if (currentTask == null || currentTask.Status is "completed" or "archived")
                items.Add(new("", currentTask?.Status == "completed" ? "当前会话 · 已完成任务（历史）"
                    : !_session.TaskTrackingEnabled ? "当前会话 · 普通对话（非任务）" : "当前会话 · 发送后创建任务"));
            if (_service != null)
                items.AddRange(_service.Tasks.List()
                    .Where(t => t.Status is not ("completed" or "archived"))
                    .Select(t => new TaskSelectionItem(t.Id, $"{t.Title} · {TaskStatusLabel(t.Status)} · {TaskModeLabel(t.Mode)}")));
            if (items.Count == 0) items.Add(new("", "暂无已保存任务"));
            TaskSelectionCombo.ItemsSource = items;
            TaskSelectionCombo.SelectedItem = items.FirstOrDefault(t => t.Id == (_session.TaskId ?? "")) ?? items[0];
        }
        catch (Exception ex)
        {
            TaskSelectionCombo.ItemsSource = new[] { new TaskSelectionItem("", "读取任务失败：" + ex.Message) };
            TaskSelectionCombo.SelectedIndex = 0;
        }
        finally { _suppressTaskSelection = false; }
    }

    private static string TaskStatusLabel(string status) => status switch
    {
        "running" => "运行中", "completed" => "已完成", "interrupted" => "已中断",
        "archived" => "已归档", _ => "已暂停"
    };
    private static string TaskModeLabel(string mode) => mode switch
    {
        "Plan" => "规划", "Agent" => "智能执行", _ => "执行"
    };

    private void TaskSelectionCombo_DropDownOpened(object sender, EventArgs e) => RefreshTaskSelection();
    private void RefreshTasksButton_Click(object sender, RoutedEventArgs e) => RefreshTaskUi();
    private void TaskSelectionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressTaskSelection || _newSessionDecisionPending || _service == null || TaskSelectionCombo.SelectedItem is not TaskSelectionItem item
            || string.IsNullOrWhiteSpace(item.Id) || item.Id == _session.TaskId) return;
        try
        {
            var session = FindRunningSession(item.Id)
                ?? _service.ListSessions().FirstOrDefault(s => s.TaskId == item.Id);
            if (session != null)
                SwitchToSession(_service.LoadSession(session.Id) ?? session);
            else
                SwitchToSession(_service.ResumeTask(item.Id));
            InputTextBox.Focus();
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowWarning(ex.Message, "选择任务失败");
            RefreshTaskSelection();
        }
    }
}
