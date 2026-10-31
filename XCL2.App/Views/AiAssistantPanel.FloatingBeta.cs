using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    internal bool IsFloatingBetaView { get; private set; }
    internal bool IsSideDrawerView { get; private set; }
    internal Action? RequestHostReveal { get; set; }
    private Border? _compactFlyout;
    private ContentControl? _compactFlyoutBody;
    private TextBlock? _compactFlyoutTitle;
    private FrameworkElement? _compactFlyoutContent;
    private FrameworkElement? _compactEmptyState;
    private ScrollViewer? _compactAttachmentsScroll;
    private Grid? _compactConversation;
    private Grid? _compactInputRow;
    private Border? _compactToolbarHost;
    internal Action<AiAssistantConfig>? FloatingQuickConfigSaved { get; set; }
    internal AiAssistantConfig FloatingConfig => _config;
    internal bool CanChangeFloatingModel => !_sending;

    internal static Button CreateCompactIconButton(string iconKey, string name, string hint)
    {
        var icon = new System.Windows.Shapes.Path { Width = 17, Height = 17, Stretch = Stretch.Uniform,
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        icon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, iconKey);
        icon.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "TextPrimaryBrush");
        var button = new Button { Content = icon, Width = 32, Height = 32, Padding = new Thickness(0), ToolTip = hint };
        button.SetResourceReference(StyleProperty, "IconButtonStyle");
        AutomationProperties.SetName(button, name);
        return button;
    }

    internal static FrameworkElement CreateCompactTitle()
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var icon = new System.Windows.Shapes.Path { Width = 21, Height = 21, Stretch = Stretch.Uniform,
            StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(0, 0, 9, 0) };
        icon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "IconAI");
        icon.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "AccentBrush");
        title.Children.Add(icon);
        title.Children.Add(new TextBlock { Text = "AI助手", FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center });
        return title;
    }

    internal void EnableFloatingBetaView()
    {
        if (IsFloatingBetaView) return;
        IsFloatingBetaView = true;
        FloatingPanelHeader.Visibility = Visibility.Collapsed;
        if (WorkModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, "Build")) is { } build)
            build.Content = "Build · 执行";
        if (WorkModeCombo.Parent is Panel oldParent)
        {
            var index = oldParent.Children.IndexOf(WorkModeCombo);
            if (index > 0 && oldParent.Children[index - 1] is TextBlock { Text: "工作模式" }) oldParent.Children.RemoveAt(index - 1);
            oldParent.Children.Remove(WorkModeCombo);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = "工作模式", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
            WorkModeCombo.Margin = new Thickness(0);
            Grid.SetColumn(WorkModeCombo, 1);
            row.Children.Add(WorkModeCombo);
            FloatingModelOptions.Children.Insert(0, row);
        }
        foreach (var child in FloatingConversationLayout.Children.OfType<UIElement>())
            if (Grid.GetColumn(child) is 0 or 1 or 3 or 4) child.Visibility = Visibility.Collapsed;
        foreach (var column in new[] { 0, 1, 3, 4 }) FloatingConversationLayout.ColumnDefinitions[column].Width = new GridLength(0);
        CloseButton.ToolTip = "关闭聊天悬浮窗（保留悬浮球）";
        ModelCombo.ToolTip = "展开后搜索模型名称、ID 或供应商，滚轮可以切换模型。自动、普通或专家路由可在会话选项中选择。";
        UpdateModelSelectionState();
    }

    internal void EnableSideDrawerView()
    {
        EnableFloatingBetaView();
        if (IsSideDrawerView) return;
        IsSideDrawerView = true;
        // Both hosts use one toolbar, one conversation surface and one composer. The existing
        // controls are moved, so routing, history, approvals and attachment handlers stay intact.
        if (FloatingModelOptions.Parent is not Border toolbarHost || toolbarHost.Parent is not Grid conversation) return;
        _compactConversation = conversation;
        _compactToolbarHost = toolbarHost;
        toolbarHost.Child = null;
        toolbarHost.Margin = new Thickness(0);
        toolbarHost.Padding = new Thickness(14, 7, 14, 9);
        toolbarHost.CornerRadius = new CornerRadius(0);
        toolbarHost.BorderThickness = new Thickness(0, 0, 0, 1);
        toolbarHost.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        toolbarHost.SetResourceReference(Border.BackgroundProperty, "PanelBrush");

        if (ModelCombo.Parent is Grid modelContainer)
        {
            if (modelContainer.Parent is Grid modelRow) FloatingModelOptions.Children.Remove(modelRow);
            modelContainer.Children.Remove(ModelCombo);
        }
        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ModelCombo.Margin = new Thickness(0, 0, 8, 0);
        ModelCombo.FontSize = 12;
        ModelCombo.Height = 32;
        toolbar.Children.Add(ModelCombo);
        var toolbarActions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(toolbarActions, 1);
        toolbar.Children.Add(toolbarActions);
        var toolbarLayout = new StackPanel();
        toolbarLayout.Children.Add(toolbar);
        if (RouteStatusText.Parent is Panel oldStatusParent) oldStatusParent.Children.Remove(RouteStatusText);
        RouteStatusText.Margin = new Thickness(2, 5, 0, 0);
        RouteStatusText.FontSize = 10;
        RouteStatusText.TextTrimming = TextTrimming.CharacterEllipsis;
        RouteStatusText.ToolTip = "当前模型路由和请求状态；自动路由按问题类型选用普通或专家模型。";
        toolbarLayout.Children.Add(RouteStatusText);
        toolbarHost.Child = toolbarLayout;

        var optionsScroll = new ScrollViewer { Content = FloatingModelOptions,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var optionsButton = CreateCompactIconButton("IconMenu", "会话选项", "选择工作模式、模型路由、推理等级、联网搜索与任务选项。");
        optionsButton.Click += (_, _) => ToggleCompactFlyout("会话选项", optionsScroll);
        toolbarActions.Children.Add(optionsButton);
        var historyButton = CreateCompactIconButton("IconHistory", "对话历史", "打开本机对话历史；切换会话不会停止正在生成的回答。");
        historyButton.Click += (_, _) => ToggleCompactFlyout("对话历史", HistoryListBox);
        toolbarActions.Children.Add(historyButton);
        var newButton = CreateCompactIconButton("IconPlus", "新对话", "新建独立对话；已有内容仍保存在对话历史中。");
        if (AnalysisOnly) newButton.Visibility = Visibility.Collapsed;
        newButton.Click += (_, _) => { CloseCompactFlyout(); NewFloatingConversation(); };
        toolbarActions.Children.Add(newButton);
        var settingsButton = CreateCompactIconButton("IconSettings", "AI助手设置", "配置接口、模型、自动保存和助手权限。");
        settingsButton.Click += (_, _) => { CloseCompactFlyout(); SettingsRequested?.Invoke(this, EventArgs.Empty); };
        toolbarActions.Children.Add(settingsButton);

        if (HistoryListBox.Parent is Panel historyParent) historyParent.Children.Remove(HistoryListBox);
        HistoryListBox.Margin = new Thickness(0);
        HistoryListBox.MinHeight = 0;
        HistoryListBox.MaxHeight = double.PositiveInfinity;
        HistoryListBox.BorderThickness = new Thickness(0);
        HistoryListBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressHistorySelection && ReferenceEquals(_compactFlyoutContent, HistoryListBox)) CloseCompactFlyout();
        };
        if (TaskSelectionCombo.Parent is Grid taskRow)
        {
            taskRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            taskRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumnSpan(TaskSelectionCombo, 2);
            foreach (var buttons in taskRow.Children.OfType<StackPanel>())
            {
                Grid.SetRow(buttons, 1); Grid.SetColumn(buttons, 0); Grid.SetColumnSpan(buttons, 3);
                buttons.Margin = new Thickness(0, 6, 0, 0);
            }
        }
        BuildCompactComposer();
        BuildCompactFlyout(conversation);
        BuildCompactEmptyState(conversation);
        MessagesScroll.Padding = new Thickness(16, 12, 16, 12);
        DrawerBackgroundLayer.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        DrawerBackgroundLayer.Effect = null;
        conversation.SizeChanged += (_, _) => UpdateCompactLayout();
        InputArea.SizeChanged += (_, _) => UpdateCompactLayout();
        MessagesScroll.SizeChanged += (_, _) => UpdateCompactLayout();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseCompactPopups(); };
        Unloaded += (_, _) => CloseCompactPopups();
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && _compactFlyout?.Visibility == Visibility.Visible)
            { CloseCompactFlyout(); args.Handled = true; }
        };
        PreviewMouseDown += (_, args) =>
        {
            if (_compactFlyout?.Visibility != Visibility.Visible) return;
            // Dismiss only from known chat surfaces. ComboBox popup roots do not reliably
            // reconnect to their placement target through the visual tree.
            var source = args.OriginalSource as DependencyObject;
            if (IsInside(source, MessagesScroll) || IsInside(source, InputArea)
                || (_compactEmptyState != null && IsInside(source, _compactEmptyState))) CloseCompactFlyout();
        };
    }

    private void BuildCompactComposer()
    {
        if (InputArea.Child is not StackPanel composer || InputTextBox.Parent is not Grid inputRow) return;
        _compactInputRow = inputRow;
        var quickOptions = composer.Children.OfType<Grid>().FirstOrDefault();
        if (quickOptions != null && !ReferenceEquals(quickOptions, inputRow))
        {
            composer.Children.Remove(quickOptions);
            quickOptions.Margin = new Thickness(0, 12, 0, 8);
            FloatingModelOptions.Children.Add(quickOptions);
        }
        if (UploadAttachmentButton.Parent is Panel attachmentParent) attachmentParent.Children.Remove(UploadAttachmentButton);
        foreach (var task in composer.Children.OfType<Expander>().ToArray())
        {
            composer.Children.Remove(task);
            task.Margin = new Thickness(0, 10, 0, 0);
            FloatingModelOptions.Children.Add(task);
        }
        composer.Children.Remove(WorkModeHintText);
        WorkModeHintText.Margin = new Thickness(0, 10, 0, 0);
        WorkModeHintText.MaxHeight = double.PositiveInfinity;
        FloatingModelOptions.Children.Add(WorkModeHintText);
        composer.Children.Remove(PendingAttachmentsItemsControl);
        var attachmentScroll = _compactAttachmentsScroll = new ScrollViewer { Content = PendingAttachmentsItemsControl, MaxHeight = 108,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        composer.Children.Insert(0, _compactAttachmentsScroll);
        void RefreshAttachments()
        {
            attachmentScroll.Visibility = PendingAttachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateCompactLayout();
        }
        PendingAttachments.CollectionChanged += (_, _) => RefreshAttachments();
        RefreshAttachments();

        // Give the editor a full row. Secondary actions sit below it instead of narrowing the text.
        inputRow.Children.Remove(StopButton);
        inputRow.Children.Remove(SendButton);
        inputRow.Children.Remove(UsageIndicatorButton);
        inputRow.Children.Remove(UsagePopup);
        Grid.SetColumnSpan(InputTextBox, 4);
        InputTextBox.MinHeight = 64;
        InputTextBox.MaxHeight = 112;
        InputTextBox.Padding = new Thickness(2, 3, 2, 8);
        InputTextBox.FontSize = 13;
        InputTextBox.BorderThickness = new Thickness(0);
        InputTextBox.Background = Brushes.Transparent;
        var composerActions = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        composerActions.ColumnDefinitions.Add(new ColumnDefinition());
        composerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UploadAttachmentButton.Content = "+ 附件";
        UploadAttachmentButton.Margin = new Thickness(0);
        UploadAttachmentButton.Padding = new Thickness(8, 4, 8, 4);
        UploadAttachmentButton.FontSize = 11;
        UploadAttachmentButton.HorizontalAlignment = HorizontalAlignment.Left;
        UploadAttachmentButton.SetResourceReference(StyleProperty, "QuietActionButton");
        composerActions.Children.Add(UploadAttachmentButton);
        var sendActions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(sendActions, 1);
        composerActions.Children.Add(sendActions);
        UsageIndicatorButton.Width = 28; UsageIndicatorButton.Height = 30; UsageIndicatorButton.Margin = new Thickness(0, 0, 8, 0);
        sendActions.Children.Add(UsageIndicatorButton);
        foreach (var button in new[] { StopButton, SendButton })
        {
            button.Width = 34; button.Height = 32; button.Margin = new Thickness(4, 0, 0, 0);
            sendActions.Children.Add(button);
        }
        SendButton.SetResourceReference(Control.BackgroundProperty, "GlowSoftBrush");
        composerActions.Children.Add(UsagePopup);
        composer.Children.Insert(composer.Children.IndexOf(inputRow) + 1, composerActions);
        foreach (var text in composer.Children.OfType<TextBlock>())
            if (text.Text.StartsWith("AI 可能会犯错", StringComparison.Ordinal)) text.Visibility = Visibility.Collapsed;
        InputArea.Margin = new Thickness(14, 6, 14, 2);
        InputArea.Padding = new Thickness(12, 10, 12, 9);
        InputArea.CornerRadius = new CornerRadius(12);
        InputArea.SetResourceReference(Border.BackgroundProperty, "SideBrush");
        NoKeyHint.Text = "请在设置中添加可用模型";
        NoKeyHint.TextWrapping = TextWrapping.Wrap;
        NoKeyHint.MaxWidth = 165;
        NoKeyHint.Margin = new Thickness(0, 3, 0, 0);
        NoKeyHint.HorizontalAlignment = HorizontalAlignment.Left;
        if (NoKeyHint.Parent is Grid hintRow)
        {
            hintRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            hintRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(NoKeyHint, 1);
        }
        QuestionCard.Margin = new Thickness(14, 6, 14, 2);
        CompactTermsButton.Content = "用户协议与条款 · 关闭AI助手";
        CompactTermsButton.SetResourceReference(StyleProperty, "QuietActionButton");
        CompactTermsButton.FontSize = 10;
        CompactTermsButton.Padding = new Thickness(8, 3, 8, 3);
        CompactTermsButton.MinHeight = 24;
        CompactTermsButton.Margin = new Thickness(8, 4, 8, 6);
    }

    private void UpdateCompactLayout()
    {
        if (_compactConversation == null || _compactToolbarHost == null || _compactInputRow == null
            || _compactAttachmentsScroll == null || InputArea.ActualHeight <= 0) return;
        var available = Math.Max(0, _compactConversation.ActualHeight - _compactToolbarHost.ActualHeight);
        // Reserve a usable message viewport. Only the editor and attachment list shrink;
        // send/stop, warnings and the flyout close button keep their normal hit targets.
        var fixedComposer = Math.Max(0, InputArea.ActualHeight - _compactInputRow.ActualHeight - _compactAttachmentsScroll.ActualHeight);
        var editorBudget = Math.Max(28, available - Math.Min(120, available * .4)
            - fixedComposer - InputArea.Margin.Top - InputArea.Margin.Bottom);
        var attachmentBudget = PendingAttachments.Count == 0 ? 0 : Math.Min(108, Math.Max(0, (editorBudget - 28) * .35));
        _compactAttachmentsScroll.MaxHeight = attachmentBudget;
        var inputHeight = Math.Clamp(editorBudget - attachmentBudget, 28, 112);
        InputTextBox.MinHeight = Math.Min(64, inputHeight);
        InputTextBox.MaxHeight = inputHeight;
        if (_compactFlyout != null) _compactFlyout.MaxHeight = Math.Max(0, MessagesScroll.ActualHeight - 16);
    }

    private void CloseCompactPopups()
    {
        CloseCompactFlyout();
        ModelCombo.IsDropDownOpen = false;
        UsagePopup.IsOpen = false;
        _usageCloseTimer?.Stop();
    }

    private void BuildCompactFlyout(Grid conversation)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = CreateCompactIconButton("IconClose", "关闭展开区域", "收起选项或历史，返回聊天。Esc 也可收起。");
        close.Width = close.Height = 26;
        close.Click += (_, _) => CloseCompactFlyout();
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        _compactFlyoutTitle = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(_compactFlyoutTitle);
        layout.Children.Add(header);
        _compactFlyoutBody = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_compactFlyoutBody, 1); layout.Children.Add(_compactFlyoutBody);
        _compactFlyout = new Border { Child = layout, Padding = new Thickness(14, 10, 14, 14), Margin = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Top, MaxHeight = 320, ClipToBounds = true };
        _compactFlyout.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _compactFlyout.SetResourceReference(Border.BorderBrushProperty, "BorderHoverBrush");
        PageTransitionBehavior.SetAnimateOnVisible(_compactFlyout, true);
        PageTransitionBehavior.SetAnimateOnVisible(_compactFlyoutBody, true);
        Grid.SetRow(_compactFlyout, 1); Panel.SetZIndex(_compactFlyout, 10);
        conversation.Children.Add(_compactFlyout);
    }

    private void BuildCompactEmptyState(Grid conversation)
    {
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24), MaxWidth = 290 };
        var emptyHost = new Grid { ClipToBounds = true };
        emptyHost.Children.Add(empty);
        _compactEmptyState = emptyHost;
        var title = new TextBlock { Text = "有什么可以帮你？", FontSize = 20, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
        empty.Children.Add(title);
        var hint = new TextBlock { Text = "询问启动器功能，或一起分析游戏问题。", FontSize = 12, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 18) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        empty.Children.Add(hint);
        var suggestions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var pair in new[] { ("查找功能", "请介绍启动器中常用功能的入口。"), ("排查游戏问题", "请帮我分析游戏运行中遇到的问题。") })
        {
            var suggestion = new Button { Content = pair.Item1, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(3), FontSize = 11 };
            suggestion.SetResourceReference(StyleProperty, "SecondaryButton");
            suggestion.ToolTip = "将问题填入输入框，你可以修改后再发送。";
            suggestion.Click += (_, _) => { InputTextBox.Text = pair.Item2; InputTextBox.Focus(); InputTextBox.CaretIndex = InputTextBox.Text.Length; };
            suggestions.Children.Add(suggestion);
        }
        empty.Children.Add(suggestions);
        Grid.SetRow(emptyHost, 1); conversation.Children.Add(emptyHost);
        MessagesScroll.SizeChanged += (_, _) =>
        {
            var shortViewport = MessagesScroll.ActualHeight < 180;
            suggestions.Visibility = shortViewport ? Visibility.Collapsed : Visibility.Visible;
            hint.Visibility = MessagesScroll.ActualHeight < 115 ? Visibility.Collapsed : Visibility.Visible;
            empty.Margin = new Thickness(shortViewport ? 12 : 24);
        };
        void RefreshEmpty() => emptyHost.Visibility = _bubbles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _bubbles.CollectionChanged += (_, _) => RefreshEmpty();
        RefreshEmpty();
    }

    private void ToggleCompactFlyout(string title, FrameworkElement content)
    {
        if (_compactFlyout == null || _compactFlyoutBody == null || _compactFlyoutTitle == null) return;
        if (_compactFlyout.Visibility == Visibility.Visible && ReferenceEquals(_compactFlyoutContent, content)) { CloseCompactFlyout(); return; }
        _compactFlyoutContent = content;
        _compactFlyoutBody.Content = content;
        _compactFlyoutTitle.Text = title;
        _compactFlyout.MaxHeight = Math.Max(0, MessagesScroll.ActualHeight - 16);
        _compactFlyout.Visibility = Visibility.Visible;
    }

    private void CloseCompactFlyout()
    {
        if (_compactFlyout == null) return;
        foreach (var combo in new[] { RoutingModeCombo, WorkModeCombo, ReasoningLevelCombo, TaskSelectionCombo }) combo.IsDropDownOpen = false;
        _compactFlyout.Visibility = Visibility.Collapsed;
        if (_compactFlyoutBody != null) _compactFlyoutBody.Content = null;
        _compactFlyoutContent = null;
    }

    private static bool IsInside(DependencyObject? source, DependencyObject container)
    {
        for (var current = source; current != null; current = current is FrameworkElement element
            ? element.Parent ?? VisualTreeHelper.GetParent(element) : current is FrameworkContentElement content
            ? content.Parent : current is Visual ? VisualTreeHelper.GetParent(current) : null)
            if (ReferenceEquals(current, container)) return true;
        return false;
    }

    internal void SelectFloatingMode(AiWorkMode mode)
    {
        if (!Enum.IsDefined(mode)) return;
        WorkModeCombo.SelectedItem = WorkModeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag && tag == mode.ToString());
    }

    internal void SelectFloatingModel(string? modelKey, AiRoutingMode route = AiRoutingMode.SpecificModel)
    {
        if (_sending) return;
        if (route == AiRoutingMode.SpecificModel)
        {
            var model = AiModelIds.FindInCatalog(_config, modelKey);
            if (model == null) return;
            _config.SelectedModel = model.ModelKey;
        }
        _config.RoutingMode = route;
        _config.AutoModelRouting = route == AiRoutingMode.Auto;
        RefreshRoutingUi(); RefreshModelCatalog(); RefreshReasoningUi(); UpdateRoutingText(); PersistQuickConfig();
    }

    internal void NewFloatingConversation() => NewChatButton_Click(NewChatButton, new RoutedEventArgs());
}
