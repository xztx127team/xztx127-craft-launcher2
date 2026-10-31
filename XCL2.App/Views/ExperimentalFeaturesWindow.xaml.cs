using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ExperimentalFeaturesWindow : OverlayDialogControl
{
    private readonly MainWindow _owner;

    public ExperimentalFeaturesWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        MiniWorldFeatureButton.Visibility = owner.ConfigService.Config.EnableMiniWorldExperimental ? Visibility.Visible : Visibility.Collapsed;
        foreach (var feature in ExperimentalFeatureCatalog.All.Skip(2))
        {
            var card = new StackPanel { Margin = new Thickness(0, 8, 0, 12) };
            card.Children.Add(new TextBlock { Text = feature.Title, FontWeight = FontWeights.Bold });
            var description = new TextBlock { Text = feature.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); card.Children.Add(description);
            var open = new Button { Content = "查看本次启动密码并执行", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6) };
            open.Click += async (_, _) =>
            {
                open.IsEnabled = false;
                try
                {
                    if (new ExperimentalAccessDialog(feature.Id).ShowDialog() == true)
                    {
                        Close();
                        await ExperimentalFeatureRunner.RunAsync(feature.Id, _owner);
                    }
                }
                finally { open.IsEnabled = true; }
            };
            card.Children.Add(open); AdditionalFeaturesPanel.Children.Add(card);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWith(null);

    private void MiniWorldFeature_Click(object sender, RoutedEventArgs e)
    {
        if (!_owner.ConfigService.Config.EnableMiniWorldExperimental) return;
        if (new ExperimentalAccessDialog("mini-world").ShowDialog() != true) return;
        if (new MiniWorldGateDialog().ShowDialog() != true) return;
        if (_owner.ConfigService.Config.EnableMiniWorldExperimental) new MiniWorldLauncherDialog(_owner).ShowDialog();
    }

    private void MultiLoaderFeature_Click(object sender, RoutedEventArgs e)
    {
        var window = new MultiLoaderInstallWindow(_owner);
        if (window.ShowDialog() == true && window.InstalledVersionIds.Count > 0) _owner.RefreshVersionsPageIfActive();
    }
}
