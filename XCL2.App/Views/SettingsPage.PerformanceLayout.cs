using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private bool _performanceLayoutEventsReady;
    private AppConfig? _performanceLayoutDraft;
    private CheckBox? _performanceClickThroughCheck;

    private void InitializePerformanceLayoutControls()
    {
        if (_performanceLayoutEventsReady) return;
        _performanceLayoutEventsReady = true;
        InitializePerformanceTemplateHelp();
        _performanceClickThroughCheck = new CheckBox
        {
            Name = "PerformanceOverlayClickThroughCheck", Content = "鼠标穿透悬浮窗",
            Margin = new Thickness(0, 0, 20, 6),
            ToolTip = "默认关闭，可直接拖动悬浮窗主体或右键设置。开启穿透后才显示右上角小齿轮，点击打开设置，按住拖动可移动位置；其余区域的点击交给后方窗口。"
        };
        if (PerformanceOverlayTopmostCheck.Parent is Panel options) options.Children.Insert(0, _performanceClickThroughCheck);
        PerformanceTemplateFieldCombo.ItemsSource = PerformanceOverlayTemplateFormatter.Fields;
        PerformanceTemplateFieldCombo.SelectedIndex = 0;
        // Only the performance section previews these edits. Normal page tracking still owns save/auto-save/undo.
        PerformanceOverlaySettingsGroup.AddHandler(ToggleButton.CheckedEvent,
            new RoutedEventHandler((_, _) => PreviewPerformanceLayout()), true);
        PerformanceOverlaySettingsGroup.AddHandler(ToggleButton.UncheckedEvent,
            new RoutedEventHandler((_, _) => PreviewPerformanceLayout()), true);
        PerformanceOverlaySettingsGroup.AddHandler(Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, e) => { if (!ReferenceEquals(e.OriginalSource, PerformanceTemplateFieldCombo)) PreviewPerformanceLayout(); }), true);
        PerformanceOverlaySettingsGroup.AddHandler(TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, PerformanceOverlayTemplateBox)) return;
                if (_performanceControlsReady && !_suppressDirtyTracking && PerformanceOverlayTemplateBox.IsKeyboardFocusWithin)
                    SelectComboByTag(PerformanceOverlayLayoutModeCombo, "Template");
                PreviewPerformanceLayout();
            }), true);
        PerformanceOverlaySettingsGroup.AddHandler(RangeBase.ValueChangedEvent,
            new RoutedPropertyChangedEventHandler<double>((_, e) => { if (e.OriginalSource is Slider) PreviewPerformanceLayout(); }), true);
    }

    private void SavePerformanceLayoutSettings(AppConfig cfg)
    {
        cfg.PerformanceOverlayLayoutMode = TagOf(PerformanceOverlayLayoutModeCombo) == "Template" ? "Template" : "Default";
        cfg.PerformanceOverlayLineBreakMode = TagOf(PerformanceOverlayLineBreakCombo) is "SingleLine" or "EachField" ? TagOf(PerformanceOverlayLineBreakCombo)! : "Preserve";
        var template = PerformanceOverlayTemplateBox.Text ?? "";
        cfg.PerformanceOverlayTextTemplate = template.Length <= PerformanceOverlayTemplateFormatter.MaximumTemplateLength
            ? template : template[..PerformanceOverlayTemplateFormatter.MaximumTemplateLength];
        cfg.PerformanceOverlayAutoWrap = PerformanceOverlayAutoWrapCheck.IsChecked == true;
        cfg.PerformanceOverlayClickThrough = _performanceClickThroughCheck?.IsChecked ?? cfg.PerformanceOverlayClickThrough;
    }

    private AppConfig CapturePerformanceLayoutDraft()
    {
        var draft = new AppConfig();
        foreach (var property in PerformanceConfigProperties.Where(property => property.CanWrite))
            property.SetValue(draft, property.GetValue(_owner.ConfigService.Config));
        CapturePerformanceOverlayControls(draft);
        return draft;
    }

    private void PreviewPerformanceLayout()
    {
        if (!_performanceControlsReady || _suppressDirtyTracking) return;
        UpdateHardwarePerformanceUi();
        _performanceLayoutDraft = CapturePerformanceLayoutDraft();
        // Editing should never be disabled merely because the standard layout is selected.
        // A genuine user edit or insertion activates the custom layout, while loading config does not.
        PerformanceTemplateEditorPanel.IsEnabled = true;
        PerformanceOverlayAutoWrapCheck.IsEnabled = PerformanceOverlayTemplateFormatter.LineBreakMode(_performanceLayoutDraft) != "SingleLine";
        UpdatePerformanceTemplatePreview();
        PerformanceOverlayService.PreviewHardwareSettings(_performanceLayoutDraft);
    }

    private void PerformanceLayoutSampleUpdated(PerformanceSnapshot sample)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => PerformanceLayoutSampleUpdated(sample))); return; }
        if (!IsLoaded || !_performanceControlsReady || _suppressDirtyTracking) return;
        UpdatePerformanceTemplatePreview(sample);
    }

    private void UpdatePerformanceTemplatePreview(PerformanceSnapshot? sample = null)
    {
        var draft = _performanceLayoutDraft ?? CapturePerformanceLayoutDraft();
        sample ??= PerformanceOverlayService.LatestSnapshot;
        var maxAge = TimeSpan.FromMilliseconds(PerformanceOverlaySettings.RefreshMilliseconds(draft) * 2 + 1000);
        if (!PerformanceOverlayService.IsPolling || sample == null || DateTimeOffset.UtcNow - sample.SampledAt > maxAge) sample = null;
        var result = PerformanceOverlayTemplateFormatter.Format(sample, draft);
        PerformanceTemplatePreviewText.Text = string.IsNullOrWhiteSpace(result.Text) ? "未选择显示内容" : result.Text;
        PerformanceTemplatePreviewText.ToolTip = result.ToolTip;
        PerformanceTemplateWarningText.Text = result.Warning ?? "";
        PerformanceTemplateWarningText.Visibility = result.Warning == null ? Visibility.Collapsed : Visibility.Visible;
        var wrap = PerformanceOverlayTemplateFormatter.AutoWrap(draft);
        PerformanceTemplatePreviewText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        PerformanceTemplatePreviewScroll.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PerformanceTemplatePreviewFrame.Width = PerformanceOverlaySettings.WindowWidth(draft);
        PerformanceTemplatePreviewText.FontSize = PerformanceOverlaySettings.FontSize(draft);
        PerformanceTemplateSampleText.Text = sample == null
            ? "暂无有效采样：字段显示“暂不可用”，不填入模拟占用。开启悬浮窗后会显示当前数据。"
            : $"使用 {sample.SampledAt.LocalDateTime:HH:mm:ss} 的真实采样；预览省略标题与底部状态说明。";
    }

    private void PerformanceSingleLine_Click(object sender, RoutedEventArgs e)
    {
        SelectComboByTag(PerformanceOverlayLineBreakCombo, "SingleLine");
        PreviewPerformanceLayout();
        OnSettingsEdited();
    }

    private void PerformanceHardwareDetails_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PerformanceHardwareDetailsDialog(CapturePerformanceLayoutDraft(), change =>
        {
            // The page may contain an unsaved custom template. Apply the dialog's requested additions
            // to the latest controls, then save only its explicitly accepted hardware-settings group.
            var edit = PerformanceHardwareDetails.StageSettingsPageEdit(CapturePerformanceLayoutDraft(), change);
            if (edit == null) return;
            PerformanceOverlayService.UpdateSettings(cfg => cfg.PerformanceOverlayHardwareDetails = edit.HardwareDetails);
            var suppress = _suppressDirtyTracking;
            _suppressDirtyTracking = true;
            try { PerformanceOverlayTemplateBox.Text = edit.Template; }
            finally { _suppressDirtyTracking = suppress; }
            PreviewPerformanceLayout();
            OnSettingsEdited();
        }, templateChangesUsePageSave: true) { Owner = _owner };
        dialog.ShowDialog();
    }
    private void PerformancePreserveLines_Click(object sender, RoutedEventArgs e)
    {
        SelectComboByTag(PerformanceOverlayLineBreakCombo, "Preserve");
        PreviewPerformanceLayout();
        OnSettingsEdited();
    }
    private void InsertPerformanceField_Click(object sender, RoutedEventArgs e)
    {
        if (PerformanceTemplateFieldCombo.SelectedItem is not PerformanceOverlayTemplateField field) return;
        InsertPerformanceField(field);
    }

    private void InsertPerformanceField(PerformanceOverlayTemplateField field)
    {
        var start = PerformanceOverlayTemplateBox.SelectionStart;
        var available = PerformanceOverlayTemplateFormatter.MaximumTemplateLength - (PerformanceOverlayTemplateBox.Text.Length - PerformanceOverlayTemplateBox.SelectionLength);
        if (field.Placeholder.Length > available) { ToastService.ShowInfo("模板最多 4096 个字符，请先删去部分内容。"); return; }
        SelectComboByTag(PerformanceOverlayLayoutModeCombo, "Template");
        PerformanceOverlayTemplateBox.SelectedText = field.Placeholder;
        PerformanceOverlayTemplateBox.CaretIndex = Math.Min(start + field.Placeholder.Length, PerformanceOverlayTemplateBox.Text.Length);
        PerformanceOverlayTemplateBox.Focus();
        PreviewPerformanceLayout();
        OnSettingsEdited();
    }
    private void ResetPerformanceTemplate_Click(object sender, RoutedEventArgs e)
    {
        SelectComboByTag(PerformanceOverlayLayoutModeCombo, "Template");
        PerformanceOverlayTemplateBox.Text = PerformanceOverlayTemplateFormatter.DefaultTemplate;
        PreviewPerformanceLayout();
        OnSettingsEdited();
    }

    private void RegisterPerformanceLayoutResets(AppConfig defaults)
    {
        RegisterUxReset(PerformanceOverlayLayoutModeCombo, () => SelectComboByTag(PerformanceOverlayLayoutModeCombo, "Default"));
        RegisterUxReset(PerformanceOverlayLineBreakCombo, () => SelectComboByTag(PerformanceOverlayLineBreakCombo, "Preserve"));
        RegisterUxReset(PerformanceOverlayTemplateBox, () => PerformanceOverlayTemplateBox.Text = defaults.PerformanceOverlayTextTemplate);
        RegisterUxReset(PerformanceOverlayAutoWrapCheck, () => PerformanceOverlayAutoWrapCheck.IsChecked = defaults.PerformanceOverlayAutoWrap);
        if (_performanceClickThroughCheck != null)
            RegisterUxReset(_performanceClickThroughCheck, () => _performanceClickThroughCheck.IsChecked = defaults.PerformanceOverlayClickThrough);
    }

    private void InitializePerformanceTemplateHelp()
    {
        foreach (var help in PerformanceTemplateEditorPanel.Children.OfType<TextBlock>())
        {
            if (!help.Text.StartsWith("字段自带名称", StringComparison.Ordinal)) continue;
            help.Text = "可以直接输入自己的文字；输入或插入字段会启用自定义模板。普通字段带名称与单位，带 _value 的字段只显示数值和单位，可自行写名称。未勾选的项目留空；未知字段按原文保留并提示。用 {{ 和 }} 显示花括号，不执行表达式。";
        }
        var content = new StackPanel();
        var example = new TextBlock
        {
            Text = "例如：我的电脑 · CPU {system_cpu_value}\n内存 {memory_used_value} / {memory_total_value}\n字段内容来自实际采样；关闭的项目不会因插入字段而开启。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8)
        };
        example.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        content.Children.Add(example);
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8), ToolTip = "搜索字段名称或说明", MaxLength = 80 };
        InputExperienceService.SetIsAuxiliary(search, true);
        content.Children.Add(search);
        var fieldList = new StackPanel();
        content.Children.Add(new ScrollViewer { Content = fieldList, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void RefreshHelp()
        {
            fieldList.Children.Clear();
            var query = search.Text.Trim();
            foreach (var field in PerformanceOverlayTemplateFormatter.Fields.Where(field =>
                         query.Length == 0 || field.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            {
                var insert = new Button
                {
                    Content = new TextBlock { Text = field.Label, TextWrapping = TextWrapping.Wrap },
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 4),
                    ToolTip = "在模板光标处插入 " + field.Placeholder
                };
                insert.SetResourceReference(StyleProperty, "SecondaryButton");
                insert.Click += (_, _) => InsertPerformanceField(field);
                fieldList.Children.Add(insert);
            }
            if (fieldList.Children.Count == 0) fieldList.Children.Add(new TextBlock { Text = "没有匹配的字段。" });
        }
        search.TextChanged += (_, e) => { RefreshHelp(); e.Handled = true; };
        RefreshHelp();
        PerformanceTemplateEditorPanel.Children.Add(new Expander
        {
            Header = "字段帮助 · 搜索并点击插入", Content = content, Margin = new Thickness(0, 0, 0, 12)
        });
    }
}
