using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    private void UpdateTokenBadge()
    {
        AiRequestUsage[] requests;
        _session.UsageRequests ??= new();
        lock (_session.UsageRequests) requests = _session.UsageRequests.Select(x => x.Clone()).ToArray();
        long total = requests.Sum(x => x.TotalTokens);
        bool estimated = requests.Any(x => x.IsEstimated);
        var current = requests.LastOrDefault(x => x.AffectsContext && x.TotalTokens > 0);
        TokenUsageText.Text = requests.Length == 0 ? "本对话 · 暂无 API 用量" : $"本对话 · {(estimated ? "约 " : "")}{total:N0} token";
        CacheUsageText.Text = requests.Length == 0 ? "当前缓存命中率：发送请求后显示" : current?.CachedInputTokens is long cached
            ? current.HasReportedInput && current.InputTokens > 0
                ? $"当前缓存命中率：{(double)cached / current.InputTokens:P1}（{cached:N0} / {current.InputTokens:N0} 输入 token）"
                : $"缓存命中 {cached:N0} token（等待接口返回有效输入总量后计算命中率）"
            : "当前缓存命中率：供应商未返回缓存数据";
        var priced = requests.Where(x => x.EstimatedCostUsd.HasValue).ToArray();
        string cost = requests.Length == 0 ? "暂无 API 请求" : priced.Length == 0 ? "未配置模型单价"
            : $"${priced.Sum(x => x.EstimatedCostUsd!.Value):0.######} USD" + (priced.Length < requests.Length ? " + 未知部分" : "");
        CostUsageText.Text = "本对话估计成本：" + cost;
        TotalUsageText.Text = $"累计输入 {requests.Sum(x => x.InputTokens):N0} · 输出 {requests.Sum(x => x.OutputTokens):N0} · 合计 {total:N0} token";
        var currentModelKey = current == null ? _config.RoutingMode switch
        {
            AiRoutingMode.SpecificModel => _config.SelectedModel,
            AiRoutingMode.Expert => _config.ExpertModelId,
            _ => _config.NormalModelId
        } : string.IsNullOrWhiteSpace(current.ModelKey) ? current.Model : current.ModelKey;
        var settings = AiUsageAccounting.Settings(_config, currentModelKey);
        QuotaUsageText.Text = "模型本地配额：" + (settings.TokenQuota is long quota
            ? $"{settings.UsedTokens:N0} / {quota:N0} token，剩余 {Math.Max(0, quota - settings.UsedTokens):N0}"
            : "未设置 Token 配额") + "\n美元预算：" + (settings.BudgetUsd is decimal budget
            ? $"${settings.SpentUsd:0.######} / ${budget:0.######} USD，剩余 ${Math.Max(0, budget - settings.SpentUsd):0.######}"
            : "未设置") + (settings.MaxOutputTokens > 0 ? $"\n模型输出上限：{settings.MaxOutputTokens:N0} token" : "");
        var configuredLimit = settings.ContextWindowTokens;
        var contextLimit = Math.Clamp(configuredLimit > 0 ? configuredLimit : current?.ContextWindowTokens ?? 0, 0, 10_000_000);
        var context = AiUsageAccounting.ContextOccupancy(current, contextLimit);
        long occupied = context.Used;
        double proportion = context.Proportion ?? -1;
        DrawUsageRing(proportion);
        ContextUsageText.Text = current == null ? (contextLimit > 0 ? $"上下文上限：{contextLimit:N0} token；发送请求后显示占用。" : "当前上下文：发送请求后显示；模型上限未设置。")
            : contextLimit <= 0 ? $"当前上下文约 {occupied:N0} token；请为该模型设置上限以显示占用比例。"
            : $"当前上下文{(current.IsEstimated ? "约 " : "")}{occupied:N0} / {contextLimit:N0}，剩余 {Math.Max(0, contextLimit - occupied):N0} token"
                + (current.InProgress && current.ReservedOutputTokens > 0 ? $"\n输出预留上限：{current.ReservedOutputTokens:N0} token" : "");
        UsageSummaryText.Text = current == null ? "点击查看用量；可在设置中填写模型上限与单价"
            : (proportion >= 0 ? $"上下文占用 {proportion:P0} · " : "上下文上限未设置 · ") + cost;
        UsageIndicatorButton.SetValue(System.Windows.Automation.AutomationProperties.HelpTextProperty, UsageSummaryText.Text);
        UsageSourceText.Text = (current == null ? "参考模型：" + ModelDisplayName(currentModelKey) + "\n" : "当前模型："
            + (string.IsNullOrWhiteSpace(current.ProviderName) ? ModelDisplayName(currentModelKey) : current.ProviderName + " · " + current.Model) + "\n")
            + (requests.Length == 0 ? "暂无 API 用量。" : estimated ? "包含本地估算；供应商未返回完整 usage 或请求中断时，token 与费用可能有偏差。" : "token 采用供应商返回的 usage。")
            + " 费用按你设置的 USD 单价估算；未返回缓存字段或未设置缓存价时按普通输入价计。"
            + (requests.Any(x => x.IsIncomplete) ? " 包含失败或中断请求，可能无法获得完整 usage。" : "")
            + " 限额是本地设置，并非服务商账户余额。累计包含本对话的工具续答、压缩、子任务；不含预热、连接测试及此功能启用前的历史请求。圆环只计算当前主请求。";
    }

    private void DrawUsageRing(double proportion)
    {
        UsageRingLabel.Text = proportion < 0 ? "—" : $"{proportion:P0}";
        UsageRemainingRing.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            proportion >= 0.9 ? "DangerBrush" : "AccentBrush");
        if (proportion <= 0) { UsageRemainingRing.Data = Geometry.Empty; return; }
        if (proportion >= 0.9999) { UsageRemainingRing.Data = new EllipseGeometry(new Point(20, 20), 17, 17); return; }
        double angle = proportion * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(20, 3), false, false);
            ctx.ArcTo(new Point(20 + Math.Sin(angle) * 17, 20 - Math.Cos(angle) * 17),
                new Size(17, 17), 0, proportion > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        UsageRemainingRing.Data = geometry;
    }

    private bool _usagePopupPinned;
    private DispatcherTimer? _usageCloseTimer;

    private void EnsureUsagePopupTimer()
    {
        if (_usageCloseTimer != null) return;
        _usageCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _usageCloseTimer.Tick += (_, _) =>
        {
            _usageCloseTimer.Stop();
            if (!_usagePopupPinned && !UsageIndicatorButton.IsMouseOver && !UsagePopupContent.IsMouseOver)
                UsagePopup.IsOpen = false;
        };
        Unloaded += (_, _) => { UsagePopup.IsOpen = false; _usageCloseTimer.Stop(); };
    }

    private void UsageIndicator_MouseEnter(object sender, MouseEventArgs e)
    {
        EnsureUsagePopupTimer();
        _usageCloseTimer!.Stop();
        UpdateTokenBadge();
        if (!_usagePopupPinned) UsagePopup.StaysOpen = true;
        UsagePopup.IsOpen = true;
    }

    private void UsagePopup_MouseEnter(object sender, MouseEventArgs e) => _usageCloseTimer?.Stop();
    private void UsageIndicator_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_usagePopupPinned) { EnsureUsagePopupTimer(); _usageCloseTimer!.Start(); }
    }

    private void UsageIndicatorButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureUsagePopupTimer();
        _usageCloseTimer!.Stop();
        if (_usagePopupPinned) { UsagePopup.IsOpen = false; return; }
        UpdateTokenBadge();
        _usagePopupPinned = true;
        UsagePopup.StaysOpen = false;
        UsagePopup.IsOpen = true;
    }

    private void UsagePopup_Closed(object sender, EventArgs e)
    {
        _usagePopupPinned = false;
        _usageCloseTimer?.Stop();
    }

    private void UsageIndicator_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        UsagePopup.IsOpen = false;
        e.Handled = true;
    }

    private void UsageSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UsagePopup.IsOpen = false;
        SettingsButton_Click(sender, e);
    }
}
