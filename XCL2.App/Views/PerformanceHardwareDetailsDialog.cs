using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>A detached draft: closing or cancelling never mutates persisted overlay preferences.</summary>
public sealed class PerformanceHardwareDetailsDialog : Window
{
    private readonly Dictionary<string, PerformanceHardwareDetailOptions> _draft;
    private readonly Action<Action<AppConfig>> _changeSettings;
    private readonly TabControl _tabs = new();
    private readonly Dictionary<(string Kind, string Field), TextBlock> _automaticLabels = new();
    private readonly Dictionary<(string Kind, string Field), (CheckBox Show, TextBox Value)> _editors = new();
    private readonly Dictionary<string, CheckBox> _kindSwitches = new();
    private readonly TextBlock _sampleStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly bool _usesTemplate;
    private readonly bool _templateChangesUsePageSave;
    private string _templateDraft;
    private readonly HashSet<string> _requestedTemplateKinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _templateSuggestion = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _templateInsert = new() { Content = "补充所选硬件字段", Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBox _templateDraftText = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Expander _templatePreview = new() { Header = "查看加入后的模板草稿", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };

    public PerformanceHardwareDetailsDialog(AppConfig cfg, Action<Action<AppConfig>> changeSettings,
        bool templateChangesUsePageSave = false)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(changeSettings);
        _draft = PerformanceHardwareDetails.Clone(cfg);
        _changeSettings = changeSettings;
        _templateChangesUsePageSave = templateChangesUsePageSave;
        _usesTemplate = cfg.PerformanceOverlayLayoutMode == "Template";
        _templateDraft = cfg.PerformanceOverlayTextTemplate ?? "";
        Title = "性能悬浮窗 · 硬件资料";
        Width = Math.Min(760, SystemParameters.WorkArea.Width);
        Height = Math.Min(680, SystemParameters.WorkArea.Height);
        MinWidth = 360; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");

        var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        // A short/narrow display must still allow access to the template hint, fields and Save buttons.
        // Give the tab body a usable viewport, and let the whole form scroll only when it cannot fit.
        var viewport = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _tabs.Height = 340;
        viewport.SizeChanged += (_, e) => _tabs.Height = Math.Clamp(e.NewSize.Height - 300, 240, 420);
        Content = viewport;
        var hint = new TextBlock
        {
            Text = "按硬件选择要显示的资料。留空使用自动识别，填写后以“手动”标注；这里仅设置展示内容，不修改硬件参数。\n" +
                "同类设备按汇总项设置。默认/基准与标准/额定速率可填写规格值；内存速度使用 MT/s，网卡链路速度使用 bit/s，与实时传输量分开。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var templatePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(templatePanel, Dock.Top); root.Children.Add(templatePanel);
        var templateAction = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_templateInsert, Dock.Right); templateAction.Children.Add(_templateInsert);
        templateAction.Children.Add(_templateSuggestion); templatePanel.Children.Add(templateAction);
        _templateSuggestion.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _templatePreview.Content = _templateDraftText; templatePanel.Children.Add(_templatePreview);
        AutomationProperties.SetName(_templateDraftText, "硬件字段加入后的模板草稿，只读");
        _templateInsert.Click += (_, _) => InsertTemplateFields();
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var sourceHint = new TextBlock
        {
            Text = "自动数据约每 30 秒更新：内存来自固件，GPU/NPU 厂商来自驱动，网卡为系统报告的链路速度。不能确定的项目显示“暂不可用”；内存条数量不会被当作通道数。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        sourceHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); footer.Children.Add(sourceHint);
        _sampleStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); footer.Children.Add(_sampleStatus);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        AddButton(actions, "清除本页自定义", ClearCurrentOverrides);
        AddButton(actions, "恢复本页默认", ResetCurrent);
        AddButton(actions, "取消", () => DialogResult = false, cancel: true);
        AddButton(actions, "保存", Save, primary: true);
        root.Children.Add(_tabs);
        foreach (var kind in PerformanceHardwareDetails.Kinds) BuildTab(kind.Key, kind.Label);
        RefreshTemplateSuggestion();
        RefreshAutomatic(PerformanceOverlayService.LatestSnapshot);
        PerformanceOverlayService.SnapshotUpdated += SnapshotUpdated;
        Closed += (_, _) => PerformanceOverlayService.SnapshotUpdated -= SnapshotUpdated;
    }

    private void BuildTab(string kind, string label)
    {
        var body = new StackPanel { Margin = new Thickness(12) };
        var show = new CheckBox { Content = "在悬浮窗显示" + label + "资料", IsChecked = _draft[kind].Show,
            Margin = new Thickness(0, 0, 0, 12) };
        _kindSwitches[kind] = show;
        show.Checked += (_, _) => RefreshTemplateSuggestion();
        show.Unchecked += (_, _) => RefreshTemplateSuggestion();
        body.Children.Add(show);
        foreach (var field in PerformanceHardwareDetails.Fields)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var fieldLabel = field.Key == "Speed" && kind == "Network" ? "链路速度" : field.Key == "Speed" && kind == "Memory" ? "配置速度" : field.Label;
            var selected = new CheckBox { Content = fieldLabel, IsChecked = PerformanceHardwareDetails.Selected(_draft[kind], field.Key),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 8, 0) };
            row.Children.Add(selected);
            var values = new StackPanel(); Grid.SetColumn(values, 1); row.Children.Add(values);
            var text = new TextBox { Text = PerformanceHardwareDetails.ManualValue(_draft[kind], field.Key),
                MaxLength = PerformanceHardwareDetails.MaximumValueLength, MinHeight = 30,
                ToolTip = "留空：自动识别；填写：手动资料（请包含 MHz、MT/s、Gbit/s 或 GiB 等单位）" };
            AutomationProperties.SetName(text, label + " " + fieldLabel + " 自定义值");
            values.Children.Add(text);
            var automatic = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0), FontSize = 11 };
            automatic.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            values.Children.Add(automatic);
            _automaticLabels[(kind, field.Key)] = automatic;
            _editors[(kind, field.Key)] = (selected, text);
            selected.Checked += (_, _) => RefreshTemplateSuggestion();
            selected.Unchecked += (_, _) => RefreshTemplateSuggestion();
            body.Children.Add(row);
        }
        _tabs.Items.Add(new TabItem { Header = label, Tag = kind, Content = new ScrollViewer
        { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
    }

    private void SnapshotUpdated(PerformanceSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => RefreshAutomatic(snapshot))); return; }
        RefreshAutomatic(snapshot);
    }
    private void RefreshAutomatic(PerformanceSnapshot? snapshot)
    {
        if (!PerformanceOverlayService.IsPolling)
        {
            _sampleStatus.Text = "性能监测已暂停；开启悬浮窗后读取自动资料。自定义资料仍可编辑和保存。";
            snapshot = null;
        }
        else if (snapshot == null || DateTimeOffset.UtcNow - snapshot.SampledAt > TimeSpan.FromSeconds(45))
        {
            _sampleStatus.Text = "正在等待有效采样…";
            snapshot = null;
        }
        else _sampleStatus.Text = $"最近性能采样：{snapshot.SampledAt.LocalDateTime:HH:mm:ss}（硬件资料最多缓存 30 秒）";
        foreach (var pair in _automaticLabels)
        {
            var value = PerformanceHardwareDetails.AutomaticValue(snapshot?.Hardware, pair.Key.Kind, pair.Key.Field);
            pair.Value.Text = "自动识别：" + (value.Length > 0 ? value : "暂不可用，可手动填写");
        }
    }
    private string? CurrentKind => (_tabs.SelectedItem as TabItem)?.Tag as string;
    private void ClearCurrentOverrides()
    {
        if (CurrentKind is not string kind) return;
        foreach (var field in PerformanceHardwareDetails.Fields) _editors[(kind, field.Key)].Value.Text = "";
    }
    private void ResetCurrent()
    {
        if (CurrentKind is not string kind) return;
        var defaults = new PerformanceHardwareDetailOptions();
        _kindSwitches[kind].IsChecked = defaults.Show;
        foreach (var field in PerformanceHardwareDetails.Fields)
        {
            var editor = _editors[(kind, field.Key)];
            editor.Show.IsChecked = PerformanceHardwareDetails.Selected(defaults, field.Key);
            editor.Value.Text = "";
        }
    }
    private void Save()
    {
        foreach (var kind in PerformanceHardwareDetails.Kinds)
        {
            var value = _draft[kind.Key];
            value.Show = _kindSwitches[kind.Key].IsChecked == true;
            foreach (var field in PerformanceHardwareDetails.Fields)
            {
                var editor = _editors[(kind.Key, field.Key)];
                PerformanceHardwareDetails.Set(value, field.Key, editor.Show.IsChecked == true, editor.Value.Text);
            }
        }
        // One settings transaction. Template additions are opt-in and are merged into the latest text.
        // The detached draft and requested additions are discarded together on Cancel/close.
        var saved = _draft.ToDictionary(pair => pair.Key, pair => PerformanceHardwareDetails.Copy(pair.Value));
        string? templateWarning = null;
        _changeSettings(cfg =>
        {
            var template = cfg.PerformanceOverlayTextTemplate;
            if (_requestedTemplateKinds.Count > 0 &&
                !PerformanceHardwareDetails.TryAppendTemplateFields(template, _requestedTemplateKinds,
                    PerformanceOverlayTemplateFormatter.MaximumTemplateLength, out template, out _, out templateWarning)) return;
            cfg.PerformanceOverlayHardwareDetails = saved;
            if (_requestedTemplateKinds.Count > 0) cfg.PerformanceOverlayTextTemplate = template;
        });
        if (templateWarning != null)
        {
            _templateSuggestion.Text = templateWarning + " 硬件资料尚未保存。";
            return;
        }
        DialogResult = true;
    }

    private string[] SelectedTemplateKinds() => PerformanceHardwareDetails.Kinds
        .Where(kind => _kindSwitches.TryGetValue(kind.Key, out var show) && show.IsChecked == true &&
            PerformanceHardwareDetails.Fields.Any(field => _editors.TryGetValue((kind.Key, field.Key), out var editor) && editor.Show.IsChecked == true))
        .Select(kind => kind.Key).ToArray();

    private void RefreshTemplateSuggestion()
    {
        _templateInsert.Visibility = _usesTemplate ? Visibility.Visible : Visibility.Collapsed;
        if (!_usesTemplate)
        {
            _templateSuggestion.Text = "当前使用默认布局，勾选的硬件资料会自动显示；切换到自定义模板后，可用 {hardware_details} 插入所有已选资料。";
            return;
        }
        if (_templateDraft.Length > PerformanceOverlayTemplateFormatter.MaximumTemplateLength)
        {
            _templateInsert.IsEnabled = false;
            _templateSuggestion.Text = $"现有模板超过 {PerformanceOverlayTemplateFormatter.MaximumTemplateLength} 字符上限，请先到模板编辑器精简，再补充硬件字段。原文不会被截断。";
            return;
        }
        var selected = SelectedTemplateKinds();
        var missing = PerformanceHardwareDetails.MissingTemplateFields(_templateDraft, selected);
        _templateInsert.IsEnabled = selected.Length > 0 && missing.Count > 0;
        if (selected.Length == 0)
            _templateSuggestion.Text = "当前使用自定义模板。先勾选“在悬浮窗显示”及要显示的资料，再点右侧按钮补充模板字段。仅填写数值不会自动开启显示。";
        else if (missing.Count > 0)
        {
            var fields = string.Join("、", missing.Select(field => "{" + field + "}"));
            _templateSuggestion.Text = "建议补充 " + fields + "，让刚选的硬件资料显示在自定义模板中。原有文字和已有字段都会保留。";
        }
        else _templateSuggestion.Text = _requestedTemplateKinds.Count > 0
            ? _templateChangesUsePageSave
                ? "已将缺少的硬件字段加入草稿。保存硬件资料时会带回设置页，模板仍按设置页的保存方式处理；也可继续勾选其他硬件再补充。"
                : "已将缺少的硬件字段加入草稿，点击“保存”后一起生效；继续勾选其他硬件时还可补充。"
            : "现有模板已包含所选硬件资料的字段，无需重复添加。";
    }

    private void InsertTemplateFields()
    {
        var selected = SelectedTemplateKinds();
        if (!PerformanceHardwareDetails.TryAppendTemplateFields(_templateDraft, selected,
            PerformanceOverlayTemplateFormatter.MaximumTemplateLength, out var template, out var added, out var warning))
        {
            _templateSuggestion.Text = warning;
            return;
        }
        if (added.Count == 0) { RefreshTemplateSuggestion(); return; }
        _templateDraft = template;
        foreach (var kind in selected)
            if (added.Contains(kind.ToLowerInvariant() + "_details", StringComparer.OrdinalIgnoreCase)) _requestedTemplateKinds.Add(kind);
        _templateDraftText.Text = _templateDraft;
        _templatePreview.Visibility = Visibility.Visible;
        RefreshTemplateSuggestion();
    }
    private static void AddButton(Panel actions, string label, Action action, bool primary = false, bool cancel = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(6, 0, 0, 6), IsDefault = primary, IsCancel = cancel };
        button.Click += (_, _) => action(); actions.Children.Add(button);
    }
}
