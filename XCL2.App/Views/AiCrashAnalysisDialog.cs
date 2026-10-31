using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>独立的只读报告会话；收起仅隐藏宿主，报告和后台请求继续保留。</summary>
internal sealed class AiCrashAnalysisDialog : UserControl
{
    private readonly MainWindow _owner;
    private readonly AiAssistantPanel _panel;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _prompt;
    private readonly string _sessionTitle;
    private bool _started;
    private bool _disposed;
    internal event Action? CollapseRequested;
    internal AiSideDrawer.Entry DrawerEntry { get; }

    internal AiCrashAnalysisDialog(MainWindow owner, string prompt, string sessionTitle)
    {
        _owner = owner;
        _prompt = prompt;
        _sessionTitle = sessionTitle;
        // 分析拥有独立配置和会话，切换报告不改变普通 AI 页当前打开的对话或执行模式。
        var config = CloneConfig(owner.AiAssistantConfig);
        config.LastSessionId = null;
        _panel = new AiAssistantPanel { RequireCrashLogPermission = true, SuppressPrewarm = true,
            AnalysisOnly = true, FloatingQuickConfigSaved = _ => { } };
        _panel.EnableSideDrawerView();
        var service = new AiAssistantService(config, App.DataDir, _http)
        {
            ForceReadOnly = true,
            SharedAccountingConfigProvider = () => owner.AiAssistantConfig,
            SaveSharedAccounting = () => owner.ConfigService.Save()
        };
        _panel.Attach(service, config);
        _panel.CloseRequested += (_, _) => CollapseRequested?.Invoke();
        _panel.SettingsRequested += (_, _) => ShowSettings();
        Content = _panel;
        DrawerEntry = new AiSideDrawer.Entry(sessionTitle, this, ShowSettings);
        Loaded += Dialog_Loaded;
    }

    private static AiAssistantConfig CloneConfig(AiAssistantConfig config)
        => JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(config)) ?? new AiAssistantConfig();

    internal void SetRevealAction(Action reveal) => _panel.RequestHostReveal = reveal;

    private async void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started || _disposed) return;
        _started = true;
        try { await _panel.SubmitExternalPromptAsync(_prompt, _sessionTitle, isCrashLogContext: true); }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("打开崩溃 AI 分析失败", ex);
            if (!_disposed) ToastService.ShowWarning("准备 AI 分析失败：" + ex.Message);
        }
    }

    internal void ShowSettings()
    {
        var settings = new AiAssistantSettingsPanel(_owner.AiAssistantConfig);
        settings.Saved += (_, config) =>
        {
            _owner.AiAssistantConfig = config;
            _owner.AiAssistantService.UpdateConfig(config);
            _owner.PersistAiAssistantConfig();
            var local = CloneConfig(config);
            local.LastSessionId = _panel.FloatingConfig.LastSessionId;
            _panel.ApplyConfig(local);
        };
        OverlayDialogService.ShowModal(settings);
    }

    internal async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= Dialog_Loaded;
        try { await _panel.StopForOverlayCloseAsync(); }
        finally { _http.Dispose(); }
    }
}
