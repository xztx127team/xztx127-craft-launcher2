using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XCL2.App.Models;

namespace XCL2.App.Views;

internal sealed class AiModelMenuPicker : StackPanel
{
    private sealed record Row(AiModelDefinition Model)
    {
        public string Label => string.IsNullOrWhiteSpace(Model.ProviderName) ? Model.DisplayName : Model.ProviderName + " · " + Model.DisplayName;
    }
    internal AiModelMenuPicker(IEnumerable<AiModelDefinition> models, string? selected, Action<string> choose, Action close)
    {
        Width = 300;
        var rows = models.Select(m => new Row(m)).ToList();
        var search = new TextBox { ToolTip = "输入模型名称、ID 或供应商筛选；滚轮或方向键切换，Enter 确认。", MinHeight = 32, Margin = new Thickness(4, 4, 4, 8) };
        var list = new ListBox { DisplayMemberPath = nameof(Row.Label), MaxHeight = 280, MinHeight = 70,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        var empty = new TextBlock { Text = "没有匹配的模型", Visibility = Visibility.Collapsed, Margin = new Thickness(8, 8, 8, 10) };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        bool filtering = true;
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Model.ModelKey, selected, StringComparison.OrdinalIgnoreCase));
        filtering = false;
        list.SelectionChanged += (_, _) =>
        {
            if (!filtering && list.SelectedItem is Row row) { selected = row.Model.ModelKey; choose(selected); }
        };
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            filtering = true;
            list.ItemsSource = rows.Where(r => query.Length == 0 || r.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.Model.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            list.SelectedItem = list.Items.OfType<Row>().FirstOrDefault(r => r.Model.ModelKey == selected);
            filtering = false;
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { close(); e.Handled = true; }
            else if ((e.Key is Key.Up or Key.Down) && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, list.Items.Count - 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && list.Items.Count > 0)
            {
                if (list.SelectedIndex < 0) list.SelectedIndex = 0;
                close(); e.Handled = true;
            }
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible) search.Focus();
        }));
        list.PreviewMouseWheel += (_, e) =>
        {
            if (list.Items.Count == 0) return;
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Delta < 0 ? 1 : -1), 0, list.Items.Count - 1);
            list.ScrollIntoView(list.SelectedItem); e.Handled = true;
        };
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem) close();
        };
        list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { close(); e.Handled = true; } };
        Children.Add(new TextBlock { Text = "搜索模型", FontSize = 11, Margin = new Thickness(4, 3, 4, 0) });
        Children.Add(search); Children.Add(list); Children.Add(empty);
        Children.Add(new TextBlock { Text = "输入搜索 · 滚轮切换模型 · 点击确认", FontSize = 10, Margin = new Thickness(4, 8, 4, 4) });
    }
}
