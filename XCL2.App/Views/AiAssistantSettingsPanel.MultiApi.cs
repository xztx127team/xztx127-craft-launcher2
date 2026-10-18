using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantSettingsPanel
{
    private readonly ObservableCollection<AiApiEndpoint> _endpoints = new();
    private readonly List<string> _defaultExtraKeys = new();
    private readonly HashSet<string> _resetUsageModelKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingEndpoint;
    private bool _fetchingModels;
    private bool _syncingEndpointSelection;
    private AiApiEndpoint? _editingEndpoint;
    private CancellationTokenSource? _modelFetchCancellation;

    // Never bind real key strings into a visible list or tooltip.
    private sealed record MaskedKey(int Index, string Label);

    private void LoadMultiApiSettings(AiAssistantConfig config)
    {
        _modelFetchCancellation?.Cancel();
        Unloaded -= ModelFetch_Unloaded;
        Unloaded += ModelFetch_Unloaded;
        _editingEndpoint = null;
        MixBuiltInApiCheck.IsChecked = config.MixBuiltInApi;
        ApiKeyFailoverCheck.IsChecked = config.EnableApiKeyFailover;
        EndpointFailoverCheck.IsChecked = config.EnableEndpointFailover;
        DefaultExtraKeyBox.Clear();
        _defaultExtraKeys.Clear();
        _defaultExtraKeys.AddRange(AiEndpointRouting.CleanKeys(config.ApiKeys));
        _endpoints.Clear();
        foreach (var endpoint in config.ApiEndpoints ?? new())
            if (endpoint != null) _endpoints.Add(endpoint.Clone());
        EndpointsCombo.ItemsSource = _endpoints;
        EndpointsCombo.SelectedItem = _endpoints.FirstOrDefault();
        ShowEndpointEditor();
        RefreshEndpointChoices(_editingEndpoint?.Id);
        RefreshMaskedKeys();
        _resetUsageModelKeys.Clear();
    }

    private void ModelFetch_Unloaded(object sender, RoutedEventArgs e) => _modelFetchCancellation?.Cancel();

    private void RefreshEndpointChoices(string? preferredId = null)
    {
        if (ModelEndpointCombo == null) return;
        var selected = preferredId ?? ModelEndpointCombo.SelectedValue as string;
        var choices = new List<AiApiEndpoint> { new() { Id = "custom", Name = "公共自定义接口", Enabled = true } };
        choices.AddRange(_endpoints.Where(e => e.Enabled).Select(e => e.Clone()));
        _syncingEndpointSelection = true;
        try
        {
            ModelEndpointCombo.ItemsSource = choices;
            ModelEndpointCombo.SelectedValue = choices.Any(e => string.Equals(e.Id, selected, StringComparison.OrdinalIgnoreCase))
                ? choices.First(e => string.Equals(e.Id, selected, StringComparison.OrdinalIgnoreCase)).Id : choices[0].Id;
        }
        finally { _syncingEndpointSelection = false; }
    }

    private void RefreshMaskedKeys()
    {
        DefaultKeysList.ItemsSource = _defaultExtraKeys.Select((_, i) => new MaskedKey(i, $"备用密钥 {i + 1} · ••••••••")).ToList();
        var endpoint = EndpointsCombo.SelectedItem as AiApiEndpoint;
        EndpointKeysList.ItemsSource = (endpoint?.ApiKeys ?? new()).Select((_, i) => new MaskedKey(i, $"密钥 {i + 1} · ••••••••")).ToList();
    }

    private void AddDefaultKey_Click(object sender, RoutedEventArgs e)
    {
        var keys = AiEndpointRouting.CleanKeys(new[] { DefaultExtraKeyBox.Password });
        if (keys.Count == 0) return;
        foreach (var key in keys)
            if (!_defaultExtraKeys.Contains(key, StringComparer.Ordinal)) _defaultExtraKeys.Add(key);
        DefaultExtraKeyBox.Clear();
        RefreshMaskedKeys();
    }

    private void RemoveDefaultKey_Click(object sender, RoutedEventArgs e)
    {
        if (DefaultKeysList.SelectedItem is not MaskedKey row) return;
        if (row.Index >= 0 && row.Index < _defaultExtraKeys.Count) _defaultExtraKeys.RemoveAt(row.Index);
        RefreshMaskedKeys();
    }

    private void AddEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = new AiApiEndpoint { Id = Guid.NewGuid().ToString("N"), Name = $"接口 {_endpoints.Count + 2}", Enabled = true };
        _endpoints.Add(endpoint);
        EndpointsCombo.SelectedItem = endpoint;
        RefreshEndpointChoices(endpoint.Id);
        EndpointNameBox.Focus();
        EndpointNameBox.SelectAll();
    }

    private void RemoveEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint) return;
        var linked = _customModels.Where(m => string.Equals(m.ProviderId, endpoint.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (linked.Count > 0 && !MessageBoxDialog.ShowConfirm($"删除“{endpoint.Name}”及其 {linked.Count} 个模型？本次编辑可用“取消”放弃。", "删除接口")) return;
        foreach (var model in linked) _customModels.Remove(model);
        _endpoints.Remove(endpoint);
        EndpointsCombo.SelectedItem = _endpoints.FirstOrDefault();
        ShowEndpointEditor();
        RefreshEndpointChoices(_editingEndpoint?.Id ?? "custom");
        RefreshCatalogPreservingSelection();
    }

    private void EndpointsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowEndpointEditor();
        if (!_loading && !_syncingEndpointSelection)
        {
            CustomModelUseStandaloneCheck.IsChecked = false;
            RefreshEndpointChoices(_editingEndpoint?.Id ?? "custom");
        }
    }

    private void ModelEndpointCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingEndpointSelection) return;
        var selectedId = ModelEndpointCombo.SelectedValue as string;
        if (!string.IsNullOrWhiteSpace(selectedId)) CustomModelUseStandaloneCheck.IsChecked = false;
        _syncingEndpointSelection = true;
        try
        {
            EndpointsCombo.SelectedItem = _endpoints.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncingEndpointSelection = false; }
    }

    private void ShowEndpointEditor()
    {
        if (EndpointEditor == null) return;
        // Keep a typed key with the endpoint it belongs to before replacing the editor.
        // Clearing this PasswordBox on selection used to silently discard unapplied keys.
        if (!_loading) CommitPendingEndpointKey(_editingEndpoint);
        _loadingEndpoint = true;
        try
        {
            var endpoint = EndpointsCombo.SelectedItem as AiApiEndpoint;
            _editingEndpoint = endpoint;
            EndpointEditor.IsEnabled = endpoint != null;
            EndpointNameBox.Text = endpoint?.Name ?? "";
            EndpointUrlBox.Text = endpoint?.BaseUrl ?? "";
            EndpointEnabledCheck.IsChecked = endpoint?.Enabled == true;
            EndpointNewKeyBox.Clear();
            RefreshMaskedKeys();
        }
        finally { _loadingEndpoint = false; }
    }

    private void EndpointFields_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || _loadingEndpoint || EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint) return;
        endpoint.Name = EndpointNameBox.Text.Trim();
        endpoint.BaseUrl = EndpointUrlBox.Text.Trim();
    }

    private void EndpointName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _loadingEndpoint) return;
        EndpointsCombo.Items.Refresh();
        RefreshEndpointChoices();
        RefreshCatalogPreservingSelection();
    }

    private void EndpointEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _loadingEndpoint || EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint) return;
        endpoint.Enabled = EndpointEnabledCheck.IsChecked == true;
        RefreshEndpointChoices(endpoint.Enabled ? endpoint.Id : "custom");
        RefreshCatalogPreservingSelection();
    }

    private void AddEndpointKey_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEndpointKey(_editingEndpoint);
        RefreshMaskedKeys();
    }

    private void CommitPendingEndpointKey(AiApiEndpoint? endpoint)
    {
        if (endpoint == null || !_endpoints.Contains(endpoint)) return;
        var keys = AiEndpointRouting.CleanKeys(new[] { EndpointNewKeyBox.Password });
        if (keys.Count == 0) return;
        endpoint.ApiKeys ??= new();
        foreach (var key in keys)
            if (!endpoint.ApiKeys.Contains(key, StringComparer.Ordinal)) endpoint.ApiKeys.Add(key);
        EndpointNewKeyBox.Clear();
    }

    private void RemoveEndpointKey_Click(object sender, RoutedEventArgs e)
    {
        if (EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint || EndpointKeysList.SelectedItem is not MaskedKey row) return;
        if (row.Index >= 0 && row.Index < endpoint.ApiKeys.Count) endpoint.ApiKeys.RemoveAt(row.Index);
        RefreshMaskedKeys();
    }

    private void CommitPendingKeys()
    {
        AddDefaultKey_Click(this, new RoutedEventArgs());
        AddEndpointKey_Click(this, new RoutedEventArgs());
    }

    private List<AiApiEndpoint> FetchableEndpoints()
    {
        var list = _endpoints.Where(e => e.Enabled).Select(e => e.Clone()).ToList();
        // An empty legacy public interface should not prevent named-only configurations.
        var defaultKeys = AiEndpointRouting.CleanKeys(new[] { ApiKeyBox.Password }.Concat(_defaultExtraKeys)).ToList();
        if (defaultKeys.Count > 0 || list.Count == 0 || _customModels.Any(m => AiModelIds.GetProviderId(m) == "custom"))
            list.Insert(0, new AiApiEndpoint { Id = "custom", Name = "公共自定义接口", BaseUrl = BaseUrlBox.Text.Trim(), ApiKeys = defaultKeys, Enabled = true });
        return list;
    }

    private bool ValidateApiConfiguration()
    {
        CommitPendingKeys();
        foreach (var endpoint in _endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint.Id) || endpoint.Id is "custom" or "builtin")
            {
                ShowSettingsWarning("命名接口的标识无效，请删除后重新添加该接口。", "AI 设置");
                return false;
            }
            if (endpoint.Enabled && (!TryNormalizeBaseUrl(endpoint.BaseUrl, out _) || string.IsNullOrWhiteSpace(endpoint.Name)))
            {
                ShowSettingsWarning($"接口“{endpoint.Name}”需要名称和有效的 http/https Base URL。", "AI 设置");
                return false;
            }
        }
        if (_endpoints.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            ShowSettingsWarning("接口标识重复，请删除重复接口后重新添加。", "AI 设置");
            return false;
        }
        var usesCommon = _customModels.Any(m => AiModelIds.GetProviderId(m) == "custom");
        if (usesCommon && !TryNormalizeBaseUrl(BaseUrlBox.Text, out _))
        {
            ShowSettingsWarning("公共自定义接口地址无效，请填写有效的 http/https Base URL。", "AI 设置");
            return false;
        }
        if (TryNormalizeBaseUrl(BaseUrlBox.Text, out var baseUrl)) BaseUrlBox.Text = baseUrl;
        foreach (var endpoint in _endpoints.Where(e => e.Enabled))
            if (TryNormalizeBaseUrl(endpoint.BaseUrl, out var normalized)) endpoint.BaseUrl = normalized;
        return true;
    }

    private async Task FetchEndpointModelsAsync(bool allEndpoints)
    {
        if (_fetchingModels) return;
        CommitPendingKeys();
        var endpoints = FetchableEndpoints();
        if (!allEndpoints)
        {
            var selectedId = ModelEndpointCombo.SelectedValue as string ?? "custom";
            if (selectedId == "custom" && endpoints.All(e => e.Id != "custom"))
                endpoints.Insert(0, new AiApiEndpoint { Id = "custom", Name = "公共自定义接口", BaseUrl = BaseUrlBox.Text.Trim(), ApiKeys = new(), Enabled = true });
            endpoints = endpoints.Where(e => string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        await FetchModelTargetsAsync(endpoints);
    }

    private async void FetchEditedEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (_fetchingModels) return;
        CommitPendingKeys();
        if (_editingEndpoint == null) { ToastService.ShowWarning("请先选择一个命名接口。"); return; }
        RefreshEndpointChoices(_editingEndpoint.Id);
        await FetchModelTargetsAsync(new() { _editingEndpoint.Clone() });
    }

    private async void FetchStandaloneModels_Click(object sender, RoutedEventArgs e)
    {
        if (_fetchingModels) return;
        var legacyUrl = CustomModelProviderBaseUrlBox.Text.Trim();
        if (!TryNormalizeBaseUrl(legacyUrl, out var baseUrl))
        { ToastService.ShowWarning("请先填写有效的独立接口地址。"); return; }
        var legacyKey = CustomModelProviderApiKeyBox.Password;
        var hasModelDraft = !string.IsNullOrWhiteSpace(CustomModelIdBox.Text)
            || !string.IsNullOrWhiteSpace(CustomModelNameBox.Text);
        var legacy = new AiModelDefinition { Id = "endpoint", ProviderBaseUrl = baseUrl };
        var success = await FetchModelTargetsAsync(new() { new AiApiEndpoint
        {
            Id = AiModelIds.GetProviderId(legacy), Name = "独立接口", BaseUrl = baseUrl,
            ApiKeys = AiEndpointRouting.CleanKeys(new[] { legacyKey }).ToList(), Enabled = true
        } }, standaloneTargets: true);
        if (success && !hasModelDraft && string.IsNullOrWhiteSpace(CustomModelIdBox.Text)
            && string.IsNullOrWhiteSpace(CustomModelNameBox.Text)
            && CustomModelProviderBaseUrlBox.Text.Trim() == legacyUrl && CustomModelProviderApiKeyBox.Password == legacyKey)
        {
            CustomModelProviderBaseUrlBox.Clear();
            CustomModelProviderApiKeyBox.Clear();
            CustomModelUseStandaloneCheck.IsChecked = false;
        }
    }

    private async Task<bool> FetchModelTargetsAsync(List<AiApiEndpoint> endpoints, bool standaloneTargets = false)
    {
        if (_fetchingModels) return false;
        if (endpoints.Count == 0) { ToastService.ShowWarning("请先选择或启用一个接口。"); return false; }
        _fetchingModels = true;
        using var cancellation = new CancellationTokenSource();
        _modelFetchCancellation = cancellation;
        FetchModelsButton.IsEnabled = FetchAllModelsButton.IsEnabled =
            FetchEditedEndpointButton.IsEnabled = FetchStandaloneModelsButton.IsEnabled = false;
        var report = new List<string>();
        int totalAdded = 0, failures = 0;
        try
        {
            foreach (var endpoint in endpoints)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                FetchModelsStatusText.Text = $"正在拉取：{endpoint.Name}…";
                if (!TryNormalizeBaseUrl(endpoint.BaseUrl, out var baseUrl))
                { report.Add($"{endpoint.Name}：接口地址无效"); failures++; continue; }
                var keys = AiEndpointRouting.CleanKeys(endpoint.ApiKeys).ToList();
                if (keys.Count == 0) keys.Add(""); // Local compatible servers may have no authentication.
                var requestEndpoint = endpoint.Clone();
                requestEndpoint.BaseUrl = baseUrl;
                requestEndpoint.ApiKeys = keys;
                var result = await AiModelListFetcher.FetchAsync(requestEndpoint, ApiKeyFailoverCheck.IsChecked == true, cancellation.Token);
                // An unloaded or reset panel must never receive a completed request's old draft.
                cancellation.Token.ThrowIfCancellationRequested();
                if (result?.Success != true)
                {
                    var error = result?.Error ?? "未返回结果";
                    foreach (var key in keys.Where(k => k.Length > 0)) error = error.Replace(key, "[密钥已隐藏]", StringComparison.Ordinal);
                    report.Add($"{endpoint.Name}：失败，{error}"); failures++; continue;
                }
                if (!IsCurrentFetchTarget(requestEndpoint, standaloneTargets, out var providerName))
                {
                    report.Add($"{endpoint.Name}：拉取期间地址、密钥或启用状态已变化，请重新拉取");
                    failures++;
                    continue;
                }
                int added = 0, existed = 0;
                foreach (var fetched in result.Models)
                {
                    if (!TryNormalizeModelId(fetched.Id, out var id, out _)) continue;
                    var model = fetched.Clone();
                    model.Id = id;
                    model.ProviderId = endpoint.Id;
                    model.ProviderName = providerName;
                    if (standaloneTargets)
                    {
                        // Empty IDs retain the legacy Base URL based provider identity.
                        model.ProviderId = string.Empty;
                        model.ProviderBaseUrl = baseUrl;
                        model.ProviderApiKey = keys[Math.Clamp(result.SuccessfulKeyIndex, 0, keys.Count - 1)];
                    }
                    if (_customModels.Any(m => string.Equals(m.ModelKey, model.ModelKey, StringComparison.OrdinalIgnoreCase))) { existed++; continue; }
                    _customModels.Add(model);
                    added++;
                }
                totalAdded += added;
                report.Add($"{endpoint.Name}：新增 {added}，已有 {existed}，跳过非对话模型 {result.SkippedNonChat}");
            }
            RefreshCatalogPreservingSelection();
            var activationHint = UseCustomKeyCheck.IsChecked != true && MixBuiltInApiCheck.IsChecked != true
                ? "启用自定义 API 或混合模式并保存后可使用。" : "请保存设置后使用。";
            if (totalAdded > 0) report.Add(activationHint);
            FetchModelsStatusText.Text = string.Join("\n", report);
            if (failures > 0) ToastService.ShowWarning($"已新增 {totalAdded} 个模型；{failures} 个接口拉取失败，请查看各接口结果。{(totalAdded > 0 ? activationHint : "")}");
            else ToastService.ShowSuccess($"已完成 {endpoints.Count} 个接口拉取，新增 {totalAdded} 个模型。{activationHint}");
            return failures == 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_modelFetchCancellation, cancellation)) _modelFetchCancellation = null;
            _fetchingModels = false;
            FetchModelsButton.IsEnabled = FetchAllModelsButton.IsEnabled =
                FetchEditedEndpointButton.IsEnabled = FetchStandaloneModelsButton.IsEnabled = true;
        }
    }

    private bool IsCurrentFetchTarget(AiApiEndpoint request, bool standalone, out string providerName)
    {
        string currentUrl;
        IEnumerable<string> currentKeys;
        providerName = request.Name;
        if (standalone)
        {
            currentUrl = CustomModelProviderBaseUrlBox.Text;
            currentKeys = new[] { CustomModelProviderApiKeyBox.Password };
        }
        else if (request.Id == "custom")
        {
            currentUrl = BaseUrlBox.Text;
            currentKeys = new[] { ApiKeyBox.Password }.Concat(_defaultExtraKeys).Append(DefaultExtraKeyBox.Password);
        }
        else
        {
            var current = _endpoints.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Id, request.Id, StringComparison.OrdinalIgnoreCase));
            if (current?.Enabled != true) return false;
            providerName = current.Name;
            currentUrl = current.BaseUrl;
            currentKeys = current.ApiKeys ?? new();
            if (ReferenceEquals(current, _editingEndpoint)) currentKeys = currentKeys.Append(EndpointNewKeyBox.Password);
        }
        return TryNormalizeBaseUrl(currentUrl, out var normalizedUrl)
            && string.Equals(normalizedUrl, request.BaseUrl, StringComparison.Ordinal)
            && AiEndpointRouting.CleanKeys(currentKeys).SequenceEqual(AiEndpointRouting.CleanKeys(request.ApiKeys), StringComparer.Ordinal);
    }

    private void EditCustomModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AiModelDefinition model }) return;
        CustomModelIdBox.Text = model.Id;
        CustomModelNameBox.Text = model.DisplayName;
        CustomModelProviderBaseUrlBox.Text = model.ProviderBaseUrl ?? "";
        CustomModelProviderApiKeyBox.Password = model.ProviderApiKey ?? "";
        var providerId = AiModelIds.GetProviderId(model);
        ModelEndpointCombo.SelectedValue = _endpoints.Any(endpoint =>
            string.Equals(endpoint.Id, providerId, StringComparison.OrdinalIgnoreCase)) ? providerId : "custom";
        CustomModelUseStandaloneCheck.IsChecked = providerId.StartsWith("legacy-", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(model.ProviderBaseUrl);
        CustomModelTierNormalRadio.IsChecked = model.Tier == AiModelTier.Normal;
        CustomModelTierExpertRadio.IsChecked = model.Tier == AiModelTier.Expert;
    }

    private void RefreshCatalogPreservingSelection()
    {
        if (_loading) return;
        var normal = NormalModelCombo.SelectedValue as string;
        var expert = ExpertModelCombo.SelectedValue as string;
        var manual = ManualModelCombo.SelectedValue as string;
        SaveAccountingModel();
        _loading = true;
        try { RefreshModelCombos(normal, expert, manual); }
        finally { _loading = false; }
    }

    private static long? ReadQuota(string? text) => long.TryParse(text, out var value) && value >= 0 ? value : null;

    private void ResetModelUsage_Click(object sender, RoutedEventArgs e)
    {
        if (_accountingModel == null) return;
        SaveAccountingModel();
        var accounting = _accounting[_accountingModel];
        accounting.UsedTokens = 0;
        accounting.SpentUsd = 0;
        accounting.UsedRequests = 0;
        accounting.UsageIncludesEstimates = false;
        _resetUsageModelKeys.Add(_accountingModel);
        ShowAccountingModel();
        ToastService.ShowSuccess("本模型累计用量将在保存设置后重置。");
    }

    private void ModelFastSupported_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _refreshingAccounting || AccountingModelCombo.SelectedItem is not AiModelDefinition selected) return;
        var model = _customModels.FirstOrDefault(m => m.ModelKey == selected.ModelKey);
        if (model == null) return;
        model.SupportsFastMode = ModelFastSupportedCheck.IsChecked == true;
        RefreshCatalogPreservingSelection();
    }

    private void SelectReasoningLevel(AiReasoningLevel level)
    {
        ReasoningLevelCombo.SelectedItem = ReasoningLevelCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, level.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? ReasoningLevelCombo.Items[0];
    }

    private AiReasoningLevel GetReasoningLevel()
    {
        if (ReasoningLevelCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<AiReasoningLevel>(tag, out var level))
            return level == AiReasoningLevel.Fast && FastReasoningItem.IsEnabled != true ? AiReasoningLevel.Off : level;
        return AiReasoningLevel.Off;
    }

    private void RefreshReasoningAvailability()
    {
        if (FastReasoningItem == null) return;
        IEnumerable<AiModelDefinition?> selected = GetRoutingMode() switch
        {
            AiRoutingMode.Normal => new[] { NormalModelCombo.SelectedItem as AiModelDefinition },
            AiRoutingMode.Expert => new[] { ExpertModelCombo.SelectedItem as AiModelDefinition },
            AiRoutingMode.SpecificModel => new[] { ManualModelCombo.SelectedItem as AiModelDefinition },
            _ => new[] { NormalModelCombo.SelectedItem as AiModelDefinition, ExpertModelCombo.SelectedItem as AiModelDefinition }
        };
        var available = selected.All(m => m?.SupportsFastMode == true);
        FastReasoningItem.IsEnabled = available;
        FastReasoningHintText.Text = available ? "当前路由所选模型已标记为支持极速。"
            : "极速仅在当前路由所选模型均明确支持时可选；可在每模型设置中确认供应商能力。";
        if (!available && ReferenceEquals(ReasoningLevelCombo.SelectedItem, FastReasoningItem))
        {
            SelectReasoningLevel(AiReasoningLevel.Off);
            ToastService.ShowWarning("当前路由所选模型未全部声明支持极速，已自动改为 off 模式。");
        }
    }
}
