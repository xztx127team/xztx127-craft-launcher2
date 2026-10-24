using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XCL2.App.Models;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    private string _modelSearchQuery = "";

    private bool ModelMatchesSearch(AiModelDefinition model)
    {
        var query = _modelSearchQuery;
        return query.Length == 0 || model.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (model.ProviderName ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ModelSearch_Changed(object sender, TextChangedEventArgs e)
    {
        _modelSearchQuery = (sender as TextBox)?.Text.Trim() ?? "";
        if (ModelCombo == null || _service == null) return;
        RefreshModelCatalog();
    }

    private void ModelCombo_DropDownOpened(object? sender, EventArgs e)
    {
        // The editor belongs to the popup template: it never occupies space in the closed selector.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!ModelCombo.IsDropDownOpen) return;
            if (ModelCombo.Template.FindName("PopupModelSearchBox", ModelCombo) is TextBox search)
            {
                search.Focus();
                search.SelectAll();
            }
        }));
    }

    private void ModelCombo_DropDownClosed(object? sender, EventArgs e)
    {
        _modelSearchQuery = "";
        if (ModelCombo.Template.FindName("PopupModelSearchBox", ModelCombo) is TextBox search) search.Clear();
        if (_service != null) RefreshModelCatalog();
    }

    private void ModelItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_sending || sender is not ComboBoxItem { Content: AiModelDefinition model }) return;
        var key = model.ModelKey;
        // Clicking the already-highlighted item raises no SelectionChanged. Treat that
        // explicit click as a manual choice too, after the native ComboBox commits/closes.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (_sending || !IsFloatingBetaView && _config.RoutingMode != AiRoutingMode.SpecificModel) return;
            SelectFloatingModel(key);
            ModelCombo.IsDropDownOpen = false;
        }));
    }

    private void ModelSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ModelCombo.IsDropDownOpen = false;
            e.Handled = true;
        }
        else if ((e.Key is Key.Down or Key.Up) && ModelCombo.Items.Count > 0)
        {
            ModelCombo.SelectedIndex = Math.Clamp(ModelCombo.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, ModelCombo.Items.Count - 1);
            (ModelCombo.ItemContainerGenerator.ContainerFromIndex(ModelCombo.SelectedIndex) as FrameworkElement)?.BringIntoView();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ModelCombo.Items.Count > 0)
        {
            if (ModelCombo.SelectedIndex < 0) ModelCombo.SelectedIndex = 0;
            if (ModelCombo.SelectedItem is AiModelDefinition model) SelectFloatingModel(model.ModelKey);
            ModelCombo.IsDropDownOpen = false;
            ModelCombo.Focus();
            e.Handled = true;
        }
    }

    private void ModelCombo_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!ModelCombo.IsEnabled || ModelCombo.Items.Count == 0) return;
        ModelCombo.SelectedIndex = Math.Clamp(ModelCombo.SelectedIndex + (e.Delta < 0 ? 1 : -1), 0, ModelCombo.Items.Count - 1);
        e.Handled = true;
    }

    private async void RejectAssistantTerms_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow launcher)
            await launcher.RevokeAiTermsAsync(confirmationOwner: Window.GetWindow(this));
        else
            MessageBox.Show("暂时无法找到启动器主窗口，请重新打开 AI 助手后再试。", "关闭 AI 助手", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
