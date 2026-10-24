using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private AiFloatingBallWindow? _desktopAiBall;
    private AiFloatingChatWindow? _floatingAiChat;
    private bool _floatingBetaInitialized;
    private bool _floatingBetaShuttingDown;
    private bool _openingFloatingAiChat;
    private object? _normalAiBallContent;

    private AiFloatingBetaSettings FloatingBetaSettings
        => ConfigService.Config.AiFloatingBeta ??= new AiFloatingBetaSettings();

    private void InitializeAiFloatingBeta()
    {
        if (_floatingBetaInitialized) return;
        _floatingBetaInitialized = true;
        _normalAiBallContent = AiFloatingButton.Content;
        AiFloatingButton.ContextMenu = CreateAiBallMenu();
        Loaded += (_, _) => SynchronizeAiFloatingBeta();
        Closed += async (_, _) =>
        {
            _floatingBetaShuttingDown = true;
            _desktopAiBall?.CloseFromHost();
            _desktopAiBall = null;
            if (_floatingAiChat != null)
            {
                try { await _floatingAiChat.ShutdownAsync(); }
                catch (Exception ex) { ErrorPresenter.LogFallback("关闭 AI 悬浮聊天失败", ex); }
                _floatingAiChat = null;
            }
        };
    }

    private void SynchronizeAiFloatingBeta(bool? forceShow = null)
    {
        if (AiFloatingButton == null || _floatingBetaShuttingDown) return;
        InitializeAiFloatingBeta();
        var settings = FloatingBetaSettings;
        var enabled = ConfigService.Config.AiAssistantFloatingButton;
        var desktop = settings.Enabled && settings.Scope == AiFloatingBallScope.Desktop;
        // Beta remains reachable while the ordinary AI page is open; a chat-window X never hides the ball.
        AiFloatingButton.Visibility = enabled && !desktop && (settings.Enabled || forceShow != false)
            ? Visibility.Visible : Visibility.Collapsed;
        if (settings.Enabled) AiFloatingBallAppearance.Apply(AiFloatingButton, settings, 40);
        else
        {
            AiFloatingButton.ClearValue(Control.TemplateProperty);
            AiFloatingButton.Content = _normalAiBallContent;
            AiFloatingButton.Width = AiFloatingButton.Height = 40;
            AiFloatingButton.ToolTip = "点击打开 AI 菜单；右键可配置悬浮球样式、颜色和 Beta 聊天窗口。";
        }
        if (!enabled || !desktop)
        {
            _desktopAiBall?.CloseFromHost();
            _desktopAiBall = null;
        }
        else if (IsLoaded)
        {
            if (_desktopAiBall == null)
            {
                _desktopAiBall = new AiFloatingBallWindow(settings, OpenFloatingAiChat, CreateAiBallMenu,
                    SaveDesktopAiBallPosition, () => { _desktopAiBall = null; CloseAiFloatingBall(); });
                _desktopAiBall.Show();
            }
            else _desktopAiBall.ApplySettings(settings);
        }
        SynchronizeFloatingAiChatHost();
    }

    private void OpenAiFloatingEntry()
    {
        if (FloatingBetaSettings.Enabled) OpenFloatingAiChat();
        else
        {
            var menu = AiFloatingButton.ContextMenu ??= CreateAiBallMenu();
            menu.PlacementTarget = AiFloatingButton;
            menu.Placement = PlacementMode.Top;
            menu.IsOpen = true;
        }
    }

    private ContextMenu CreateAiBallMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => PopulateAiBallMenu(menu);
        PopulateAiBallMenu(menu);
        return menu;
    }

    private void PopulateAiBallMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var settings = FloatingBetaSettings;
        MenuItem Add(string label, Action action, string? hint = null)
        {
            var item = new MenuItem { Header = label, ToolTip = hint };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        Add(settings.Enabled ? "打开聊天悬浮窗" : "打开 AI 助手", settings.Enabled ? OpenFloatingAiChat : NavigateToAiAssistant,
            "继续当前对话；窗口关闭后仍可从这里重新打开。");
        if (_aiSideDrawer?.Current != null) Add("继续查看右侧 AI / 分析报告", RevealAiSideDrawer);
        Add(settings.Enabled ? "悬浮球设置…" : "开启悬浮聊天…", OpenAiFloatingBetaSettings,
            "设置悬浮球颜色、样式、图片、显示范围，以及悬浮球和聊天窗口各自的置顶状态。");
        if (settings.Enabled)
        {
            var current = _floatingAiChat?.Panel.FloatingConfig;
            var modeMenu = new MenuItem { Header = "工作模式", ToolTip = "选择执行、规划、智能执行或纯聊天；当前会话保持不变。" };
            foreach (var pair in new[] { (AiWorkMode.Build, "Building · 执行"), (AiWorkMode.Plan, "Plan · 规划"),
                (AiWorkMode.Agent, "Agent · 智能执行"), (AiWorkMode.Chat, "纯聊天") })
            {
                var mode = pair.Item1;
                var item = new MenuItem { Header = pair.Item2, IsCheckable = true,
                    IsChecked = (current?.WorkMode ?? settings.WorkMode) == mode };
                item.Click += (_, _) => ChangeFloatingMode(mode);
                modeMenu.Items.Add(item);
            }
            menu.Items.Add(modeMenu);
            var modelMenu = new MenuItem { Header = "模型", IsEnabled = _floatingAiChat?.Panel.CanChangeFloatingModel != false,
                ToolTip = "展开后输入名称、ID 或供应商搜索；滚轮可上下切换模型。正在生成时不能更换模型。" };
            var route = current?.RoutingMode ?? settings.RoutingMode;
            foreach (var pair in new[] { (AiRoutingMode.Auto, "自动路由"), (AiRoutingMode.Normal, "普通默认模型"), (AiRoutingMode.Expert, "专家默认模型") })
            {
                var selectedRoute = pair.Item1;
                var item = new MenuItem { Header = pair.Item2, IsCheckable = true, IsChecked = route == selectedRoute };
                item.Click += (_, _) => ChangeFloatingModel(null, selectedRoute);
                modelMenu.Items.Add(item);
            }
            modelMenu.Items.Add(new Separator());
            modelMenu.Items.Add(new MenuItem { StaysOpenOnClick = true, Padding = new Thickness(0),
                Header = new AiModelMenuPicker(AiModelIds.GetCatalog(current ?? AiAssistantConfig),
                    current?.SelectedModel ?? settings.SelectedModel,
                    key => ChangeFloatingModel(key, AiRoutingMode.SpecificModel), () => menu.IsOpen = false) });
            menu.Items.Add(modelMenu);
            var ballTopmost = new MenuItem { Header = "悬浮球置顶", IsCheckable = true, IsChecked = settings.BallTopmost,
                ToolTip = "桌面悬浮球保持在其他普通窗口上方；不改变聊天窗口的置顶状态。" };
            ballTopmost.Click += (_, _) => SetAiFloatingTopmost(ball: true, enabled: ballTopmost.IsChecked);
            menu.Items.Add(ballTopmost);
            var chatTopmost = new MenuItem { Header = "聊天窗口置顶", IsCheckable = true, IsChecked = settings.ChatTopmost,
                ToolTip = "桌面聊天窗口保持在其他普通窗口上方；不改变悬浮球的置顶状态。" };
            chatTopmost.Click += (_, _) => SetAiFloatingTopmost(ball: false, enabled: chatTopmost.IsChecked);
            menu.Items.Add(chatTopmost);
            Add("关闭 Beta，保留普通入口", () =>
            {
                settings.Enabled = false;
                SaveAiFloatingBeta();
                HideFloatingAiChat();
                SynchronizeAiFloatingBeta();
            });
        }
        menu.Items.Add(new Separator());
        Add("关闭悬浮球", CloseAiFloatingBall, "隐藏悬浮球；可以在 AI 助手设置中重新开启。");
    }

    internal void SetAiFloatingTopmost(bool ball, bool enabled)
    {
        var settings = FloatingBetaSettings;
        var previous = ball ? settings.BallTopmost : settings.ChatTopmost;
        if (ball) settings.BallTopmost = enabled;
        else settings.ChatTopmost = enabled;
        try
        {
            ConfigService.Save();
        }
        catch (Exception ex)
        {
            if (ball) settings.BallTopmost = previous;
            else settings.ChatTopmost = previous;
            ErrorPresenter.LogFallback("保存 AI 悬浮窗置顶设置失败", ex);
            ToastService.ShowInfo("置顶设置未能保存，请稍后再试。");
            return;
        }
        // A preference toggle must not restore a minimized chat or reopen a hidden drawer.
        _desktopAiBall?.ApplySettings(settings);
        _floatingAiChat?.ApplyScope(settings.Scope);
    }

    private void OpenAiFloatingBetaSettings()
    {
        if (_floatingBetaShuttingDown) return;
        Window owner = _floatingAiChat?.IsVisible == true ? _floatingAiChat : this;
        if (!owner.IsVisible) owner.Show();
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        var dialog = new AiFloatingBetaSettingsDialog(owner, FloatingBetaSettings, draft =>
        {
            var previous = ConfigService.Config.AiFloatingBeta;
            var previousShow = AiAssistantConfig.ShowFloatingButton;
            // The appearance dialog does not own live mode/model/session or a dragged ball's position.
            // Merge those from the latest state if the desktop menu changed them while it was open.
            var next = FloatingBetaSettings.Copy();
            next.Enabled = draft.Enabled;
            next.Style = draft.Style;
            next.Scope = draft.Scope;
            next.BallTopmost = draft.BallTopmost;
            next.ChatTopmost = draft.ChatTopmost;
            next.Color = draft.Color;
            next.ImagePath = draft.ImagePath;
            ConfigService.Config.AiFloatingBeta = next;
            AiAssistantConfig.ShowFloatingButton = true;
            try { PersistAiAssistantConfig(); }
            catch
            {
                ConfigService.Config.AiFloatingBeta = previous;
                AiAssistantConfig.ShowFloatingButton = previousShow;
                throw;
            }
            SynchronizeAiFloatingBeta();
            ToastService.ShowInfo("✓ 悬浮球 Beta 设置已应用");
        });
        dialog.ShowDialog();
    }

    internal AiAssistantConfig CreateFloatingAiConfig()
    {
        var local = JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(AiAssistantConfig)) ?? new AiAssistantConfig();
        var settings = FloatingBetaSettings;
        local.WorkMode = AiWorkModePolicy.Normalize(settings.WorkMode);
        local.RoutingMode = Enum.IsDefined(settings.RoutingMode) ? settings.RoutingMode : AiRoutingMode.Auto;
        local.AutoModelRouting = local.RoutingMode == AiRoutingMode.Auto;
        if (AiModelIds.FindInCatalog(local, settings.SelectedModel) is { } model) local.SelectedModel = model.ModelKey;
        local.LastSessionId = System.Text.RegularExpressions.Regex.IsMatch(settings.SessionId ?? "", "^[a-f0-9]{32}$") ? settings.SessionId : null;
        local.ShowTaskSelectionBar = false;
        return local;
    }

    internal void RefreshAiPanelsAfterFloatingSettings()
    {
        _aiAssistantPanel?.ApplyConfig(AiAssistantConfig);
        UpdateAiFloatingButtonVisibility();
    }

    private void OpenFloatingAiChat()
    {
        if (_floatingBetaShuttingDown || _openingFloatingAiChat || _revokingAiTerms) return;
        _openingFloatingAiChat = true;
        try
        {
            // These existing agreement flows are hosted by MainWindow's overlay. Restore it only
            // for first-use confirmation; approved desktop chat does not depend on its visibility.
            if (ConfigService.Config.RestrictedMode || AiAssistantConfig.AcceptedTermsVersion < AiTermsDialog.CurrentVersion)
            {
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            }
            if (!EnsureNotRestrictedModeForAi() || !EnsureAiTermsAccepted()) return;
            if (_floatingAiChat == null)
            {
                _floatingAiChat = new AiFloatingChatWindow(this, CreateFloatingAiConfig(), SaveFloatingQuickConfig, OpenAiFloatingBetaSettings);
                _floatingAiChat.LauncherRevealRequested = PresentFloatingAiChat;
                _floatingAiChat.LauncherCollapseRequested = HideFloatingAiChat;
                var work = SystemParameters.WorkArea;
                _floatingAiChat.Left = Math.Clamp(Left + ActualWidth - _floatingAiChat.Width - 24, work.Left, Math.Max(work.Left, work.Right - _floatingAiChat.Width));
                _floatingAiChat.Top = Math.Clamp(Top + ActualHeight - _floatingAiChat.Height - 24, work.Top, Math.Max(work.Top, work.Bottom - _floatingAiChat.Height));
            }
            _floatingAiChat.ApplyScope(FloatingBetaSettings.Scope);
            PresentFloatingAiChat();
        }
        catch (Exception ex) { MessageBoxDialog.ShowError("打开 AI 悬浮聊天失败：" + ex.Message); }
        finally { _openingFloatingAiChat = false; }
    }

    internal void DockFloatingAiChat()
    {
        if (_floatingAiChat == null || _floatingBetaShuttingDown) return;
        var previous = FloatingBetaSettings.Scope;
        FloatingBetaSettings.Scope = AiFloatingBallScope.Launcher;
        try { ConfigService.Save(); }
        catch (Exception ex)
        {
            FloatingBetaSettings.Scope = previous;
            ToastService.ShowWarning("未能保存聊天位置：" + ex.Message);
            return;
        }
        _floatingAiChat.Hide();
        _floatingAiChat.ApplyScope(AiFloatingBallScope.Launcher);
        RestoreFromTray();
        PresentFloatingAiChat();
        SynchronizeAiFloatingBeta();
    }

    private void SaveFloatingQuickConfig(AiAssistantConfig local)
    {
        var settings = FloatingBetaSettings;
        settings.WorkMode = local.WorkMode;
        settings.RoutingMode = local.RoutingMode;
        settings.SelectedModel = local.SelectedModel;
        settings.SessionId = local.LastSessionId ?? "";
        SaveAiFloatingBeta();
    }

    private void ChangeFloatingMode(AiWorkMode mode)
    {
        if (_floatingAiChat != null) _floatingAiChat.Panel.SelectFloatingMode(mode);
        else { FloatingBetaSettings.WorkMode = mode; SaveAiFloatingBeta(); }
    }

    private void ChangeFloatingModel(string? key, AiRoutingMode route)
    {
        if (_floatingAiChat != null) _floatingAiChat.Panel.SelectFloatingModel(key, route);
        else
        {
            FloatingBetaSettings.RoutingMode = route;
            if (!string.IsNullOrWhiteSpace(key)) FloatingBetaSettings.SelectedModel = key;
            SaveAiFloatingBeta();
        }
    }

    private void SaveDesktopAiBallPosition()
    {
        if (_desktopAiBall == null) return;
        FloatingBetaSettings.DesktopLeft = _desktopAiBall.Left;
        FloatingBetaSettings.DesktopTop = _desktopAiBall.Top;
        SaveAiFloatingBeta();
    }

    private void SaveAiFloatingBeta()
    {
        try { ConfigService.Save(); }
        catch (Exception ex) { ToastService.ShowWarning("保存悬浮球设置失败：" + ex.Message); }
    }

    private void CloseAiFloatingBall()
    {
        AiAssistantConfig.ShowFloatingButton = false;
        try { PersistAiAssistantConfig(); }
        catch (Exception ex) { ToastService.ShowWarning("保存悬浮球关闭状态失败：" + ex.Message); }
        SynchronizeAiFloatingBeta();
    }
}
