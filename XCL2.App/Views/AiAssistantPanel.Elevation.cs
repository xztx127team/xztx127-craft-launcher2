using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    private readonly Dictionary<string, AiElevationSession> _elevationSessions = new(StringComparer.Ordinal);

    private Window? AiInteractionWindow => Window.GetWindow(this) ?? _owner ?? Application.Current?.MainWindow;

    private AiElevationInteraction CreateElevationInteraction(SessionRun run)
    {
        if (!_elevationSessions.TryGetValue(run.Session.Id, out var session))
            _elevationSessions.Add(run.Session.Id, session = new AiElevationSession());
        return new AiElevationInteraction
        {
            Session = session,
            Activate = () => ActivateAiInteraction(run),
            IsPresented = () => IsLoaded && IsVisible && IsCurrentRun(run),
            AskAsync = (request, ct) => Dispatcher.CheckAccess()
                ? ShowSessionElevationAsync(run, request, ct)
                : Dispatcher.InvokeAsync(() => ShowSessionElevationAsync(run, request, ct)).Task.Unwrap()
        };
    }

    private bool ConfirmAiAccessRequest(string title, string message)
    {
        if (ConfigService.Active?.Config.NativeBackgroundApprovals != false)
            return NativeApprovalService.Confirm(message, title, AiInteractionWindow);

        // MessageBoxDialog is hosted by OverlayDialogService in the main window, even when this
        // request came from a Beta chat window. Its notice must reveal that actual approval host.
        var floating = Window.GetWindow(this) as AiFloatingChatWindow;
        var revealFloatingAfterwards = floating?.IsVisible == true;
        if (revealFloatingAfterwards) floating!.Hide(); // owned/topmost chat must not cover the main approval overlay
        using var notice = AiAttentionService.Track(() => Application.Current?.MainWindow,
            () => true, title, message, () =>
            {
                var main = Application.Current?.MainWindow;
                if (main == null) return;
                if (!main.IsVisible) main.Show();
                if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                main.Activate();
                SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(main).Handle);
            });
        try { return MessageBoxDialog.ShowConfirm(message, title); }
        finally { if (revealFloatingAfterwards && !_overlayClosed) floating!.Reveal(); }
    }

    private void ActivateAiInteraction(SessionRun? run)
    {
        RequestHostReveal?.Invoke();
        var host = AiInteractionWindow;
        if (RequestHostReveal == null && host is MainWindow main && (!IsLoaded || !IsVisible)) main.NavigateToAiAssistant();
        if (run != null) SwitchToSession(run.Session);
        if (host == null) return;
        if (host is AiFloatingChatWindow floating) floating.Reveal();
        if (!host.IsVisible) host.Show();
        if (host.WindowState == WindowState.Minimized) host.WindowState = WindowState.Normal;
        host.Activate();
        SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(host).Handle);
        if (run?.Elevation is { } card) card.BringIntoView();
        else if (run?.Question is { } question) question.BringIntoView();
    }

    private async Task<AiElevationDecision> ShowSessionElevationAsync(SessionRun run, AiElevationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (run.Completed || _overlayClosed || QuestionCard.Parent is not Panel host) return AiElevationDecision.Deny;
        var card = new AiElevationRequestCard(request) { Margin = QuestionCard.Margin, VerticalAlignment = VerticalAlignment.Stretch };
        run.Elevation = card;
        host.Children.Add(card);
        Panel.SetZIndex(card, 20);
        using var cancellation = ct.Register(() => Dispatcher.BeginInvoke(new Action(() => card.Cancel(ct))));
        using var notice = AiAttentionService.Track(() => AiInteractionWindow,
            () => IsLoaded && IsVisible && IsCurrentRun(run), "AI 助手申请管理员权限",
            $"{request.Shell}：{request.Command}\n完整重试可能重复原命令已完成的部分操作。允许后仍需确认系统 UAC。",
            () => ActivateAiInteraction(run), card.Resolve);
        try
        {
            run.RouteStatus = "等待管理员权限审批…";
            RefreshSessionQuestionVisibility();
            if (IsCurrentRun(run)) { RouteStatusText.Text = run.RouteStatus; ScrollToBottom(); }
            return await card.Completion;
        }
        finally
        {
            if (ReferenceEquals(run.Elevation, card)) run.Elevation = null;
            host.Children.Remove(card);
            RefreshSessionQuestionVisibility();
            if (!run.Completed)
            {
                run.RouteStatus = ct.IsCancellationRequested ? "正在停止生成…" : "正在处理命令…";
                if (IsCurrentRun(run)) RouteStatusText.Text = run.RouteStatus;
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
