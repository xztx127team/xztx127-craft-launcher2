using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private bool _revokingAiTerms;
    internal async Task RevokeAiTermsAsync(Action? onConfirmed = null, Window? confirmationOwner = null)
    {
        if (_revokingAiTerms) return;
        _revokingAiTerms = true;
        var owner = confirmationOwner ?? this;
        var revoked = false;
        try
        {
            // Keep confirmations with the window that was clicked. Activating the launcher here
            // can hide its overlay behind a topmost chat or a modal AI settings window.
            if (!owner.IsVisible) owner.Show();
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            owner.Activate();
            if (!AiTermsRevocationConfirmation.Confirm(owner)) return;
            var previousTerms = AiAssistantConfig.AcceptedTermsVersion;
            AiAssistantConfig.AcceptedTermsVersion = 0;
            try { PersistAiAssistantConfig(); }
            catch (Exception ex)
            {
                AiAssistantConfig.AcceptedTermsVersion = previousTerms;
                ErrorPresenter.LogFallback("保存 AI 协议撤回状态失败", ex);
                AiTermsRevocationConfirmation.ShowError(owner, "撤回协议未能保存，请检查配置目录后重试。");
                return;
            }
            revoked = true;
            // A failing settings close callback must not leave AI requests running after consent
            // has already been withdrawn.
            var closeViewFailed = false;
            try { onConfirmed?.Invoke(); }
            catch (Exception ex)
            {
                closeViewFailed = true;
                ErrorPresenter.LogFallback("关闭 AI 设置界面失败", ex);
            }
            AiAssistantService.UpdateConfig(AiAssistantConfig);
            var panel = _aiAssistantPanel;
            _aiAssistantPanel = null;
            var floating = _floatingAiChat;
            _floatingAiChat = null;
            _floatingDrawerEntry = null;
            var reports = _crashAnalysisDrawers.ToArray();
            if (floating != null) { _aiSideDrawer?.RemoveEntry(floating.Panel); floating.Hide(); }
            foreach (var report in reports) _aiSideDrawer?.RemoveEntry(report.DrawerEntry.View);
            var stopping = new List<Task>();
            if (panel != null) stopping.Add(panel.StopForOverlayCloseAsync());
            if (floating != null) stopping.Add(floating.ShutdownAsync());
            foreach (var report in reports) stopping.Add(report.DisposeAsync());
            AiSideDrawerLayer.Visibility = Visibility.Collapsed;
            ReminderCenterService.Remove("ai-side-drawer-hidden");
            _crashAnalysisDrawers.Clear();
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            NavigateToHome();
            await Task.WhenAll(stopping);
            if (closeViewFailed)
                AiTermsRevocationConfirmation.ShowError(owner.IsVisible ? owner : this,
                    "协议已撤回，AI 请求已停止，但设置界面未能自动关闭，请手动关闭该界面。再次打开 AI 助手时需要重新阅读协议。");
            else ToastService.ShowInfo("已关闭 AI 助手。再次打开时请重新阅读协议。");
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("停止 AI 助手失败", ex);
            AiTermsRevocationConfirmation.ShowError(owner.IsVisible ? owner : this, revoked
                ? "协议已撤回，但关闭 AI 助手时遇到问题。请重新启动启动器；再次打开 AI 助手时需要重新阅读协议。"
                : "未能关闭 AI 助手，协议状态未更改，请重试。");
        }
        finally { _revokingAiTerms = false; }
    }
}
