using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

internal sealed partial class MiniWorldLauncherDialog : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly ListBox _sources = new() { BorderThickness = new Thickness(0), Padding = new Thickness(0) };
    private readonly StackPanel _detail = new();
    private readonly Dictionary<string, IReadOnlyList<MiniWorldVersion>> _versions = new();
    private readonly Dictionary<string, string> _selectedVersions = new();
    private readonly HashSet<string> _loading = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly Dictionary<string, MiniWorldSourceProbe> _availability = new();
    private readonly HashSet<string> _probing = new();
    private readonly Dictionary<string, DownloadQueueItem> _downloads = new();
    private readonly Dictionary<string, string> _installers = new();
    private readonly CancellationTokenSource _catalogCancellation = new();
    private volatile bool _closed;

    public MiniWorldLauncherDialog(MainWindow owner)
    {
        _owner = owner; Width = 970; MinHeight = 580; MaxHeight = 760;
        OverlayClosed += (_, _) => { _closed = true; _catalogCancellation.Cancel(); _catalogCancellation.Dispose(); };
        owner.ConfigService.Config.MiniWorldClientPaths ??= new();
        owner.ConfigService.Config.MiniWorldCommunityClients ??= new();
        owner.ConfigService.Config.MiniWorldSavedVersions ??= new();
        InitializeLocalInstallations();
        foreach (var source in MiniWorldCatalog.Sources.Where(source => source.VersionIndexUrl == null))
            _versions[source.Id] = MiniWorldVersionService.LoadAsync(source).GetAwaiter().GetResult();
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new TextBlock { Text = "迷你世界 · 版本下载与启动", FontSize = 24, FontWeight = FontWeights.Bold });
        var badge = Text(MiniWorldCatalog.ExperimentalNotice + " · 旧版本和社区版本分别保存", 12);
        badge.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush"); header.Children.Add(badge);
        var localActions = new WrapPanel();
        localActions.Children.Add(Button("本地客户端", () => SelectLocalClients()));
        localActions.Children.Add(Button("导入本地安装…", () => ImportLocalClient()));
        localActions.Children.Add(Button("检测已安装客户端", () => _ = DetectCommonClientsAsync()));
        header.Children.Add(localActions);
        header.Children.Add(new Expander { Header = "查看作者声明", Margin = new Thickness(0, 8, 0, 0), Content = Text(MiniWorldCatalog.Disclosure, 12) });
        root.Children.Add(header);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sources.SelectionChanged += (_, _) => RefreshDetail(); body.Children.Add(_sources);
        var detailScroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(detailScroll, 2); body.Children.Add(detailScroll); Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var close = Button("关闭", () => CloseWith(false)); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        var custom = Button("＋ 添加下载来源 / 社区端", AddCommunityClient); DockPanel.SetDock(custom, Dock.Left); footer.Children.Add(custom);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root; RefreshSources();
    }

    private static TextBlock Text(string text, double size = 13) => new()
    { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), LineHeight = size * 1.55 };

    private static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 8, 8) };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButton" : "SecondaryButton");
        button.Click += (_, _) => action(); return button;
    }

    private void RefreshSources(string? selectedId = null)
    {
        _sources.Items.Clear();
        AddSource(new MiniWorldSource("local-installed", "本地客户端", $"已保存 {_owner.ConfigService.Config.MiniWorldLocalInstallations.Count} 个安装", "无需联网即可管理和启动已安装的客户端。", ""));
        foreach (var source in MiniWorldCatalog.Sources) AddSource(source);
        foreach (var client in _owner.ConfigService.Config.MiniWorldCommunityClients.Where(c => c != null))
        {
            var id = "custom:" + client.Id;
            if (string.IsNullOrWhiteSpace(client.VersionIndexUrl)) _versions.TryAdd(id, Array.Empty<MiniWorldVersion>());
            AddSource(new MiniWorldSource(id, client.Name, "自定义 · " + client.Category, "由你添加的独立来源。可订阅版本目录、导入 JSON，或为每个版本添加多个镜像直链。", client.DownloadPageUrl,
                VersionIndexUrl: string.IsNullOrWhiteSpace(client.VersionIndexUrl) ? null : client.VersionIndexUrl));
        }
        _sources.SelectedItem = _sources.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is MiniWorldSource source && source.Id == selectedId)
            ?? _sources.Items.OfType<ListBoxItem>().FirstOrDefault();
    }

    private void AddSource(MiniWorldSource source)
    {
        var panel = new StackPanel { Margin = new Thickness(8, 10, 8, 10) };
        panel.Children.Add(new TextBlock { Text = source.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var subtitle = Text(source.Kind, 11); subtitle.Margin = new Thickness(0, 4, 0, 0);
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); panel.Children.Add(subtitle);
        _sources.Items.Add(new ListBoxItem { Content = panel, Tag = source, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 5) });
    }

    private IReadOnlyList<MiniWorldVersion> VersionsFor(MiniWorldSource source)
    {
        var result = (_versions.GetValueOrDefault(source.Id) ?? Array.Empty<MiniWorldVersion>()).ToList();
        foreach (var saved in _owner.ConfigService.Config.MiniWorldSavedVersions.Where(item => item != null && item.SourceId == source.Id))
            if ((MiniWorldVersionService.IsWebAddress(saved.DownloadUrl) || MiniWorldVersionService.IsWebAddress(saved.CdkRedeemUrl)) && MiniWorldVersionService.IsSupportedFileName(saved.FileName))
                result.Add(new MiniWorldVersion("saved:" + saved.Id, source.Id, saved.Version, saved.Name, "用户添加（" + source.Name + "）", saved.SourcePageUrl,
                    saved.DownloadUrl, saved.FileName, "用户提供的版本下载，请对照来源页核实发布者与版本。", saved.PublishedDate, Array.Empty<MiniWorldShare>())
                    { Mirrors = (saved.Mirrors ?? new()).Where(MiniWorldVersionService.IsWebAddress).ToArray(), Sha256 = saved.Sha256 ?? "", CdkRedeemUrl = saved.CdkRedeemUrl ?? "" });
        return result;
    }

    private void RefreshDetail()
    {
        if (_closed) return;
        _detail.Children.Clear();
        if ((_sources.SelectedItem as ListBoxItem)?.Tag is not MiniWorldSource source) return;
        if (source.Id == "local-installed") { RenderLocalClients(); return; }
        _detail.Children.Add(new TextBlock { Text = source.Name, FontSize = 21, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        _detail.Children.Add(Text(source.Description));
        var toolbar = new WrapPanel();
        if (ExternalLinkService.TryGetWebUri(source.HomeUrl, out _)) toolbar.Children.Add(Button("发布者来源页", () => ExternalLinkService.Open(source.HomeUrl)));
        toolbar.Children.Add(Button(source.VersionIndexUrl == null ? "刷新状态" : "刷新版本目录", () =>
        {
            if (source.VersionIndexUrl != null) _versions.Remove(source.Id);
            _errors.Remove(source.Id); _availability.Clear(); RefreshDetail();
        }));
        toolbar.Children.Add(Button("＋ 添加版本直链", () => AddVersion(source)));
        toolbar.Children.Add(Button("导入版本目录 JSON…", () => ImportVersionIndex(source))); _detail.Children.Add(toolbar);
        if (source.VersionIndexUrl != null && !_versions.ContainsKey(source.Id))
        {
            var message = _errors.TryGetValue(source.Id, out var error) ? error : "正在读取发布者的公开版本目录…";
            var hint = Text(message, 12); hint.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush"); _detail.Children.Add(hint);
            if (!_loading.Contains(source.Id) && !_errors.ContainsKey(source.Id)) _ = LoadVersionsAsync(source);
        }
        var versions = VersionsFor(source);
        var version = versions.FirstOrDefault(item => item.Id == _selectedVersions.GetValueOrDefault(source.Id)) ?? versions.FirstOrDefault();
        RenderLocalClient(source, version);
        if (version != null)
        {
            _selectedVersions[source.Id] = version.Id;
            var selector = new ListBox { MaxHeight = 175, Margin = new Thickness(0, 4, 0, 14) };
            foreach (var item in versions)
            {
                var row = new StackPanel(); row.Children.Add(new TextBlock { Text = item.Name + " · " + item.Version, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                var date = Text(item.DateLabel + " · " + item.Publisher, 11); date.Margin = new Thickness(0, 3, 0, 0); row.Children.Add(date);
                var option = new ListBoxItem { Content = row, Tag = item, Padding = new Thickness(8) }; selector.Items.Add(option);
                if (item.Id == version.Id) selector.SelectedItem = option;
            }
            selector.SelectionChanged += (_, _) => { if ((selector.SelectedItem as ListBoxItem)?.Tag is MiniWorldVersion chosen) { _selectedVersions[source.Id] = chosen.Id; RefreshDetail(); } };
            _detail.Children.Add(selector); RenderVersion(version);
        }
        else if (!_loading.Contains(source.Id)) _detail.Children.Add(Text("此来源没有公开可直接下载的版本目录。可前往发布者页面，或添加带版本号和来源页的下载直链。", 12));
        if (source.Id.StartsWith("custom:", StringComparison.Ordinal))
            _detail.Children.Add(Button("移除此来源记录", () =>
            {
                _owner.ConfigService.Config.MiniWorldCommunityClients.RemoveAll(c => "custom:" + c.Id == source.Id);
                _owner.ConfigService.Config.MiniWorldSavedVersions.RemoveAll(item => item.SourceId == source.Id);
                _owner.ConfigService.Save(); RefreshSources();
            }));
    }

    private async Task LoadVersionsAsync(MiniWorldSource source)
    {
        if (_closed || !_loading.Add(source.Id)) return;
        var ct = _catalogCancellation.Token;
        try { _versions[source.Id] = await MiniWorldVersionService.LoadAsync(source, ct); _errors.Remove(source.Id); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _errors[source.Id] = "读取版本目录失败：" + ex.Message + "。可点击刷新重试，或打开发布者来源页。"; }
        finally { _loading.Remove(source.Id); RequestDetailRefresh(); }
    }

    private void RenderVersion(MiniWorldVersion version)
    {
        _detail.Children.Add(Text(version.Version + " · " + version.DateLabel + "\n收录来源：" + version.Publisher, 12));
        if (version.Mirrors.Count > 0)
            _detail.Children.Add(Text($"主来源与 {version.Mirrors.Count} 个备用镜像自动回退" + (version.Sha256.Length > 0 ? " · 校验发布者 SHA-256" : " · 校验文件格式与传输完整性"), 12));
        if (version.Description.Length > 0) _detail.Children.Add(new Expander { Header = "版本介绍（发布者提供）", Content = Text(version.Description, 12), Margin = new Thickness(0, 0, 0, 10) });
        var state = Text(_availability.TryGetValue(version.ClientKey, out var probe) ? probe.Message : "正在检查主来源是否返回实际文件…", 12);
        state.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush"); _detail.Children.Add(state);
        if (!_availability.ContainsKey(version.ClientKey) && _probing.Add(version.ClientKey)) _ = ProbeVersionAsync(version);
        var actions = new WrapPanel();
        if (MiniWorldVersionService.IsWebAddress(version.DownloadUrl)) actions.Children.Add(Button(version.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "下载此版本安装包" : "下载此版本压缩包", () => Download(version), true));
        actions.Children.Add(Button("CDK / 直链下载…", () =>
        {
            if (FindActiveDownload(version) != null) { ToastService.ShowInfo("该版本已有下载任务，请先在下载管理中查看，避免重复兑换 CDK。"); return; }
            var dialog = new MiniWorldDirectDownloadDialog(version);
            if (dialog.ShowDialog() == true && dialog.DownloadVersion is { } target) Download(target);
        }, version.CdkRedeemUrl.Length > 0));
        if (ExternalLinkService.TryGetWebUri(version.SourcePageUrl, out _)) actions.Children.Add(Button("版本来源页", () => ExternalLinkService.Open(version.SourcePageUrl)));
        if (version.FileName.Length == 0 && ExternalLinkService.TryGetWebUri(version.DownloadUrl, out _)) actions.Children.Add(Button("主分享页", () => ExternalLinkService.Open(version.DownloadUrl)));
        _detail.Children.Add(actions);
        foreach (var share in version.PublicShares)
        {
            var shareRow = new WrapPanel(); shareRow.Children.Add(Button("备用分享页", () => ExternalLinkService.Open(share.Url)));
            if (share.ExtractionCode.Length > 0)
            {
                shareRow.Children.Add(Text("提取码：" + share.ExtractionCode, 12));
                shareRow.Children.Add(Button("复制提取码", () => { Clipboard.SetText(share.ExtractionCode); ToastService.ShowInfo("提取码已复制"); }));
            }
            _detail.Children.Add(shareRow);
        }
        if (version.SourceId == "cbl-community" && version.Name.Contains("星辉阁", StringComparison.Ordinal))
            _detail.Children.Add(Button("星辉阁发布方备用页", () => ExternalLinkService.Open("https://xinghuige.cn/1869.html")));
        else if (version.SourceId == "cbl-community" && version.Name.Contains("XHG", StringComparison.OrdinalIgnoreCase))
            _detail.Children.Add(Button("XHG 发布方说明页（PC 信息见正文）", () => ExternalLinkService.Open("https://xinghuige.cn/1465.html")));
        if (version.Id.StartsWith("saved:", StringComparison.Ordinal))
            _detail.Children.Add(Button("移除此版本记录", () =>
            {
                _owner.ConfigService.Config.MiniWorldSavedVersions.RemoveAll(item => "saved:" + item.Id == version.Id && item.SourceId == version.SourceId);
                _owner.ConfigService.Save(); RefreshDetail();
            }));
        _detail.Children.Add(Text("压缩包需手动解压；分卷文件须全部下载并放在同一目录。安装或解压后为当前版本单独绑定 EXE。遇到网盘验证时，请在备用分享页完成下载。", 12));
        var existing = DownloadQueueService.Instance.Items.FirstOrDefault(item => item.Name == version.QueueName);
        if (existing != null) _downloads[version.ClientKey] = existing;
        var directory = VersionDirectory(version);
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any(path => MiniWorldVersionService.IsSupportedFileName(Path.GetFileName(path))))
            _detail.Children.Add(Button("打开此版本下载目录", () => OpenDirectory(directory)));
        var savedInstaller = Path.Combine(directory, version.FileName);
        var installer = _installers.GetValueOrDefault(version.ClientKey) ?? savedInstaller;
        if (version.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(installer))
        {
            var installActions = new WrapPanel();
            installActions.Children.Add(Button("运行已下载安装包", () => LaunchExecutable(installer)));
            installActions.Children.Add(Button("安装完成，关联本地游戏…", () => ImportLocalClient(MiniWorldCatalog.Sources.FirstOrDefault(item => item.Id == version.SourceId), version)));
            _detail.Children.Add(installActions);
        }
        if (_downloads.TryGetValue(version.ClientKey, out var task))
        {
            var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12), DataContext = task };
            status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DownloadQueueItem.StatusText))); _detail.Children.Add(status);
        }
    }

    private async Task ProbeVersionAsync(MiniWorldVersion version)
    {
        if (_closed) { _probing.Remove(version.ClientKey); return; }
        var ct = _catalogCancellation.Token;
        try { _availability[version.ClientKey] = await MiniWorldVersionService.ProbeAsync(version, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _availability[version.ClientKey] = new(false, "检查下载来源失败：" + ex.Message); }
        finally { _probing.Remove(version.ClientKey); RequestDetailRefresh(); }
    }

    private void RequestDetailRefresh()
    {
        if (_closed || Dispatcher.HasShutdownStarted || !OverlayDialogService.IsOpen(this)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed && !Dispatcher.HasShutdownStarted && OverlayDialogService.IsOpen(this)) RefreshDetail();
        }));
    }

    private void RenderLocalClient(MiniWorldSource source, MiniWorldVersion? version)
    {
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(18), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 16) };
        card.SetResourceReference(Border.BackgroundProperty, "GlowSoftBrush"); card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = version == null ? "启动本地客户端" : "启动此版本 · " + version.Version, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });
        var installation = FindLocalInstallation(source, version);
        panel.Children.Add(Text(installation == null ? "先导入已安装/已解压的游戏程序；下载包本身不能直接当作已安装客户端。" : installation.Name + "\n" + installation.ExecutablePath, 12));
        var actions = new WrapPanel();
        var launch = Button("▶ 启动", () => { if (installation != null) _ = LaunchInstallationAsync(installation); }, true);
        launch.IsEnabled = installation != null && File.Exists(installation.ExecutablePath) && !_launching.Contains(installation.Id); actions.Children.Add(launch);
        actions.Children.Add(Button(installation == null ? "选择游戏 EXE…" : "编辑启动设置…", () => { if (installation == null) ImportLocalClient(source, version); else EditLocalInstallation(installation); }));
        actions.Children.Add(Button("从安装/解压目录识别…", () => _ = ScanLocalDirectoryAsync(source, version)));
        panel.Children.Add(actions);
        if (installation != null)
        {
            panel.Children.Add(Text("工作目录：" + installation.WorkingDirectory + "\n参数：" + (installation.Arguments?.Count > 0 ? string.Join(" · ", installation.Arguments) : "无"), 11));
            if (!File.Exists(installation.ExecutablePath)) panel.Children.Add(Text("启动文件已移动或丢失，请编辑设置重新选择。", 12));
        }
        else if (version == null && source.VersionIndexUrl != null)
            panel.Children.Add(Text("在线目录读取期间也可导入本地安装。导入结果保存在独立的本地客户端列表；绑定指定版本需先选中该版本。", 11));
        card.Child = panel; _detail.Children.Add(card);
    }

    private static string VersionDirectory(MiniWorldVersion version) => Path.Combine(App.DataDir, "mini-world", "downloads", version.DownloadDirectoryKey);

    private static DownloadQueueItem? FindActiveDownload(MiniWorldVersion version)
        => DownloadQueueService.Instance.Items.FirstOrDefault(item => item.Name == version.QueueName &&
            item.Status is DownloadQueueItemStatus.Downloading or DownloadQueueItemStatus.Waiting or DownloadQueueItemStatus.Paused or DownloadQueueItemStatus.Pausing or DownloadQueueItemStatus.Canceling);

    private void Download(MiniWorldVersion version)
    {
        var running = FindActiveDownload(version);
        if (running != null) { _downloads[version.ClientKey] = running; ToastService.ShowInfo("该版本已有下载任务，请在下载管理中查看。"); return; }
        _downloads[version.ClientKey] = DownloadQueueService.Instance.StartNew(version.QueueName, async (item, ct) =>
        {
            var downloads = new List<(string Url, string FileName)>();
            var probe = version.SkipDownloadProbe
                ? new MiniWorldSourceProbe(true, "使用提供的直链直接下载并校验文件。")
                : await MiniWorldVersionService.ProbeAsync(version, ct);
            if (probe.Available || version.Mirrors.Count > 0) downloads.Add((version.DownloadUrl, version.FileName));
            else
            {
                if (version.PublicShares.Count == 0) throw new InvalidOperationException(probe.Message + " 请打开此版本来源页下载。");
                item.StatusText = "主链接暂不可用，正在读取发布者的公开备用下载…";
                foreach (var share in version.PublicShares)
                {
                    var resolved = await MiniWorldPublicShareResolver.ResolveAsync(share.Url, share.ExtractionCode, ct);
                    downloads.Add((resolved.Url, resolved.FileName));
                }
            }
            var directory = VersionDirectory(version);
            var staging = Path.Combine(directory, ".staging", item.Id.ToString("N"));
            using var downloader = new GenericFileDownloadService();
            try
            {
                foreach (var asset in downloads)
                {
                    if (!MiniWorldVersionService.IsSupportedFileName(asset.FileName)) throw new InvalidDataException("来源返回的文件名或格式不受支持，请打开分享页下载。");
                    item.StatusText = "正在下载 " + asset.FileName;
                    var urls = asset.Url == version.DownloadUrl ? new[] { asset.Url }.Concat(version.Mirrors).ToArray() : new[] { asset.Url };
                    var staged = await downloader.DownloadAsync(urls, staging, asset.FileName, MiniWorldPublicShareResolver.BrowserUserAgent, item.CreateProgress(), ct,
                        expectedSha256: asset.Url == version.DownloadUrl ? version.Sha256 : null,
                        validateFile: path => MiniWorldVersionService.ValidateDownloadedPackage(path, asset.FileName));
                    MiniWorldVersionService.ValidateDownloadedPackage(staged); ct.ThrowIfCancellationRequested();
                    var path = Path.Combine(directory, asset.FileName); File.Move(staged, path, overwrite: true);
                    if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !_closed && !_owner.Dispatcher.HasShutdownStarted)
                        await _owner.Dispatcher.InvokeAsync(() => { if (!_closed) _installers[version.ClientKey] = path; });
                }
            }
            finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            item.CompletionText = downloads.All(asset => asset.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ? "此版本安装包已下载；请手动安装，再为该版本绑定 EXE"
                : "此版本压缩包已下载；请手动解压（分卷需放在一起），再为该版本绑定 EXE";
            if (!_closed && !_owner.Dispatcher.HasShutdownStarted) await _owner.Dispatcher.InvokeAsync(RequestDetailRefresh);
        }, originPage: "实验性功能 · 迷你世界 · " + version.Version);
        RefreshDetail();
    }

    private static void LaunchExecutable(string? path)
    {
        try { var executable = MiniWorldCatalog.ValidateExecutable(path); Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! }); }
        catch (Exception ex) { MessageBoxDialog.ShowWarning("无法启动：" + ex.Message, "迷你世界"); }
    }

    private static void OpenDirectory(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBoxDialog.ShowWarning("无法打开目录：" + ex.Message); }
    }

    private void AddCommunityClient()
    {
        var dialog = new MiniWorldCommunityDialog(); if (dialog.ShowDialog() != true || dialog.Client == null) return;
        _owner.ConfigService.Config.MiniWorldCommunityClients.Add(dialog.Client); _owner.ConfigService.Save(); InitializeLocalInstallations(); RefreshSources("custom:" + dialog.Client.Id);
    }

    private void AddVersion(MiniWorldSource source)
    {
        var dialog = new MiniWorldVersionDialog(source); if (dialog.ShowDialog() != true || dialog.Version == null) return;
        _owner.ConfigService.Config.MiniWorldSavedVersions.Add(dialog.Version); _owner.ConfigService.Save();
        _selectedVersions[source.Id] = "saved:" + dialog.Version.Id; RefreshDetail();
    }

    private void ImportVersionIndex(MiniWorldSource source)
    {
        var picker = new OpenFileDialog { Filter = "版本目录 (*.json)|*.json", CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 2 * 1024 * 1024) throw new InvalidDataException("版本目录不得超过 2 MB。");
            var versions = MiniWorldVersionService.ParseIndex(File.ReadAllText(picker.FileName), source);
            var saved = _owner.ConfigService.Config.MiniWorldSavedVersions;
            foreach (var version in versions)
            {
                var id = "import-" + MiniWorldCatalog.ClientKey(source.Id, version.Id).Split(':')[1];
                saved.RemoveAll(item => item.SourceId == source.Id && item.Id == id);
                saved.Add(new MiniWorldSavedVersion { Id = id, SourceId = source.Id, Name = version.Name, Version = version.Version,
                    SourcePageUrl = version.SourcePageUrl, DownloadUrl = version.DownloadUrl, FileName = version.FileName,
                    PublishedDate = version.PublishedDate ?? "", Mirrors = version.Mirrors.ToList(), Sha256 = version.Sha256, CdkRedeemUrl = version.CdkRedeemUrl });
            }
            _owner.ConfigService.Save(); RefreshDetail(); ToastService.ShowInfo($"已导入 {versions.Count} 个版本。");
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning("目录导入失败：" + ex.Message, "迷你世界"); }
    }
}

internal sealed class MiniWorldCommunityDialog : OverlayDialogControl
{
    public MiniWorldCommunityClient? Client { get; private set; }
    public MiniWorldCommunityDialog()
    {
        Width = 540; MaxHeight = 620; var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "添加下载来源 / 社区端", FontSize = 21, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "分别保存社区名称、下载页和本地启动程序。已有社区端可直接选择 EXE；其他客户端安装后再选择。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "来源名称" }); var name = new TextBox { MaxLength = 80, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(8) }; panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "来源分类" });
        var category = new ComboBox { ItemsSource = new[] { "社区版本", "镜像站", "官方版本", "历史版本", "其他来源" }, SelectedIndex = 0, Margin = new Thickness(0, 6, 0, 12) }; panel.Children.Add(category);
        panel.Children.Add(new TextBlock { Text = "下载页（选填，http / https）" }); var url = new TextBox { MaxLength = 2048, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(8) }; panel.Children.Add(url);
        panel.Children.Add(new TextBlock { Text = "版本目录 JSON 地址（选填，https）" }); var index = new TextBox { MaxLength = 2048, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(8) }; panel.Children.Add(index);
        var path = new TextBlock { Text = "尚未选择本地程序", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
        string executable = "";
        var browse = new Button { Content = "选择已安装的 EXE（选填）", Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += (_, _) => { var picker = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true }; if (picker.ShowDialog() == true) { executable = picker.FileName; path.Text = executable; } };
        panel.Children.Add(browse); panel.Children.Add(path);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; error.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush"); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => CloseWith(false); actions.Children.Add(cancel);
        var save = new Button { Content = "添加", Padding = new Thickness(16, 8, 16, 8) }; save.SetResourceReference(Button.StyleProperty, "PrimaryButton");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "请输入社区名称。"; return; }
            if (!string.IsNullOrWhiteSpace(url.Text) && !ExternalLinkService.TryGetWebUri(url.Text, out _)) { error.Text = "下载页必须是有效的 http / https 网页。"; return; }
            if (!string.IsNullOrWhiteSpace(index.Text) && !MiniWorldVersionService.IsWebAddress(index.Text.Trim())) { error.Text = "版本目录必须是有效的 https 地址。"; return; }
            if (string.IsNullOrWhiteSpace(url.Text) && string.IsNullOrWhiteSpace(executable) && string.IsNullOrWhiteSpace(index.Text)) { error.Text = "请填写下载页、版本目录或选择本地程序。"; return; }
            Client = new MiniWorldCommunityClient { Name = name.Text.Trim(), DownloadPageUrl = url.Text.Trim(), ExecutablePath = executable,
                Category = category.SelectedItem as string ?? "社区版本", VersionIndexUrl = index.Text.Trim() }; CloseWith(true);
        };
        actions.Children.Add(save); panel.Children.Add(actions); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

internal sealed class MiniWorldVersionDialog : OverlayDialogControl
{
    public MiniWorldSavedVersion? Version { get; private set; }
    public MiniWorldVersionDialog(MiniWorldSource source)
    {
        Width = 600; MaxHeight = 720; var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "添加有来源的版本下载", FontSize = 21, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "来源分类：" + source.Name + "。请填写发布者公开的程序或压缩包直链，网页、网盘分享页不能填写为文件直链。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        TextBox Field(string label, string value = "")
        {
            panel.Children.Add(new TextBlock { Text = label });
            var control = new TextBox { Text = value, MaxLength = 2048, Padding = new Thickness(8), Margin = new Thickness(0, 5, 0, 10) }; panel.Children.Add(control); return control;
        }
        var name = Field("版本名称", source.Name); name.MaxLength = 100;
        var number = Field("版本号（例如 0.52.1 或 社区版 3.0）"); number.MaxLength = 100;
        var date = Field("发布日期（选填，YYYY-MM-DD；留空显示来源未提供）"); date.MaxLength = 10;
        var page = Field("发布者来源页（https）", source.HomeUrl);
        var url = Field("文件直链（https；仅 CDK 版本可留空）");
        var cdk = Field("CDK 兑换接口（选填，发布者提供的 https JSON 接口）");
        var fileName = Field("保存文件名（exe / zip / 7z；可从直链推导）"); fileName.MaxLength = 240;
        var mirrors = Field("备用镜像直链（选填，每行一个，同一版本同一文件）"); mirrors.AcceptsReturn = true; mirrors.Height = 75; mirrors.MaxLength = 32768; mirrors.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var sha256 = Field("SHA-256（选填，由发布者提供）"); sha256.MaxLength = 64;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; error.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush"); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => CloseWith(false); actions.Children.Add(cancel);
        var save = new Button { Content = "添加版本", Padding = new Thickness(16, 8, 16, 8) }; save.SetResourceReference(Button.StyleProperty, "PrimaryButton");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(number.Text)) { error.Text = "请填写版本名称和版本号。"; return; }
            if (!MiniWorldVersionService.IsWebAddress(page.Text.Trim())
                || (url.Text.Trim().Length > 0 && !MiniWorldVersionService.IsWebAddress(url.Text.Trim()))
                || (cdk.Text.Trim().Length > 0 && !MiniWorldVersionService.IsWebAddress(cdk.Text.Trim()))
                || (url.Text.Trim().Length == 0 && cdk.Text.Trim().Length == 0))
            { error.Text = "请填写 HTTPS 来源页，并提供有效的文件直链或 CDK 兑换接口。"; return; }
            var target = fileName.Text.Trim(); if (target.Length == 0 && MiniWorldVersionService.IsWebAddress(url.Text.Trim())) target = Path.GetFileName(new Uri(url.Text.Trim()).LocalPath);
            if (!MiniWorldVersionService.IsSupportedFileName(target)) { error.Text = "请填写不含路径的有效 exe / zip / 7z 文件名。"; return; }
            var backup = mirrors.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
            if (backup.Count > 16 || backup.Any(value => !MiniWorldVersionService.IsWebAddress(value))) { error.Text = "备用镜像必须是 https 地址，最多 16 个。"; return; }
            var hash = sha256.Text.Trim();
            if (hash.Length != 0 && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) { error.Text = "SHA-256 必须是 64 位十六进制值。"; return; }
            var published = date.Text.Trim();
            if (published.Length > 0 && !DateOnly.TryParseExact(published, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) { error.Text = "发布日期应为 YYYY-MM-DD，或留空。"; return; }
            Version = new MiniWorldSavedVersion { SourceId = source.Id, Name = name.Text.Trim(), Version = number.Text.Trim(), SourcePageUrl = page.Text.Trim(), DownloadUrl = url.Text.Trim(), FileName = target, PublishedDate = published, Mirrors = backup, Sha256 = hash, CdkRedeemUrl = cdk.Text.Trim() }; CloseWith(true);
        };
        actions.Children.Add(save); panel.Children.Add(actions); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
