using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private AiSideDrawer? _aiSideDrawer;
    private bool _aiDrawerDocked = true;
    private bool _aiDrawerInitialized;
    private AiSideDrawer.Entry? _floatingDrawerEntry;
    private readonly List<AiCrashAnalysisDialog> _crashAnalysisDrawers = new();

    private void EnsureAiSideDrawer()
    {
        if (_aiDrawerInitialized) return;
        _aiDrawerInitialized = true;
        _aiSideDrawer = new AiSideDrawer();
        AiSideDrawerLayer.Children.Add(_aiSideDrawer);
        _aiSideDrawer.CollapseRequested += CollapseAiSideDrawer;
        _aiSideDrawer.DockRequested += () => { _aiDrawerDocked = true; LayoutAiSideDrawer(); };
        _aiSideDrawer.DragRequested += (dx, dy) =>
        {
            _aiDrawerDocked = false;
            LayoutAiSideDrawer(Canvas.GetLeft(_aiSideDrawer) + dx, Canvas.GetTop(_aiSideDrawer) + dy);
        };
        AiSideDrawerLayer.SizeChanged += (_, _) => LayoutAiSideDrawer();
        AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape || OverlayDialogService.HasActiveOverlay || AiSideDrawerLayer.Visibility != Visibility.Visible) return;
            CollapseAiSideDrawer();
            args.Handled = true;
        }));
        Closed += async (_, _) =>
        {
            foreach (var report in _crashAnalysisDrawers.ToArray())
                try { await report.DisposeAsync(); }
                catch (Exception ex) { ErrorPresenter.LogFallback("关闭 AI 分析面板失败", ex); }
            _crashAnalysisDrawers.Clear();
        };
    }

    private void LayoutAiSideDrawer(double? x = null, double? y = null)
    {
        if (_aiSideDrawer == null) return;
        var bounds = AiSideDrawerLayout.Place(AiSideDrawerLayer.ActualWidth, AiSideDrawerLayer.ActualHeight,
            _aiDrawerDocked, x ?? Canvas.GetLeft(_aiSideDrawer), y ?? Canvas.GetTop(_aiSideDrawer));
        _aiSideDrawer.Width = bounds.Width;
        _aiSideDrawer.Height = bounds.Height;
        Canvas.SetLeft(_aiSideDrawer, bounds.X);
        Canvas.SetTop(_aiSideDrawer, bounds.Y);
    }

    private void ShowAiSideDrawer(AiSideDrawer.Entry entry)
    {
        EnsureAiSideDrawer();
        _aiSideDrawer!.ShowEntry(entry);
        AiSideDrawerLayer.Visibility = Visibility.Visible;
        ReminderCenterService.Remove("ai-side-drawer-hidden");
        LayoutAiSideDrawer();
        // 不改变 MainContent/侧边栏 IsEnabled，不使用 OverlayRoot 或全屏透明遮罩。
    }

    private void CollapseAiSideDrawer()
    {
        AiSideDrawerLayer.Visibility = Visibility.Collapsed;
        if (_aiSideDrawer?.Current != null)
            ReminderCenterService.Publish("ai-side-drawer-hidden", "AI 面板已收起",
                "会话和分析报告仍然保留，后台请求继续运行；停止生成请重新打开后点击停止。",
                "继续查看", () => { RevealAiSideDrawer(); return Task.CompletedTask; });
        UpdateAiFloatingButtonVisibility(MainContent.Content is not AiAssistantPanel);
    }

    private void RevealAiSideDrawer()
    {
        if (_aiSideDrawer?.Current == null) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowAiSideDrawer(_aiSideDrawer.Current);
        Activate();
    }

    private void HideFloatingAiChat()
    {
        _floatingAiChat?.Hide();
        if (_floatingAiChat != null && ReferenceEquals(_aiSideDrawer?.Current?.View, _floatingAiChat.Panel)) CollapseAiSideDrawer();
    }

    private void SynchronizeFloatingAiChatHost()
    {
        if (_floatingAiChat == null) return;
        var wasVisible = _floatingAiChat.IsVisible || AiSideDrawerLayer.Visibility == Visibility.Visible && ReferenceEquals(_aiSideDrawer?.Current?.View, _floatingAiChat.Panel);
        _floatingAiChat.ApplyScope(FloatingBetaSettings.Scope);
        if (FloatingBetaSettings.Scope == AiFloatingBallScope.Desktop)
        {
            var wasCurrent = ReferenceEquals(_aiSideDrawer?.Current?.View, _floatingAiChat.Panel);
            _aiSideDrawer?.RemoveEntry(_floatingAiChat.Panel);
            if (wasCurrent && _aiSideDrawer?.Current == null) CollapseAiSideDrawer();
            _floatingAiChat.AttachPanelToWindow();
            _floatingAiChat.Panel.RequestHostReveal = PresentFloatingAiChat;
            if (wasVisible) _floatingAiChat.Reveal();
        }
        else if (wasVisible) PresentFloatingAiChat();
    }

    private void PresentFloatingAiChat()
    {
        if (_floatingAiChat == null) return;
        if (FloatingBetaSettings.Scope == AiFloatingBallScope.Desktop)
        {
            var wasCurrent = ReferenceEquals(_aiSideDrawer?.Current?.View, _floatingAiChat.Panel);
            _aiSideDrawer?.RemoveEntry(_floatingAiChat.Panel);
            if (wasCurrent && _aiSideDrawer?.Current == null) CollapseAiSideDrawer();
            _floatingAiChat.AttachPanelToWindow();
            _floatingAiChat.Panel.RequestHostReveal = () => _floatingAiChat?.Reveal();
            _floatingAiChat.Reveal();
            return;
        }
        _floatingAiChat.Hide();
        _floatingAiChat.DetachPanelFromWindow();
        _floatingDrawerEntry ??= new AiSideDrawer.Entry("AI助手", _floatingAiChat.Panel,
            _floatingAiChat.OpenAiSettings, _floatingAiChat.Panel.NewFloatingConversation);
        _floatingAiChat.Panel.RequestHostReveal = PresentFloatingAiChat;
        ShowAiSideDrawer(_floatingDrawerEntry);
    }
}
