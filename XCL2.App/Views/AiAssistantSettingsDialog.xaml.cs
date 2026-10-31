using System;
using System.Windows;
using System.Windows.Input;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>兼容旧调用方的独立窗口包装；实际设置 UI 与主窗口 Overlay 共用 AiAssistantSettingsPanel。</summary>
public partial class AiAssistantSettingsDialog : Window
{
    public AiAssistantConfig Result { get; private set; }
    private bool _closingFromPanel;
    private bool _closeRequestPending;

    public AiAssistantSettingsDialog(AiAssistantConfig current)
    {
        Result = current;
        InitializeComponent();

        var panel = new AiAssistantSettingsPanel(current);
        PanelHost.Content = panel;
        panel.UnsavedChangesPrompt = () => OwnedMessageDialog.Show(this, "AI 助手设置尚未保存，是否保存本次修改？",
            "保存 AI 助手设置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => XclMessageResult.Yes,
            MessageBoxResult.No => XclMessageResult.No,
            _ => XclMessageResult.Cancel
        };
        panel.SettingsWarningPrompt = (message, title) => OwnedMessageDialog.Show(this, message, title,
            MessageBoxButton.OK, MessageBoxImage.Warning);
        panel.Saved += (_, config) => Result = config;
        panel.RequestClose += (_, accepted) =>
        {
            _closingFromPanel = true;
            DialogResult = accepted == true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            panel.RequestDismiss();
        };
        Closing += (_, e) =>
        {
            if (_closingFromPanel) return;
            e.Cancel = true;
            if (_closeRequestPending) return;
            _closeRequestPending = true;
            // Finish cancelling this native close before a panel decision starts a new one.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _closeRequestPending = false;
                if (IsVisible) panel.RequestDismiss();
            }));
        };
    }
}
