using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>基岩版页面按需打开的市场。查询随页面取消；已入队的下载独立继续。</summary>
public partial class BedrockMarketplacePage : UserControl
{
    private readonly Action _onBack;
    private readonly BedrockMarketplaceService _service = new();
    private readonly CancellationTokenSource _pageLifetime = new();
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _filesCancellation;
    private int _searchGeneration;
    private int _filesGeneration;
    private bool _started;
    private bool _disposed;
    private bool _searching;
    private bool _loadingFiles;
    private bool _updatingFilters;
    private BedrockMarketSearchOptions _searchOptions = new();
    private IReadOnlyList<BedrockMarketFile> _loadedFiles = Array.Empty<BedrockMarketFile>();
    private IReadOnlyList<BedrockMarketPublicationPage> _publicationPages = Array.Empty<BedrockMarketPublicationPage>();
    private bool _hasSelectedProject;
    private int _page = 1;
    private int _totalPages = 1;
    private string? _lastSaveDirectory;

    public BedrockMarketplacePage(string entryContext, Action onBack)
    {
        _onBack = onBack;
        InitializeComponent();
        EntryContextText.Text = $"从基岩版{entryContext}进入 · 社区资源";
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack,
            (_, e) => { e.Handled = true; ReturnToBedrock(); },
            (_, e) => { e.CanExecute = !_disposed; e.Handled = true; }));
        InputBindings.Add(new KeyBinding(NavigationCommands.BrowseBack, new KeyGesture(Key.Left, ModifierKeys.Alt)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find,
            (_, e) => { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, new KeyGesture(Key.F, ModifierKeys.Control)));
        PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.XButton1) return;
            e.Handled = true;
            ReturnToBedrock();
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) => DisposePage();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || _disposed) return;
        _started = true;
        SearchBox.Focus();
        RefreshSearchClearButton();
        ApplyFileFilters();
        await LoadProjectsAsync(ReadSearchOptions("", 1));
    }

    private void ReturnToBedrock()
    {
        if (_disposed) return;
        DisposePage();
        _onBack();
    }

    private void DisposePage()
    {
        if (_disposed) return;
        _disposed = true;
        _searchGeneration++;
        _filesGeneration++;
        CancelAndDispose(ref _searchCancellation);
        CancelAndDispose(ref _filesCancellation);
        _pageLifetime.Cancel();
        _pageLifetime.Dispose();
        _service.Dispose();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var old = source;
        source = null;
        old?.Cancel();
        old?.Dispose();
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
        => await LoadProjectsAsync(ReadSearchOptions(SearchBox.Text.Trim(), 1));

    private async void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
        await LoadProjectsAsync(ReadSearchOptions("", 1));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_started && !_disposed) RefreshSearchClearButton();
    }

    private void RefreshSearchClearButton()
    {
        // 模板可在无搜索词时隐藏清空按钮；不依赖可选装饰元素的生成字段。
        if (FindName("SearchClearButton") is Button clear)
            clear.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        if (e.Key == Key.Escape) SearchBox.Clear();
        await LoadProjectsAsync(ReadSearchOptions(SearchBox.Text.Trim(), 1));
    }

    private async void ProjectFilters_Changed(object sender, RoutedEventArgs e)
    {
        if (!_started || _disposed || _updatingFilters) return;
        await LoadProjectsAsync(ReadSearchOptions(SearchBox.Text.Trim(), 1));
    }

    private async void ResetProjectFilters_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _updatingFilters = true;
        try
        {
            CategoryFilter.SelectedIndex = 0;
            StatusFilter.SelectedIndex = 0;
            ProjectSortFilter.SelectedIndex = 0;
            FuzzySearchFilter.IsChecked = false;
        }
        finally { _updatingFilters = false; }
        await LoadProjectsAsync(ReadSearchOptions(SearchBox.Text.Trim(), 1));
    }

    private BedrockMarketSearchOptions ReadSearchOptions(string query, int page) => new()
    {
        Query = query,
        Page = page,
        Category = int.TryParse(SelectedTag(CategoryFilter), out var category) ? category : 0,
        Status = int.TryParse(SelectedTag(StatusFilter), out var status) ? status : 0,
        Sort = Enum.TryParse<BedrockMarketSort>(SelectedTag(ProjectSortFilter), out var sort) ? sort : BedrockMarketSort.Default,
        FuzzySearch = FuzzySearchFilter.IsChecked == true
    };

    private BedrockMarketSearchOptions SearchPage(int page) => new()
    {
        Query = _searchOptions.Query,
        Page = page,
        Category = _searchOptions.Category,
        Status = _searchOptions.Status,
        Sort = _searchOptions.Sort,
        FuzzySearch = _searchOptions.FuzzySearch
    };

    private static string SelectedTag(ComboBox control)
        => (control.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

    private void FileFilters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_started || _disposed || _updatingFilters) return;
        ApplyFileFilters();
    }

    private void FileVersionFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_started || _disposed || _updatingFilters) return;
        ApplyFileFilters();
    }

    private void ResetFileFilters_Click(object sender, RoutedEventArgs e)
    {
        _updatingFilters = true;
        try
        {
            SourceFilter.SelectedIndex = 0;
            FileTypeFilter.SelectedIndex = 0;
            FileSortFilter.SelectedIndex = 0;
            GameVersionFilter.Clear();
        }
        finally { _updatingFilters = false; }
        ApplyFileFilters();
    }

    private void ApplyFileFilters()
    {
        var selectedUrl = (FileList.SelectedItem as BedrockMarketFile)?.DownloadUrl;
        var filtered = BedrockMarketFileFilter.Apply(_loadedFiles, SelectedTag(SourceFilter),
            SelectedTag(FileTypeFilter), GameVersionFilter.Text, SelectedTag(FileSortFilter));
        FileList.ItemsSource = filtered;
        FileList.SelectedItem = selectedUrl == null ? null
            : filtered.FirstOrDefault(file => file.DownloadUrl == selectedUrl);
        FileFilterStatusText.Text = !_hasSelectedProject ? "选择资源后，可筛选已加载的文件"
            : _loadingFiles ? "正在加载文件…"
            : $"已加载文件：显示 {filtered.Count} / {_loadedFiles.Count} 个" +
                (GameVersionFilter.Text.Trim().Length > 0 ? " · 仅匹配作者已标注的游戏版本" : "");
        FileEmptyState.Visibility = _hasSelectedProject && !_loadingFiles && filtered.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (!_searching && _page > 1) await LoadProjectsAsync(SearchPage(_page - 1));
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!_searching && _page < _totalPages) await LoadProjectsAsync(SearchPage(_page + 1));
    }

    private void CancelLoading_Click(object sender, RoutedEventArgs e)
    {
        _searchCancellation?.Cancel();
        _filesCancellation?.Cancel();
    }

    private async Task LoadProjectsAsync(BedrockMarketSearchOptions options)
    {
        if (_disposed) return;
        CancelAndDispose(ref _searchCancellation);
        CancelAndDispose(ref _filesCancellation);
        var generation = ++_searchGeneration;
        ++_filesGeneration;
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var ct = cancellation.Token;
        _searchOptions = options;
        var query = options.Query;
        var page = options.Page;
        _searching = true;
        _loadingFiles = false;
        _page = page;
        _totalPages = page;
        ProjectList.ItemsSource = null;
        ProjectEmptyState.Visibility = Visibility.Collapsed;
        ResetProjectDetails();
        SearchStatusText.Text = string.IsNullOrWhiteSpace(query) ? "正在加载基岩版资源…" : $"正在搜索“{query}”…";
        PageText.Text = $"第 {page} 页";
        RefreshLoadingState();

        try
        {
            var result = await _service.SearchAsync(options, ct);
            if (_disposed || generation != _searchGeneration) return;
            ct.ThrowIfCancellationRequested();
            _page = Math.Max(1, result.Page);
            _totalPages = Math.Max(1, result.TotalPages);
            ProjectList.ItemsSource = result.Projects;
            ProjectEmptyState.Visibility = result.Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchStatusText.Text = result.Projects.Count == 0
                ? "没有找到符合搜索与筛选条件的基岩版资源。"
                : $"本页 {result.Projects.Count} 项 · 筛选结果共 {result.TotalCount} 项";
            PageText.Text = $"{_page} / {_totalPages}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!_disposed && generation == _searchGeneration) SearchStatusText.Text = "已取消加载，可重新搜索。";
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _searchGeneration) return;
            SearchStatusText.Text = "加载失败：" + ex.Message + " 可点击搜索重试。";
            LauncherLogService.AppendLine("[基岩版市场] 查询失败：" + ex.Message);
        }
        finally
        {
            if (!_disposed && generation == _searchGeneration)
            {
                _searching = false;
                RefreshLoadingState();
            }
            if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ResetProjectDetails()
    {
        _hasSelectedProject = false;
        _loadedFiles = Array.Empty<BedrockMarketFile>();
        _publicationPages = Array.Empty<BedrockMarketPublicationPage>();
        ProjectHeaderPanel.DataContext = null;
        ApplyFileFilters();
        ProjectNameText.Text = "选择一个资源";
        ProjectSummaryText.Text = "选择左侧项目后，可查看可下载的版本和文件。";
        FileNoticeText.Text = "";
        FileSizeText.Text = "";
        DownloadStatusText.Text = "";
        DownloadButton.IsEnabled = false;
        RetryFilesButton.Visibility = Visibility.Collapsed;
        RefreshPublicationPageButton();
        OpenEncyclopediaPageButton.IsEnabled = false;
    }

    private async void ProjectPublicationPage_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_disposed || sender is not Button button || button.DataContext is not BedrockMarketProject project ||
            !project.CanOpenPublicationPage) return;
        var generation = _searchGeneration;
        var unavailable = false;
        // Keep state on the item so recycled controls cannot enable a second open action.
        project.SetPublicationPageState(resolving: true);
        try
        {
            var pages = await _service.GetPublicationPagesAsync(project, _pageLifetime.Token);
            if (_disposed || generation != _searchGeneration) return;
            var page = pages.FirstOrDefault();
            if (page == null)
            {
                unavailable = true;
                ToastService.ShowInfo("此资源未提供可识别的发布页面，可在资源详情中打开百科页面查看其他来源。");
                return;
            }
            ExternalLinkService.Open(page.Url);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed && generation == _searchGeneration) ToastService.ShowWarning("无法读取发布页面：" + ex.Message);
        }
        finally
        {
            project.SetPublicationPageState(resolving: false, unavailable: unavailable);
        }
    }

    private void RefreshPublicationPageButton()
    {
        var file = FileList.SelectedItem as BedrockMarketFile;
        var release = file == null ? "" : BedrockMarketplaceParser.PublicationPageUrl(file.ReleasePageUrl, file.Source);
        var page = _publicationPages.FirstOrDefault(value => value.Source == file?.Source) ?? _publicationPages.FirstOrDefault();
        var url = release.Length > 0 ? release : page?.Url;
        OpenPublicationPageButton.Tag = url;
        OpenPublicationPageButton.IsEnabled = ExternalLinkService.TryGetWebUri(url, out _);
        OpenPublicationPageButton.Content = "打开发布页面";
        OpenPublicationPageButton.ToolTip = release.Length > 0 ? "在浏览器中打开所选版本的发布网页：" + release
            : page == null ? "此资源尚未提供可识别的发布网页。" : "在浏览器中打开 " + page.Source + " 项目网页：" + page.Url;
        PublicationPageStatusText.Text = !_hasSelectedProject ? ""
            : release.Length > 0 ? "所选版本的发布页 · " + file!.Source
            : page != null ? page.Source + " 项目发布页"
            : _loadingFiles ? "正在查找发布页面…" : "未提供可识别的发布页面，可查看百科中的其他来源。";
    }

    private void OpenPublicationPage_Click(object sender, RoutedEventArgs e)
    {
        if (!_disposed && OpenPublicationPageButton.IsEnabled && OpenPublicationPageButton.Tag is string url)
            ExternalLinkService.Open(url);
    }

    private void OpenEncyclopediaPage_Click(object sender, RoutedEventArgs e)
    {
        if (!_disposed && ProjectHeaderPanel.DataContext is BedrockMarketProject project &&
            ExternalLinkService.TryGetWebUri(project.PageUrl, out _)) ExternalLinkService.Open(project.PageUrl);
    }

    private async void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectList.SelectedItem is BedrockMarketProject project) await LoadFilesAsync(project);
    }

    private async void RetryFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is BedrockMarketProject project) await LoadFilesAsync(project);
    }

    private async Task LoadFilesAsync(BedrockMarketProject project)
    {
        if (_disposed) return;
        CancelAndDispose(ref _filesCancellation);
        var generation = ++_filesGeneration;
        var cancellation = new CancellationTokenSource();
        _filesCancellation = cancellation;
        var ct = cancellation.Token;
        _loadingFiles = true;
        _hasSelectedProject = true;
        _loadedFiles = Array.Empty<BedrockMarketFile>();
        _publicationPages = Array.Empty<BedrockMarketPublicationPage>();
        ProjectHeaderPanel.DataContext = project;
        OpenEncyclopediaPageButton.IsEnabled = ExternalLinkService.TryGetWebUri(project.PageUrl, out _);
        ApplyFileFilters();
        ProjectNameText.Text = project.Name;
        ProjectSummaryText.Text = string.IsNullOrWhiteSpace(project.Summary)
            ? project.EnglishName : project.Summary;
        FileNoticeText.Text = "正在查找可下载的版本与文件…";
        FileSizeText.Text = "";
        DownloadStatusText.Text = "";
        DownloadButton.IsEnabled = false;
        RetryFilesButton.Visibility = Visibility.Collapsed;
        RefreshLoadingState();
        RefreshPublicationPageButton();

        try
        {
            var publicationPages = await _service.GetPublicationPagesAsync(project, ct);
            if (_disposed || generation != _filesGeneration) return;
            ct.ThrowIfCancellationRequested();
            _publicationPages = publicationPages;
            project.SetPublicationPageState(project.IsResolvingPublicationPage, publicationPages.Count == 0);
            RefreshPublicationPageButton();
            var result = await _service.GetFilesAsync(project, ct);
            if (_disposed || generation != _filesGeneration) return;
            ct.ThrowIfCancellationRequested();
            _loadedFiles = result.Files;
            FileNoticeText.Text = !string.IsNullOrWhiteSpace(result.Notice) ? result.Notice
                : result.Files.Count == 0 ? "该项目暂时没有可直接下载的公开包文件。"
                : $"找到 {result.Files.Count} 个文件，选择需要的版本。";
            RetryFilesButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!_disposed && generation == _filesGeneration)
            {
                FileNoticeText.Text = "已取消文件加载。";
                RetryFilesButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _filesGeneration) return;
            FileNoticeText.Text = "文件加载失败：" + ex.Message;
            RetryFilesButton.Visibility = Visibility.Visible;
            LauncherLogService.AppendLine("[基岩版市场] 文件查询失败：" + ex.Message);
        }
        finally
        {
            if (!_disposed && generation == _filesGeneration)
            {
                _loadingFiles = false;
                ApplyFileFilters();
                RefreshLoadingState();
                RefreshPublicationPageButton();
            }
            if (ReferenceEquals(_filesCancellation, cancellation)) _filesCancellation = null;
            cancellation.Dispose();
        }
    }

    private void RefreshLoadingState()
    {
        var busy = _searching || _loadingFiles;
        LoadingProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelLoadingButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.IsEnabled = !_searching && _page > 1;
        NextButton.IsEnabled = !_searching && _page < _totalPages;
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var file = FileList.SelectedItem as BedrockMarketFile;
        DownloadButton.IsEnabled = file != null;
        FileSizeText.Text = file == null ? "" : $"{FormatSize(file.Size)} · {file.Source}";
        DownloadStatusText.Text = "";
        RefreshPublicationPageButton();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "大小未知",
        < 1024 => $"{bytes} 字节",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MiB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.#} GiB"
    };

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || FileList.SelectedItem is not BedrockMarketFile selected) return;
        var dialog = new XCL2.App.Services.CompatibleFolderDialog
        {
            Title = $"选择“{selected.Name}”的保存位置",
            InitialDirectory = _lastSaveDirectory != null && Directory.Exists(_lastSaveDirectory)
                ? _lastSaveDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            DownloadStatusText.Text = "已取消选择保存位置。";
            return;
        }

        var destination = dialog.FolderName;
        _lastSaveDirectory = destination;
        try
        {
            // 捕获文件和目录快照；下载器由任务拥有，页面返回/释放不影响队列。
            DownloadQueueService.Instance.StartNew($"下载 {selected.Name}", async (item, ct) =>
            {
                using var downloader = new BedrockMarketplaceDownloadService();
                var path = await downloader.DownloadAsync(selected, destination, item.CreateProgress(), ct);
                item.CompletionText = "已保存：" + path;
            }, originPage: "基岩版市场");
            DownloadStatusText.Text = $"已加入下载列表，保存到：{destination}";
        }
        catch (Exception ex)
        {
            DownloadStatusText.Text = "无法开始下载：" + ex.Message;
        }
    }
}
