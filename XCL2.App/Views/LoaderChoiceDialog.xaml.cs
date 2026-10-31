using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class LoaderChoiceDialog : OverlayDialogControl
{
    /// <summary>用户确认选择的加载器类型；取消返回 null。</summary>
    public ServerCoreType SelectedLoader { get; private set; } = ServerCoreType.Vanilla;
    public string? SelectedPluginLoaderId { get; private set; }

    private readonly string _mcVersionId;
    private readonly ClientLoaderInstallService _probeService;

    public LoaderChoiceDialog(string mcVersionId)
    {
        InitializeComponent();
        _mcVersionId = mcVersionId;
        TitleText.Text = $"安装 {mcVersionId}";
        if (OptVanilla.Parent is Panel loaderChoices)
            foreach (var provider in Services.Plugins.PluginProviderRegistry.Snapshot("loader"))
            {
                var button = new Button { Content = Loc.F("Str_Plugin_LoaderChoice", "插件：{0}", provider.Name), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 8, 12, 8) };
                button.Click += (_, _) => { SelectedPluginLoaderId = provider.Id; CloseWith(true); };
                loaderChoices.Children.Add(button);
            }

        // 只是用来探测"支持不支持"，不需要跟着用户的下载源/多线程设置走，用最简单的默认构造即可。
        _probeService = new ClientLoaderInstallService(DownloadSource.Official);
        OverlayClosed += (_, _) => _probeService.Dispose();

        _ = LoadAvailabilityAsync();
    }

    private async Task LoadAvailabilityAsync()
    {
        Dictionary<ServerCoreType, bool> availability;
        try
        {
            availability = await LoaderAvailabilityService.GetAvailabilityAsync(_probeService, _mcVersionId);
        }
        catch
        {
            // 探测本身整体失败（理论上不会，GetAvailabilityAsync 内部每一项都已经吞过异常了，
            // 这里只是双重保险）：保持四个非主流选项原样置灰，不影响主流五项的正常使用。
            return;
        }

        ApplyAvailability(OptOptiFine, ServerCoreType.OptiFine, availability,
            "该版本没有查到 OptiFine 构建（可能是官方没有发布，或暂时无法访问数据源）。");
        ApplyAvailability(OptLiteLoader, ServerCoreType.LiteLoader, availability,
            "该版本没有 LiteLoader 构建（LiteLoader 早已停止更新，只覆盖较老的版本）。");
        ApplyAvailability(OptCleanroom, ServerCoreType.Cleanroom, availability,
            "Cleanroom 目前只支持 Minecraft 1.12.2。");
        ApplyAvailability(OptLabyMod, ServerCoreType.LabyMod, availability,
            "LabyMod 暂不支持在这里自动安装，请前往 LabyMod 官网手动下载。");
    }

    private void ApplyAvailability(RadioButton button, ServerCoreType type,
        Dictionary<ServerCoreType, bool> availability, string unsupportedHint)
    {
        var supported = availability.TryGetValue(type, out var ok) && ok;
        button.IsEnabled = supported;
        button.ToolTip = supported ? null : unsupportedHint;
        // 万一该选项正好是之前某次探测完打开、用户选中了，但这次重新探测发现不再支持
        // （理论上不会发生，因为每次都是新开对话框），保险起见置灰的同时也取消选中。
        if (!supported && button.IsChecked == true)
        {
            button.IsChecked = false;
            OptVanilla.IsChecked = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedLoader = OptVanilla.IsChecked == true ? ServerCoreType.Vanilla
            : OptFabric.IsChecked == true ? ServerCoreType.Fabric
            : OptForge.IsChecked == true ? ServerCoreType.Forge
            : OptNeoForge.IsChecked == true ? ServerCoreType.NeoForge
            : OptQuilt.IsChecked == true ? ServerCoreType.Quilt
            : OptOptiFine.IsChecked == true ? ServerCoreType.OptiFine
            : OptLiteLoader.IsChecked == true ? ServerCoreType.LiteLoader
            : OptCleanroom.IsChecked == true ? ServerCoreType.Cleanroom
            : OptLabyMod.IsChecked == true ? ServerCoreType.LabyMod
            : ServerCoreType.Vanilla;

        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
