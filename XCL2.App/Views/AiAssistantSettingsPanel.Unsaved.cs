using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Views;

public partial class AiAssistantSettingsPanel
{
    // Keep only a digest as the baseline. Draft credentials never leave process memory.
    private string _savedDraftFingerprint = string.Empty;
    private bool _promptingToClose;
    private bool _closeApproved;
    internal Func<XclMessageResult>? UnsavedChangesPrompt { get; set; }
    internal Action<string, string>? SettingsWarningPrompt { get; set; }

    private void ShowSettingsWarning(string message, string title)
    {
        if (SettingsWarningPrompt is { } prompt) prompt(message, title);
        else MessageBoxDialog.ShowWarning(message, title);
    }

    public void RequestDismiss()
    {
        if (_promptingToClose || _closeApproved) return;
        _promptingToClose = true;
        try
        {
            if (!string.Equals(_savedDraftFingerprint, CreateDraftFingerprint(), StringComparison.Ordinal))
            {
                var choice = UnsavedChangesPrompt?.Invoke() ?? MessageBoxDialog.ShowThreeChoice(
                    "AI 助手设置尚未保存，是否保存本次修改？", "保存 AI 助手设置", "取消", "不保存", "保存");
                if (choice == XclMessageResult.Cancel) return;
                if (choice == XclMessageResult.Yes)
                {
                    TrySaveChanges(); // Failed validation or persistence keeps this same draft open.
                    return;
                }
            }
            _closeApproved = true;
            Cancelled?.Invoke(this, EventArgs.Empty);
            RequestClose?.Invoke(this, false);
        }
        finally { _promptingToClose = false; }
    }

    private bool TryCommitPendingCustomModel()
    {
        var hasModel = !string.IsNullOrWhiteSpace(CustomModelIdBox.Text)
            || !string.IsNullOrWhiteSpace(CustomModelNameBox.Text);
        var hasStandaloneDraft = CustomModelUseStandaloneCheck.IsChecked == true
            && (!string.IsNullOrWhiteSpace(CustomModelProviderBaseUrlBox.Text)
                || !string.IsNullOrWhiteSpace(CustomModelProviderApiKeyBox.Password));
        // The standalone fetch helper is not a saved model until it has an ID and a name.
        return (!hasModel && !hasStandaloneDraft) || TryAddCustomModel();
    }

    private string CreateDraftFingerprint()
    {
        // Do not move pending key fields into a collection just to inspect the draft.
        var config = CaptureUiAsConfig(commitPendingKeys: false);
        config.PanelWidth = 0;
        config.PanelWasOpen = false;
        config.LastSessionId = null;
        config.ModelAccounting = EditableAccountingSnapshot(config.ModelAccounting);
        config.ModelTierOverrides = config.ModelTierOverrides.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        config.CommandPermissions.ExactCommands = config.CommandPermissions.ExactCommands.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var hasModelDraft = !string.IsNullOrWhiteSpace(CustomModelIdBox.Text)
            || !string.IsNullOrWhiteSpace(CustomModelNameBox.Text)
            || CustomModelUseStandaloneCheck.IsChecked == true;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Config = config,
            // Preserve invalid / incomplete numeric edits that a config parser would replace by defaults.
            RawFields = new[] { ThresholdBox.Text, RequestTimeoutBox.Text, LongWaitHintSecondsBox.Text,
                LongWaitDescriptionBox.Text, TemperatureBox.Text, MaxOutputTokensBox.Text,
                MaxToolActionsBox.Text, MaxSubAgentsBox.Text, CustomSystemPromptBox.Text },
            DefaultKeyDraft = DefaultExtraKeyBox.Password,
            EndpointKeyDraft = EndpointNewKeyBox.Password,
            ModelDraft = hasModelDraft ? new
            {
                Id = CustomModelIdBox.Text, Name = CustomModelNameBox.Text,
                Endpoint = ModelEndpointCombo.SelectedValue as string,
                Standalone = CustomModelUseStandaloneCheck.IsChecked == true,
                Url = CustomModelUseStandaloneCheck.IsChecked == true ? CustomModelProviderBaseUrlBox.Text : "",
                Key = CustomModelUseStandaloneCheck.IsChecked == true ? CustomModelProviderApiKeyBox.Password : "",
                Expert = CustomModelTierExpertRadio.IsChecked == true
            } : null,
            ExactCommandDrafts = _exactCommandPermissions.Select(p => new { p.Shell, p.Command, p.Allowed }).ToArray(),
            InvalidAccountingFields = InvalidAccountingFields(),
            ResetUsage = _resetUsageModelKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray()
        });
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private Dictionary<string, AiModelAccountingConfig> EditableAccountingSnapshot(Dictionary<string, AiModelAccountingConfig> source)
    {
        // Merely opening a different model creates an accounting editor entry. Ignore empty entries,
        // legacy aliases and live token counters so navigation or background use does not look edited.
        var catalog = CurrentCatalog();
        var canonical = new Dictionary<string, AiModelAccountingConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in catalog)
            if (source.TryGetValue(model.ModelKey, out var settings) || source.TryGetValue(model.Id, out settings))
                canonical[model.ModelKey] = settings;
        foreach (var pair in source)
            if (!catalog.Any(m => string.Equals(m.ModelKey, pair.Key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.Id, pair.Key, StringComparison.OrdinalIgnoreCase)))
                canonical[pair.Key] = pair.Value;
        return canonical.Where(p => p.Value.ContextWindowTokens != 0 || p.Value.MaxOutputTokens != 0
                || p.Value.TokenQuota.HasValue || p.Value.BudgetUsd.HasValue || p.Value.InputPricePerMillion.HasValue
                || p.Value.CachedInputPricePerMillion.HasValue || p.Value.OutputPricePerMillion.HasValue)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => new AiModelAccountingConfig
            {
                ContextWindowTokens = p.Value.ContextWindowTokens, MaxOutputTokens = p.Value.MaxOutputTokens,
                TokenQuota = p.Value.TokenQuota, BudgetUsd = p.Value.BudgetUsd,
                InputPricePerMillion = p.Value.InputPricePerMillion,
                CachedInputPricePerMillion = p.Value.CachedInputPricePerMillion,
                OutputPricePerMillion = p.Value.OutputPricePerMillion
            }, StringComparer.OrdinalIgnoreCase);
    }

    private string[] InvalidAccountingFields()
    {
        var invalid = new List<string>();
        void Integer(string name, string text, long max)
        {
            if (!string.IsNullOrWhiteSpace(text)
                && (!long.TryParse(text, out var value) || value < 0 || value > max))
                invalid.Add(name + "=" + text);
        }
        void Price(string name, string text)
        {
            if (!string.IsNullOrWhiteSpace(text) && (!decimal.TryParse(text, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var value) || value < 0 || value > 1_000_000m))
                invalid.Add(name + "=" + text);
        }
        Integer("context", ContextWindowBox.Text, 10_000_000);
        Integer("output", ModelOutputTokensBox.Text, 10_000_000);
        Integer("quota", ModelTokenQuotaBox.Text, long.MaxValue);
        Price("budget", ModelBudgetBox.Text);
        Price("input", InputPriceBox.Text);
        Price("cached", CachedInputPriceBox.Text);
        Price("output", OutputPriceBox.Text);
        if (invalid.Count > 0) invalid.Insert(0, _accountingModel ?? "");
        return invalid.ToArray();
    }
}
