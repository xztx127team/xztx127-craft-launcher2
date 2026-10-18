using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    // A run belongs to the exact session object, rather than whichever conversation is visible.
    // Switching the view changes only the controls; cancellation, streaming and persistence stay
    // attached to this record until its request completes.
    private sealed class SessionRun(AiChatSession session, BubbleVm pending, CancellationTokenSource cancellation, AiWorkMode workMode)
    {
        public AiChatSession Session { get; } = session;
        public BubbleVm Pending { get; } = pending;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public AiWorkMode WorkMode { get; private set; } = AiWorkModePolicy.Normalize(workMode);
        public AiWorkMode EffectiveWorkMode { get; private set; } = AiWorkModePolicy.Normalize(workMode);
        public void SetWorkMode(AiWorkMode mode)
        {
            WorkMode = AiWorkModePolicy.Normalize(mode);
            // Capture revocation immediately, even if the model is awaiting a slow
            // response and the user switches back before its next policy check.
            if (WorkMode == AiWorkMode.Chat) EffectiveWorkMode = AiWorkMode.Chat;
            else if (WorkMode == AiWorkMode.Plan && EffectiveWorkMode != AiWorkMode.Chat)
                EffectiveWorkMode = AiWorkMode.Plan;
        }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public bool Completed { get; set; }
        public string RouteStatus { get; set; } = "正在生成…";
        public string? FailureText { get; set; }
        public AiQuestionCard? Question { get; set; }
    }

    private readonly Dictionary<string, SessionRun> _runningSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiWorkMode> _sessionWorkModes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiChatSession> _unsavedSessions = new(StringComparer.Ordinal);
    private bool _newSessionDecisionPending;

    private SessionRun? CurrentRun => _runningSessions.TryGetValue(_session.Id, out var run) ? run : null;
    private bool IsCurrentRun(SessionRun run) => ReferenceEquals(run.Session, _session) && ReferenceEquals(CurrentRun, run);

    private AiChatSession? FindRunningSession(string? taskId) => string.IsNullOrWhiteSpace(taskId) ? null
        : _runningSessions.Values.FirstOrDefault(run => !run.Completed && run.Session.TaskId == taskId)?.Session;

    private void SaveEarlyRunFailure(AiAssistantService service, SessionRun run, AiChatMessage? previousAssistant)
    {
        // The service normally persists a stopped/failed assistant turn. Validation and BeginTask
        // can fail before its collector is created; retain those errors for background history too.
        var session = run.Session;
        var last = session.Messages.LastOrDefault();
        if (last?.Role != AiMessageRole.Assistant || ReferenceEquals(last, previousAssistant))
            session.Messages.Add(new AiChatMessage { Role = AiMessageRole.Assistant, Content = run.FailureText ?? "请求失败。" });
        try
        {
            service.SaveSession(session);
            _unsavedSessions.Remove(session.Id);
        }
        catch (Exception ex)
        {
            // Retain the live object so switching back cannot replace the latest turn with an old
            // disk snapshot. Disk errors are shown in that conversation, never thrown by async void.
            _unsavedSessions[session.Id] = session;
            var notice = "会话保存失败：" + ex.Message + "（当前内容暂保留在内存中）";
            run.RouteStatus += " · 会话保存失败";
            session.Messages.Last().Content += "\n\n" + notice;
        }
    }

    private void StartNewSession()
    {
        if (_service == null) return;
        try { SwitchToSession(_service.CreateNewSession()); }
        catch (Exception ex) { ToastService.ShowWarning("新建会话失败：" + ex.Message); }
    }

    /// <summary>Restore the live object for a running session, including its stream, question and stop button.</summary>
    private void SwitchToSession(AiChatSession session)
    {
        if (_session.Id == session.Id) return;
        _sessionDrafts[_session.Id] = InputTextBox.Text;
        _sessionWorkModes[_session.Id] = CurrentRun?.WorkMode ?? _config.WorkMode;
        SaveAttachmentDraft(_session.Id);
        _session = _runningSessions.TryGetValue(session.Id, out var run) ? run.Session
            : _unsavedSessions.TryGetValue(session.Id, out var unsaved) ? unsaved : session;
        // Restore the selected conversation's mode without changing any other run's
        // live mode callback. Configuration still supplies the shared permission gates.
        _config.WorkMode = run?.WorkMode
            ?? (_sessionWorkModes.TryGetValue(_session.Id, out var mode) ? mode : _config.WorkMode);
        RefreshWorkModeUi();
        ClearInputAndResetUndo();
        InputTextBox.Text = _sessionDrafts.TryGetValue(_session.Id, out var draft) ? draft : "";
        RestoreAttachmentDraft(_session.Id);
        RememberCurrentSession();
        RebuildBubbles();
        SetSendingState(run != null);
        RouteStatusText.Text = run?.RouteStatus ?? "";
        RefreshHistory();
        UpdateRoutingText();
        ScrollToBottom();
        if (InputArea.IsEnabled) InputTextBox.Focus();
    }

    private void RefreshSessionQuestionVisibility()
    {
        foreach (var run in _runningSessions.Values)
            if (run.Question != null)
                run.Question.Visibility = IsCurrentRun(run) && run.Question.IsAsking ? Visibility.Visible : Visibility.Collapsed;
        InputArea.IsEnabled = CurrentRun?.Question?.IsAsking != true;
    }

    private async Task<AiQuestionAnswer?> ShowSessionQuestionAsync(SessionRun run, AiQuestionSet set, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Independent cards keep an old conversation's pending answer intact when a new session
        // also asks a question. Only the current session's card is shown over the shared input area.
        if (QuestionCard.Parent is not Panel host) return null;
        var card = new AiQuestionCard
        {
            Margin = QuestionCard.Margin,
            VerticalAlignment = QuestionCard.VerticalAlignment,
            Visibility = Visibility.Collapsed,
            AllowStopping = true
        };
        card.StopRequested += (_, _) =>
        {
            if (run.Completed || run.Cancellation.IsCancellationRequested) return;
            card.AllowStopping = false;
            run.RouteStatus = "正在停止生成…";
            if (IsCurrentRun(run))
            {
                RouteStatusText.Text = run.RouteStatus;
                StopButton.IsEnabled = false;
            }
            run.Cancellation.Cancel();
        };
        run.Question = card;
        host.Children.Add(card);
        try
        {
            var answer = card.AskAsync(set, ct);
            RefreshSessionQuestionVisibility();
            if (IsCurrentRun(run)) ScrollToBottom();
            else RefreshHistory();
            return await answer;
        }
        finally
        {
            if (ReferenceEquals(run.Question, card)) run.Question = null;
            host.Children.Remove(card);
            RefreshSessionQuestionVisibility();
        }
    }
}
