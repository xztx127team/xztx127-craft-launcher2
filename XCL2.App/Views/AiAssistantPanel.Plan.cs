using System.Windows;
using XCL2.App.Services.Workbench;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    // The sidebar is a snapshot of the same persistent task used by update_task and resume.
    // It never guesses completion from streamed prose or a generic tool completion event.
    private void RefreshPlanUi(WorkTask? task, string? error = null)
    {
        if (PlanStepsItemsControl == null) return;
        if (_config.WorkMode == XCL2.App.Models.AiWorkMode.Chat)
        {
            PlanStepsItemsControl.ItemsSource = null;
            PlanSummaryText.Text = "纯聊天模式";
            PlanEmptyText.Visibility = Visibility.Visible;
            PlanEmptyText.Text = "用于聊天和交流，不创建执行计划。";
            PlanNextActionText.Text = "";
            PlanNextActionBorder.Visibility = Visibility.Collapsed;
            return;
        }
        PlanStepsItemsControl.ItemsSource = task?.Steps.Select(step => new PlanStepVm(step, task.Status)).ToList();
        PlanSummaryText.Text = task == null ? "任务计划" :
            $"{task.Title}\n{TaskStatusLabel(task.Status)} · {task.Steps.Count(s => s.Status == "completed")}/{task.Steps.Count} 步完成";
        PlanEmptyText.Visibility = task?.Steps.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        PlanEmptyText.Text = error ?? (task != null
            ? "尚未保存步骤。复杂任务建议记录计划；简单需求可直接处理。"
            : !_session.TaskTrackingEnabled
                ? "当前为普通对话。点击“设为任务”后，AI 可以保存计划与步骤进度。"
                : "复杂任务建议保存计划，步骤进度显示在这里；简单需求可直接处理。");
        PlanNextActionText.Text = task?.NextAction ?? "";
        PlanNextActionBorder.Visibility = string.IsNullOrWhiteSpace(task?.NextAction) ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed class PlanStepVm
    {
        public string Title { get; }
        public string Symbol { get; }
        public string StatusLabel { get; }
        public string Evidence { get; }
        public Visibility EvidenceVisibility => string.IsNullOrWhiteSpace(Evidence) ? Visibility.Collapsed : Visibility.Visible;

        public PlanStepVm(WorkStep step, string taskStatus)
        {
            Title = step.Title;
            Evidence = step.Evidence;
            Symbol = step.Status switch { "in_progress" => "○", "completed" => "✓", _ => "–" };
            StatusLabel = step.Status switch
            {
                "completed" => "已完成",
                "in_progress" when taskStatus == "interrupted" => "正在进行 · 已中断",
                "in_progress" when taskStatus == "paused" => "正在进行 · 已暂停",
                "in_progress" => "正在进行",
                "blocked" => "受阻 · 等待处理",
                _ => "未开始"
            };
        }
    }
}
