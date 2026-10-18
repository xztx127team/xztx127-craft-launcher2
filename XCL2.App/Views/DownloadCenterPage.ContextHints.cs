using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class DownloadCenterPage
{
    private sealed record ContextHint(Border Host, TextBlock Text, Button Action);
    private readonly Dictionary<ListBox, ContextHint> _contextHints = new();

    private void InitializeContextHints()
    {
        foreach (var list in new[] { OnlineListBox, ResourceListBox, MapListBox, ModListBox })
        {
            if (list.Parent is not Grid grid) continue;
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var action = new Button { MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Center,
                Style = Application.Current.TryFindResource("SecondaryButton") as Style };
            action.Click += (_, _) => { if (action.Tag is Action callback) callback(); };
            var stack = new StackPanel();
            stack.Children.Add(text);
            stack.Children.Add(action);
            var host = new Border { Child = stack, Padding = new Thickness(20), MaxWidth = 440,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed };
            Grid.SetRow(host, Grid.GetRow(list));
            Grid.SetColumn(host, Grid.GetColumn(list));
            grid.Children.Add(host);
            _contextHints[list] = new(host, text, action);
        }
        _mcModHits.CollectionChanged += (_, _) =>
        {
            ModWikiReferenceButton.Visibility = _mcModHits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_mcModHits.Count == 0) McModReferencePopup.IsOpen = false;
        };
        Unloaded += (_, _) => McModReferencePopup.IsOpen = false;
        // 行内点击已有入口也能通过键盘使用；焦点在按钮上时保留按钮自身的 Enter 行为。
        ModListBox.KeyDown += async (_, e) =>
        {
            if (CanOpenWithKeyboard(e) && ModListBox.SelectedItem is UnifiedModItem item)
            { e.Handled = true; await OpenModDetailAsync(item); }
        };
        ResourceListBox.KeyDown += async (_, e) =>
        {
            if (CanOpenWithKeyboard(e) && ResourceListBox.SelectedItem is UnifiedResourceItem item)
            { e.Handled = true; await OpenResourceDetailAsync(item); }
        };
        MapListBox.KeyDown += async (_, e) =>
        {
            if (CanOpenWithKeyboard(e) && MapListBox.SelectedItem is CurseForgeMod item)
            { e.Handled = true; await OpenMapDetailAsync(item); }
        };
        Loaded += (_, _) =>
        {
            PresentationPreferencesService.Changed -= RefreshPresentationText;
            PresentationPreferencesService.Changed += RefreshPresentationText;
            RefreshPresentationText();
        };
        Unloaded += (_, _) => PresentationPreferencesService.Changed -= RefreshPresentationText;
    }

    private void ModWikiReference_Click(object sender, RoutedEventArgs e)
    {
        McModReferencePopup.IsOpen = !McModReferencePopup.IsOpen;
        if (McModReferencePopup.IsOpen) McModListBox.Focus();
    }

    private void McModReference_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        McModReferencePopup.IsOpen = false;
        ModWikiReferenceButton.Focus();
        e.Handled = true;
    }

    private static bool CanOpenWithKeyboard(KeyEventArgs e)
        => !e.Handled && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
           && Keyboard.FocusedElement is ListBox or ListBoxItem;

    private void SetContextHint(ListBox list, string? message, string actionLabel = "", Action? action = null)
    {
        if (!_contextHints.TryGetValue(list, out var hint)) return;
        hint.Host.Visibility = list.Items.Count == 0 && !string.IsNullOrWhiteSpace(message)
            ? Visibility.Visible : Visibility.Collapsed;
        hint.Text.Text = message ?? "";
        hint.Action.Content = actionLabel;
        hint.Action.Tag = action;
        AutomationProperties.SetName(hint.Action, actionLabel);
        hint.Action.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetVersionSearch()
    {
        VersionSearchBox.Clear();
        _versionPageIndex = 0;
        ApplyVersionFilter();
    }

    private void ResetMapSearch()
    {
        MapSearchBox.Clear();
        MapGameVersionBox.Clear();
        _mapPageIndex = 0;
        _debounceTimer.Stop();
        _ = RunMapSearchAsync();
    }

    private void RefreshPresentationText()
    {
        foreach (var item in _online.Concat(_favorites)) item.RefreshPresentationText();
    }
}
