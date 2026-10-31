using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;

namespace XCL2.App.Views;

public partial class DownloadCenterPage
{
    private BedrockPage? _bedrockDownloads;

    private void OpenBedrockDownloads()
    {
        _bedrockDownloads ??= new BedrockPage(_owner);
        BedrockDownloadHost.Content = _bedrockDownloads;
        BedrockDownloadHost.Visibility = Visibility.Visible;
    }

    private void RefreshBedrockFavorites()
    {
        var favorites = _owner.ConfigService.Config.FavoriteItems
            .Where(item => item.Type is FavoriteItemType.BedrockClient or FavoriteItemType.BedrockServer)
            .ToList();
        BedrockFavoritesList.ItemsSource = favorites;
        BedrockFavoritesPanel.Visibility = favorites.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OpenBedrockFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteItem favorite }) return;
        CatBedrock.IsChecked = true;
        OpenBedrockDownloads();
        await _bedrockDownloads!.SelectDownloadFavoriteAsync(favorite);
    }
}
