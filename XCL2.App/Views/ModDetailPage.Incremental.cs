using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ModDetailPage
{
    private readonly ObservableCollection<DetailVersionGroupView> _visibleDetailGroups = new();
    private readonly HashSet<FrameworkElement> _detailGroupTails = new();
    private IReadOnlyList<VersionGroup> _filteredDetailGroups = Array.Empty<VersionGroup>();
    private bool _detailContinuousLoading;
    private bool _detailControlsUpdating;
    private bool _detailLoadingBatch;
    private bool _detailScrollPending;
    private int _detailBatchSize = 20;
    private int _detailGeneration;

    private void InitializeDetailIncrementalLoading()
    {
        RefreshDetailIncrementalSettings();
        Loaded += (_, _) => RefreshDetailIncrementalSettings();
        Unloaded += (_, _) =>
        {
            _detailGeneration++;
            _detailLoadingBatch = false;
            _detailScrollPending = false;
            _detailGroupTails.Clear();
            foreach (var group in _visibleDetailGroups) group.IsLoading = false;
            UpdateDetailGroupsTail();
        };
    }

    private void RefreshDetailIncrementalSettings()
    {
        var config = ConfigService.Active?.Config;
        var continuous = config?.DownloadResourcesContinuousLoading ?? false;
        var batchSize = DownloadListBatchPolicy.Normalize(config?.DownloadListBatchSize ?? 20);
        var changed = continuous != _detailContinuousLoading || batchSize != _detailBatchSize;
        _detailContinuousLoading = continuous;
        _detailBatchSize = batchSize;
        _detailControlsUpdating = true;
        DetailContinuousLoadingCheck.IsChecked = continuous;
        _detailControlsUpdating = false;
        if (changed && _filteredDetailGroups.Count > 0) SetDetailVersionGroups(_filteredDetailGroups);
    }

    private void DetailContinuousLoading_Changed(object sender, RoutedEventArgs e)
    {
        if (_detailControlsUpdating) return;
        _detailContinuousLoading = DetailContinuousLoadingCheck.IsChecked == true;
        if (ConfigService.Active is { } service)
        {
            service.Config.DownloadResourcesContinuousLoading = _detailContinuousLoading;
            try { service.Save(); }
            catch (Exception ex) { ErrorPresenter.LogFallback("下载列表设置保存失败", ex); }
        }
        SetDetailVersionGroups(_filteredDetailGroups);
    }

    private void ClearDetailVersionGroups()
    {
        _detailGeneration++;
        _detailLoadingBatch = false;
        _detailScrollPending = false;
        _filteredDetailGroups = Array.Empty<VersionGroup>();
        _visibleDetailGroups.Clear();
        _detailGroupTails.Clear();
        GroupsList.ItemsSource = _visibleDetailGroups;
        UpdateDetailGroupsTail();
    }

    private void SetDetailVersionGroups(IReadOnlyList<VersionGroup> groups)
    {
        // Modrinth's version endpoint returns metadata as one array. Keep that metadata
        // for accurate filtering, but only materialize groups and expanded file rows on demand.
        ClearDetailVersionGroups();
        _filteredDetailGroups = groups;
        AppendDetailGroups(_detailContinuousLoading ? DownloadListBatchPolicy.Initial(_detailBatchSize) : groups.Count);
    }

    private void AppendDetailGroups(int count)
    {
        var end = _visibleDetailGroups.Count + Math.Min(count, _filteredDetailGroups.Count - _visibleDetailGroups.Count);
        // A filter/mode change can preserve several expanded groups. Share the first
        // render's row budget across them, rather than materializing 20 files per group.
        var remainingEntryBudget = count;
        for (var index = _visibleDetailGroups.Count; index < end; index++)
        {
            var group = new DetailVersionGroupView(_filteredDetailGroups[index], _detailContinuousLoading, _detailBatchSize, remainingEntryBudget);
            _visibleDetailGroups.Add(group);
            remainingEntryBudget = Math.Max(0, remainingEntryBudget - group.Entries.Count);
        }
        UpdateDetailGroupsTail();
    }

    private void UpdateDetailGroupsTail()
    {
        DetailMoreGroupsPanel.Visibility = _detailContinuousLoading && _visibleDetailGroups.Count < _filteredDetailGroups.Count
            ? Visibility.Visible : Visibility.Collapsed;
        DetailMoreGroupsSpinner.Visibility = _detailLoadingBatch ? Visibility.Visible : Visibility.Collapsed;
        DetailMoreGroupsText.Text = _detailLoadingBatch ? "正在加载更多版本分组…" : "继续向下滚动加载更多版本分组";
    }

    private void DetailGroupTail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement tail) _detailGroupTails.Add(tail);
    }

    private void DetailGroupTail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement tail) _detailGroupTails.Remove(tail);
    }

    private void DetailScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Layout/extent changes are not user requests and must not drain the whole catalog.
        if (e.VerticalChange > 0) RequestDetailBatch();
    }

    private void DetailScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Also handles a short/collapsed first batch that does not yet overflow the viewport.
        // Normal scrolling is handled by ScrollChanged, so one wheel event cannot queue two batches.
        if (e.Delta < 0 && DetailScrollViewer.VerticalOffset >= DetailScrollViewer.ScrollableHeight - 1)
            RequestDetailBatch();
    }

    private void DetailScrollViewer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SaveNameCombo.IsKeyboardFocusWithin) return;
        if ((e.Key is Key.Down or Key.PageDown or Key.End) &&
            DetailScrollViewer.VerticalOffset >= DetailScrollViewer.ScrollableHeight - 1)
            RequestDetailBatch();
    }

    private bool IsDetailTailNearViewport(FrameworkElement tail)
    {
        if (!tail.IsVisible || tail.ActualHeight <= 0) return false;
        try
        {
            var bounds = tail.TransformToAncestor(DetailScrollViewer)
                .TransformBounds(new Rect(new Point(), tail.RenderSize));
            return bounds.Bottom >= 0 && bounds.Top <= DetailScrollViewer.ActualHeight + 160;
        }
        catch (InvalidOperationException) { return false; } // A filter replaced this visual.
    }

    private async void RequestDetailBatch()
    {
        if (!_detailContinuousLoading || !IsLoaded) return;
        if (_detailLoadingBatch) { _detailScrollPending = true; return; }
        var generation = _detailGeneration;
        _detailLoadingBatch = true;
        try
        {
            do
            {
                _detailScrollPending = false;
                // Let the wheel/keyboard operation settle before checking the visible tails.
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                if (generation != _detailGeneration || !IsLoaded) return;
                var nextGroup = _detailGroupTails
                    .Where(IsDetailTailNearViewport)
                    .Select(tail => tail.DataContext as DetailVersionGroupView)
                    .FirstOrDefault(group => group is { HasMore: true } && _visibleDetailGroups.Contains(group));
                if (nextGroup != null)
                {
                    nextGroup.IsLoading = true;
                    await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                    if (generation != _detailGeneration || !IsLoaded) return;
                    nextGroup.LoadNext();
                    nextGroup.IsLoading = false;
                }
                else if (_visibleDetailGroups.Count < _filteredDetailGroups.Count && IsDetailTailNearViewport(DetailMoreGroupsPanel))
                {
                    UpdateDetailGroupsTail();
                    await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                    if (generation != _detailGeneration || !IsLoaded) return;
                    AppendDetailGroups(_detailBatchSize);
                }
            } while (_detailScrollPending);
        }
        catch (Exception ex) { ErrorPresenter.LogFallback("版本文件分段加载失败", ex); }
        finally
        {
            if (generation == _detailGeneration)
            {
                _detailLoadingBatch = false;
                foreach (var group in _visibleDetailGroups) group.IsLoading = false;
                UpdateDetailGroupsTail();
            }
        }
    }

    internal sealed class DetailVersionGroupView : INotifyPropertyChanged
    {
        private readonly VersionGroup _source;
        private readonly IncrementalGroupEntries<InlineVersionEntry> _entries;
        private bool _isLoading;
        public string GameVersion => _source.GameVersion;
        public string Loader => _source.Loader;
        public int EntryCount => _entries.TotalCount;
        public List<ModrinthDependency> RequiredDependencies => _source.RequiredDependencies;
        public bool HasRequiredDependencies => IsExpanded && _source.HasRequiredDependencies;
        public ObservableCollection<InlineVersionEntry> Entries => _entries.Visible;
        public bool HasMore => _entries.HasMore;
        public string MoreHint => IsLoading ? "正在加载更多文件…" : $"已显示 {Entries.Count} / {EntryCount} 个文件 · 继续向下滚动加载";
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; Changed(); Changed(nameof(MoreHint)); }
        }
        public bool IsExpanded
        {
            get => _entries.IsExpanded;
            set
            {
                if (_entries.IsExpanded == value) return;
                _source.IsExpanded = value;
                _entries.SetExpanded(value);
                Changed(); Changed(nameof(HasMore)); Changed(nameof(HasRequiredDependencies)); Changed(nameof(MoreHint));
            }
        }

        public DetailVersionGroupView(VersionGroup source, bool continuous, int batchSize, int initialEntryLimit)
        {
            _source = source;
            _entries = new IncrementalGroupEntries<InlineVersionEntry>(source.Entries, continuous, batchSize);
            _entries.SetExpanded(source.IsExpanded, initialEntryLimit);
        }

        public void LoadNext()
        {
            _entries.LoadNext();
            Changed(nameof(HasMore)); Changed(nameof(MoreHint));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? property = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
