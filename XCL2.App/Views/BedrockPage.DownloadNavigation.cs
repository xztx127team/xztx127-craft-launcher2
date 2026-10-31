using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class BedrockPage
{
    private bool _downloadNavigationReady;
    private bool _updatingFavoriteLists;
    private FavoriteItem? _requestedClientFavorite;
    private FavoriteItem? _requestedServerFavorite;
    private bool _serverFavoriteMissing;

    private void InitializeDownloadNavigation()
    {
        _downloadNavigationReady = true;
        BedrockClientVersionCombo.SelectionChanged += (_, _) => UpdateBedrockFavoriteButtons();
        BedrockServerVersionCombo.SelectionChanged += (_, _) => UpdateBedrockFavoriteButtons();
        RefreshDownloadFavoriteLists();
        UpdateBedrockFavoriteButtons();
    }

    private void DownloadSection_Checked(object sender, RoutedEventArgs e)
    {
        if (!_downloadNavigationReady || sender is not RadioButton tab) return;
        var server = Equals(tab.Tag, "server");
        ClientDownloadSection.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
        ServerDownloadSection.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
    }

    private FavoriteItem? CurrentDownloadFavorite(bool server)
    {
        var version = server
            ? BedrockServerVersionCombo.SelectedItem as string
            : (BedrockClientVersionCombo.SelectedItem as BedrockClientDownloadService.BedrockVersionInfo)?.Name;
        if (string.IsNullOrWhiteSpace(version)) return null;
        var preview = server ? BedrockServerChannelCombo.SelectedIndex == 1 : BedrockClientChannelCombo.SelectedIndex == 1;
        var channel = preview ? "Preview" : "Stable";
        return new FavoriteItem
        {
            Type = server ? FavoriteItemType.BedrockServer : FavoriteItemType.BedrockClient,
            Source = ModSource.Combined,
            SourceId = channel + "|" + version,
            Title = (server ? "服务端 · " : "客户端 · ") + version,
            Description = preview ? "基岩版 · 预览版" : "基岩版 · 正式版",
            Author = channel
        };
    }

    private void UpdateBedrockFavoriteButtons()
    {
        if (!_downloadNavigationReady) return;
        Update(ClientFavoriteButton, CurrentDownloadFavorite(false));
        Update(ServerFavoriteButton, CurrentDownloadFavorite(true));
        if (BedrockServerVersionCombo.SelectedItem != null) _serverFavoriteMissing = false;
        BedrockServerDownloadBtn.IsEnabled = !_serverFavoriteMissing;
        void Update(Button button, FavoriteItem? selected)
        {
            button.IsEnabled = selected != null;
            var favorite = selected != null && _owner.ConfigService.Config.FavoriteItems.Any(item =>
                item.MatchesKey(selected.Type, selected.SourceId, selected.Source));
            button.Content = favorite ? "★ 已收藏 · 点击取消" : "☆ 收藏选中版本";
        }
    }

    private void ToggleBedrockFavorite_Click(object sender, RoutedEventArgs e)
    {
        var selected = CurrentDownloadFavorite(sender is Button { Tag: "server" });
        if (selected == null) return;
        var items = _owner.ConfigService.Config.FavoriteItems;
        var existing = items.FirstOrDefault(item => item.MatchesKey(selected.Type, selected.SourceId, selected.Source));
        if (existing != null) items.Remove(existing);
        else items.Add(selected);
        _owner.ConfigService.Save();
        RefreshDownloadFavoriteLists();
        UpdateBedrockFavoriteButtons();
    }

    private void RefreshDownloadFavoriteLists()
    {
        _updatingFavoriteLists = true;
        try
        {
            var items = _owner.ConfigService.Config.FavoriteItems;
            ClientFavoriteCombo.ItemsSource = items.Where(item => item.Type == FavoriteItemType.BedrockClient).ToList();
            ServerFavoriteCombo.ItemsSource = items.Where(item => item.Type == FavoriteItemType.BedrockServer).ToList();
        }
        finally { _updatingFavoriteLists = false; }
    }

    private async void FavoriteVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFavoriteLists || !_downloadNavigationReady || sender is not ComboBox { SelectedItem: FavoriteItem favorite }) return;
        await SelectDownloadFavoriteAsync(favorite);
    }

    public async Task SelectDownloadFavoriteAsync(FavoriteItem favorite)
    {
        var parts = favorite.SourceId.Split('|', 2);
        if (parts.Length != 2 || favorite.Type is not (FavoriteItemType.BedrockClient or FavoriteItemType.BedrockServer)) return;
        var preview = parts[0] == "Preview";
        if (favorite.Type == FavoriteItemType.BedrockServer)
        {
            _serverFavoriteMissing = false;
            _requestedServerFavorite = favorite;
            BedrockServerTab.IsChecked = true;
            BedrockServerChannelCombo.SelectedIndex = preview ? 1 : 0;
            await LoadServerVersionsAsync();
            ApplyRequestedServerFavorite();
            _requestedServerFavorite = null;
            if (BedrockServerVersionCombo.SelectedItem == null)
            {
                _serverFavoriteMissing = true;
                BedrockServerStatusText.Text = "收藏的版本暂未在当前下载索引中找到，请刷新或选择其他版本。";
            }
        }
        else
        {
            _requestedClientFavorite = favorite;
            BedrockClientTab.IsChecked = true;
            BedrockClientChannelCombo.SelectedIndex = preview ? 1 : 0;
            await LoadClientVersionsAsync();
            ApplyRequestedClientFavorite();
            _requestedClientFavorite = null;
            UpdateClientVersionAvailability();
            if (BedrockClientVersionCombo.SelectedItem == null)
                BedrockClientStatusText.Text = "收藏的版本暂未在当前下载索引中找到，请刷新或选择其他版本。";
        }
        UpdateBedrockFavoriteButtons();
    }

    private void ApplyRequestedClientFavorite()
    {
        if (_requestedClientFavorite == null) return;
        var parts = _requestedClientFavorite.SourceId.Split('|', 2);
        if (parts.Length != 2) return;
        BedrockClientVersionCombo.SelectedItem = _clientVersions.FirstOrDefault(version =>
            version.Channel.ToString() == parts[0] && version.Name == parts[1]);
    }

    private void ApplyRequestedServerFavorite()
    {
        if (_requestedServerFavorite == null) return;
        var parts = _requestedServerFavorite.SourceId.Split('|', 2);
        if (parts.Length != 2) return;
        BedrockServerVersionCombo.SelectedItem = BedrockServerVersionCombo.Items.Cast<string>()
            .FirstOrDefault(version => version == parts[1]);
    }

    private void OpenBedrockWiki_Click(object sender, RoutedEventArgs e)
    {
        var server = sender is Button { Tag: "server" };
        var version = server ? BedrockServerVersionCombo.SelectedItem as string
            : (BedrockClientVersionCombo.SelectedItem as BedrockClientDownloadService.BedrockVersionInfo)?.Name;
        var article = server ? "基岩版专用服务器" : string.IsNullOrWhiteSpace(version) ? "基岩版" : "基岩版" + version;
        ExternalLinkService.Open(server
            ? "https://zh.minecraft.wiki/w/" + Uri.EscapeDataString(article)
            : "https://zh.minecraft.wiki/?search=" + Uri.EscapeDataString(article));
    }
}
