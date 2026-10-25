using System.Globalization;
using System.Text.Json;
using XCL2.App.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using XCL2.App.Models;

namespace XCL2.App.Views;

public partial class AiAssistantSettingsPanel
{
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private bool _autoSaving;
    private bool _autoSaveInitialized;
    private int _autoSaveSuspensions;
    private AiApiProtocol SelectedApiProtocol => Enum.TryParse<AiApiProtocol>(ApiProtocolCombo.SelectedValue as string, out var protocol)
        ? protocol : AiApiProtocol.OpenAI;

    private void EndpointProtocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _loadingEndpoint || EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint) return;
        endpoint.Protocol = Enum.TryParse<AiApiProtocol>(EndpointProtocolCombo.SelectedValue as string, out var protocol) ? protocol : AiApiProtocol.OpenAI;
    }

    private void InitializeAutoSave()
    {
        _autoSaveInitialized = true;
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); TryAutoSave(); };
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleAutoSave()), true);
        AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((_, _) => ScheduleAutoSave()), true);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => ScheduleAutoSave()), true);
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, AutoSaveSettingsCheck)) PersistAutoSavePreference();
            ScheduleAutoSave();
        }), true);
        Unloaded += (_, _) => _autoSaveTimer.Stop();
    }

    private void ScheduleAutoSave()
    {
        if (!_autoSaveInitialized || _loading || _autoSaving || _autoSaveSuspensions > 0 || _closeApproved || !IsLoaded) return;
        _autoSaveTimer.Stop();
        if (AutoSaveSettingsCheck.IsChecked == true)
        {
            AutoSaveStatusText.Text = "等待保存…";
            _autoSaveTimer.Start();
        }
        else AutoSaveStatusText.Text = "自动保存已关闭，请使用底部的保存按钮。";
    }

    private IDisposable SuspendAutoSave()
    {
        _autoSaveTimer.Stop();
        _autoSaveSuspensions++;
        return new AutoSavePause(this);
    }

    private sealed class AutoSavePause : IDisposable
    {
        private AiAssistantSettingsPanel? _panel;
        internal AutoSavePause(AiAssistantSettingsPanel panel) => _panel = panel;
        public void Dispose()
        {
            var panel = _panel;
            _panel = null;
            if (panel == null) return;
            panel._autoSaveSuspensions = Math.Max(0, panel._autoSaveSuspensions - 1);
            if (panel._autoSaveSuspensions == 0) panel.ScheduleAutoSave();
        }
    }

    private void PersistAutoSavePreference()
    {
        if (_loading || _autoSaving || _autoSaveSuspensions > 0 || _closeApproved) return;
        _autoSaveTimer.Stop();
        var previous = _currentConfig.AutoSaveSettings;
        _autoSaving = true;
        try
        {
            // Persist the toggle immediately even if another editor contains an incomplete draft.
            var live = ConfigService.Active?.Config.AiAssistant ?? _currentConfig;
            var next = JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(live)) ?? new AiAssistantConfig();
            next.AutoSaveSettings = AutoSaveSettingsCheck.IsChecked == true;
            Saved?.Invoke(this, next);
            _currentConfig = next;
        }
        catch
        {
            AutoSaveSettingsCheck.IsChecked = previous;
            AutoSaveStatusText.Text = "保存自动保存选项失败，请重试。";
        }
        finally { _autoSaving = false; }
    }

    private void TryAutoSave()
    {
        if (_loading || _autoSaving || _autoSaveSuspensions > 0 || _closeApproved || _fetchingModels || AutoSaveSettingsCheck.IsChecked != true) return;
        // Do not commit half-entered models, keys or numeric fields while the user is editing.
        if (!string.IsNullOrWhiteSpace(CustomModelIdBox.Text) || !string.IsNullOrWhiteSpace(CustomModelNameBox.Text)
            || !string.IsNullOrEmpty(DefaultExtraKeyBox.Password) || !string.IsNullOrEmpty(EndpointNewKeyBox.Password)
            || (CustomModelUseStandaloneCheck.IsChecked == true && (!string.IsNullOrWhiteSpace(CustomModelProviderBaseUrlBox.Text)
                || !string.IsNullOrEmpty(CustomModelProviderApiKeyBox.Password))) || InvalidAccountingFields().Length > 0
            || !ValidInteger(ThresholdBox.Text, 2000, int.MaxValue)
            || !ValidInteger(RequestTimeoutBox.Text, 0, 86400)
            || !ValidInteger(MaxRunMinutesBox.Text, 1, 1440)
            || !ValidInteger(LongWaitHintSecondsBox.Text, 0, 86400)
            || !ValidInteger(MaxOutputTokensBox.Text, 0, 32768)
            || !ValidInteger(MaxToolActionsBox.Text, 0, int.MaxValue)
            || !ValidInteger(MaxSubAgentsBox.Text, 0, 4)
            || !double.TryParse(TemperatureBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
            || !double.IsFinite(temperature) || temperature < 0 || temperature > 2)
        {
            AutoSaveStatusText.Text = "草稿尚未完成，请完成数值、添加模型或密钥后再保存。";
            return;
        }
        _autoSaving = true;
        try
        {
            if (string.Equals(_savedDraftFingerprint, CreateDraftFingerprint(), StringComparison.Ordinal))
            { return; }
            TrySaveChanges(closeAfterSave: false);
        }
        catch { AutoSaveStatusText.Text = "自动保存失败，草稿仍保留，请使用保存按钮重试。"; }
        finally { _autoSaving = false; }
    }

    private static bool ValidInteger(string text, int min, int max)
        => int.TryParse(text, out var value) && value >= min && value <= max;

    private Task ShowModelTestResultAsync(string message, string title, bool success)
    {
        if (Window.GetWindow(this) is { } owner && owner is not MainWindow)
        {
            OwnedMessageDialog.Show(owner, message, title, MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            return Task.CompletedTask;
        }
        return success ? MessageBoxDialog.ShowSuccessAsync(message, title) : MessageBoxDialog.ShowErrorAsync(message, title);
    }

    private async void RejectTerms_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Application.Current.MainWindow is not MainWindow launcher)
        {
            ShowSettingsWarning("暂时无法连接启动器主窗口，请重新打开 AI 设置后重试。", "关闭 AI 助手");
            return;
        }
        using var pause = SuspendAutoSave();
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            await launcher.RevokeAiTermsAsync(() =>
            {
                _closeApproved = true;
                RequestClose?.Invoke(this, false);
            }, Window.GetWindow(this));
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }
}
