using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class TouchpadProcessRow : INotifyPropertyChanged
{
    public GameProcessInfo? Info { get; }
    public KnownWindowInfo? Window { get; private set; }
    public string TitleLine => Info == null ? Window?.Title ?? "窗口"
        : Window == null ? Info.VersionId : $"{Info.VersionId} · {Window.Title}";
    public string DetailLine => Info != null
        ? $"账户：{Info.AccountLabel}  |  PID {Info.Pid}  |  {Window?.ProcessName ?? "游戏"}  |  {Info.TouchInputRoute}"
        : $"PID {Window?.Pid}  |  {Window?.ProcessName}  |  HWND 0x{Window?.Handle.ToInt64():X}";
    public TouchOverlayState State { get; private set; }
    public string? ErrorText { get; private set; }
    public string LocationLine => Info?.GameDir ?? "独立窗口触摸板 · 开启后切回此窗口即可使用";
    public bool UsesWindowSession => Window != null && (Info == null ||
        WindowTouchOverlayService.GetStatus(Window).State != TouchOverlayState.Disabled);
    public Visibility ErrorVisibility => string.IsNullOrEmpty(ErrorText) ? Visibility.Collapsed : Visibility.Visible;
    public string StatusText => (State switch
    {
        TouchOverlayState.Starting => "正在开启…",
        TouchOverlayState.Enabled => "已开启",
        TouchOverlayState.Failed => "开启失败",
        _ => "未开启"
    }) + (Window?.IsForeground == true ? " · 当前前台" : "");

    public event PropertyChangedEventHandler? PropertyChanged;

    public TouchpadProcessRow(GameProcessInfo? info, KnownWindowInfo? window = null)
    {
        Info = info;
        Window = window;
        Refresh(window);
    }

    public void Refresh(KnownWindowInfo? window = null)
    {
        Window = window;
        var status = UsesWindowSession ? WindowTouchOverlayService.GetStatus(Window!)
            : Info != null ? TouchOverlayService.GetStatus(Info) : new(TouchOverlayState.Disabled, null);
        State = status.State;
        ErrorText = status.Error;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocationLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorVisibility)));
    }
}

public partial class TouchpadManagerWindow : OverlayDialogControl
{
    private readonly GameProcessManager _manager;
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var previousEffect = Effect;
        try
        {
            // 主界面的磨砂保持；打开按键设置时，后面的触摸板管理面板也磨砂。
            SetCurrentValue(EffectProperty, new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 12,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            });
            var settings = new TouchControlsSettingsWindow();
            var owner = Window.GetWindow(this);
            if (owner != null)
            {
                settings.Owner = owner;
                settings.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            settings.ShowDialog();
        }
        finally { SetCurrentValue(EffectProperty, previousEffect); }
    }

    private readonly AppConfig _config;
    private readonly ObservableCollection<TouchpadProcessRow> _rows = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _subscribed;
    private bool _discovering;
    private IReadOnlyList<KnownWindowInfo> _knownWindows = Array.Empty<KnownWindowInfo>();

    private async void DiscoverExternalGames()
    {
        if (_discovering || !_subscribed) return;
        _discovering = true;
        try
        {
            var includeWindows = ShowAllWindowsCheck.IsChecked == true || WindowTouchOverlayService.ManagedWindows.Count > 0;
            await _manager.RefreshAsync();
            var windows = includeWindows ? await Task.Run(KnownWindowCatalog.Read) : Array.Empty<KnownWindowInfo>();
            if (_subscribed)
            {
                _knownWindows = ShowAllWindowsCheck.IsChecked == true || WindowTouchOverlayService.ManagedWindows.Count > 0
                    ? windows : Array.Empty<KnownWindowInfo>();
                RefreshInstances();
            }
        }
        catch (Exception ex) { ErrorPresenter.LogTechnicalDetail($"[发现外部游戏窗口] {ex}"); }
        finally { _discovering = false; }
    }

    public TouchpadManagerWindow(GameProcessManager manager, AppConfig config)
    {
        _manager = manager;
        _config = config;
        InitializeComponent();
        MaxHeight = Math.Max(340, Math.Min(580, (Application.Current?.MainWindow?.ActualHeight ?? 700) - 80));
        MinHeight = Math.Min(MinHeight, MaxHeight);
        InstanceListBox.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _refreshTimer.Tick += (_, _) => { RefreshInstances(); DiscoverExternalGames(); };
        RefreshInstances();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _manager.Changed += RefreshInstances;
            TouchOverlayService.Changed += RefreshInstances;
            WindowTouchOverlayService.Changed += RefreshInstances;
            _subscribed = true;
        }
        RefreshInstances();
        _refreshTimer.Start();
        DiscoverExternalGames();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        if (!_subscribed) return;
        _manager.Changed -= RefreshInstances;
        TouchOverlayService.Changed -= RefreshInstances;
        WindowTouchOverlayService.Changed -= RefreshInstances;
        _subscribed = false;
    }

    private void RefreshInstances()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(RefreshInstances));
            return;
        }
        // 与关闭游戏和内存预警使用同一份进程列表；发现实例不会启用触屏。
        var running = _manager.Running;
        var managedWindows = WindowTouchOverlayService.ManagedWindows;
        var windows = _knownWindows.Concat(managedWindows.Select(w => w with { IsForeground = false }))
            .DistinctBy(w => (w.Pid, w.Handle)).ToList();
        var wasEmpty = _rows.Count == 0;
        foreach (var row in _rows.Where(row => row.Info != null && !running.Contains(row.Info)).ToList())
            _rows.Remove(row);
        foreach (var info in running)
        {
            var row = _rows.FirstOrDefault(item => ReferenceEquals(item.Info, info));
            var handle = windows.Any(w => w.Pid == info.Pid)
                ? TouchGameWindow.Find(info.Pid, info.TouchWindowHandle) : IntPtr.Zero;
            var window = windows.FirstOrDefault(w => w.Pid == info.Pid && w.Handle == handle);
            if (row == null) _rows.Add(new TouchpadProcessRow(info, window));
            else row.Refresh(window);
        }

        var displayedGameWindows = _rows.Where(r => r.Info != null && r.Window != null)
            .Select(r => (r.Window!.Pid, r.Window.Handle)).ToHashSet();
        var managedWindowKeys = managedWindows.Select(w => (w.Pid, w.Handle)).ToHashSet();
        var otherWindows = windows.Where(w => ShowAllWindowsCheck.IsChecked == true || managedWindowKeys.Contains((w.Pid, w.Handle)))
            .Where(w => !displayedGameWindows.Contains((w.Pid, w.Handle))).ToList();
        foreach (var row in _rows.Where(r => r.Info == null && !otherWindows.Any(w => w.Handle == r.Window?.Handle && w.Pid == r.Window?.Pid)).ToList())
            _rows.Remove(row);
        foreach (var window in otherWindows)
        {
            var row = _rows.FirstOrDefault(r => r.Info == null && r.Window?.Handle == window.Handle && r.Window?.Pid == window.Pid);
            if (row == null) _rows.Add(new TouchpadProcessRow(null, window));
            else row.Refresh(window);
        }

        // 保留现有行对象与选中项，定时刷新不会打断选择或重置滚动位置。
        if (wasEmpty && _rows.Count == 1) InstanceListBox.SelectedIndex = 0;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = ShowAllWindowsCheck.IsChecked == true ? "暂未发现可见的应用窗口。" : "暂无正在运行的游戏实例\n勾选“显示所有已知窗口”可为其他应用开启触摸板。";
        var enabled = _rows.Count(row => row.State == TouchOverlayState.Enabled);
        var starting = _rows.Count(row => row.State == TouchOverlayState.Starting);
        SummaryText.Text = $"运行中：{running.Count} 个游戏  ·  其他窗口：{_rows.Count(r => r.Info == null)} 个  ·  触摸板已开启：{enabled} 个" +
                           (starting > 0 ? $"  ·  正在开启：{starting} 个" : "");
        RefreshActions();
    }

    private void RefreshActions()
    {
        var row = InstanceListBox.SelectedItem as TouchpadProcessRow;
        var reason = row?.UsesWindowSession == true ? WindowTouchOverlayService.UnavailableReason(row.Window!) : null;
        var running = row != null && (row.UsesWindowSession ? reason == null : row.Info is { HasExited: false });
        EnableButton.ToolTip = row == null ? "请先选择游戏或应用窗口。" : reason ?? "开启后切回选中的窗口即可使用触摸板。";
        EnableButton.IsEnabled = running &&
            (row!.State is TouchOverlayState.Disabled or TouchOverlayState.Failed);
        DisableButton.IsEnabled = row != null && (row.UsesWindowSession
            ? row.State != TouchOverlayState.Disabled : row.Info?.TouchModeEnabled == true);
        DisableButton.Content = row?.State == TouchOverlayState.Starting ? "取消开启" : "关闭触摸板";
    }

    private void InstanceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshActions();

    private void EnableButton_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceListBox.SelectedItem is TouchpadProcessRow row)
        {
            if (row.UsesWindowSession) WindowTouchOverlayService.Enable(row.Window!, _config);
            else if (row.Info is { HasExited: false }) TouchOverlayService.Enable(row.Info, _config);
        }
        RefreshInstances();
    }

    private void DisableButton_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceListBox.SelectedItem is TouchpadProcessRow row)
        {
            if (row.UsesWindowSession) WindowTouchOverlayService.Detach(row.Window!);
            else if (row.Info != null) TouchOverlayService.Detach(row.Info);
        }
        RefreshInstances();
    }

    private void ShowAllWindowsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (ShowAllWindowsCheck.IsChecked != true && WindowTouchOverlayService.ManagedWindows.Count == 0)
            _knownWindows = Array.Empty<KnownWindowInfo>();
        RefreshInstances();
        DiscoverExternalGames();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => CloseWith(null);
}
