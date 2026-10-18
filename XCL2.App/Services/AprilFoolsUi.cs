using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Views;
using Screen = System.Windows.Forms.Screen;
using Box = XCL2.App.Services.AprilFoolsMotionPolicy.Box;
using MotionPoint = XCL2.App.Services.AprilFoolsMotionPolicy.Point;

namespace XCL2.App.Services;

/// <summary>视觉彩蛋与真实启动分离：点到真实启动入口就认输，下载不误判为启动。</summary>
public static class AprilFoolsUi
{
    private sealed class WindowAttachment
    {
        public bool TemporaryGeometry;
        public WINDOWPLACEMENT? OriginalPlacement;
        public WindowState? OriginalState;
        public RECT? MovementOrigin;
        public int Generation;
    }
    private static readonly ConditionalWeakTable<MainWindow, WindowAttachment> WindowAttachments = new();
    private static readonly ConditionalWeakTable<Panel, object> HomeAttachments = new();
    private static readonly Random Rng = new();
    private static DispatcherTimer? _recoveryTimer;
    private const string RickRoll1Url = "https://www.bilibili.com/video/BV1GJ411x7h7/";
    private const string RickRoll2Url = "https://www.bilibili.com/video/BV12rMQ6yEP2";

    public static bool IsWindowGeometryTemporary(Window window) => window is MainWindow main
        && WindowAttachments.TryGetValue(main, out var attachment) && attachment.TemporaryGeometry;
    public static WindowState? OriginalWindowState(Window window) => window is MainWindow main
        && WindowAttachments.TryGetValue(main, out var attachment) ? attachment.OriginalState : null;
    private static bool CanInteract(Window? window) => window is { IsLoaded: true, IsVisible: true, IsActive: true, IsEnabled: true }
        && window.WindowState != WindowState.Minimized && !OverlayDialogService.HasActiveOverlay
        && !window.OwnedWindows.Cast<Window>().Any(child => child.IsVisible);
    public static bool IsWindowMotionEnabled => AprilFoolsService.Has(AprilFoolsService.Effect.WindowChaos)
        && AnimationSettingsService.IsEnabled(AnimationKind.WindowFullscreen);
    private static int Interval => Math.Clamp(ConfigService.Active?.Config.AprilFoolsMoveIntervalMs ?? 150, 100, 1000);

    public static void Attach(MainWindow owner)
    {
        if (WindowAttachments.TryGetValue(owner, out _)) return;
        var attachment = new WindowAttachment();
        WindowAttachments.Add(owner, attachment);
        var closed = false;
        var subscribed = false;
        var watchdog = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        var movement = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(Interval) };
        var config = ConfigService.Active;

        void RestoreGeometry()
        {
            if (attachment.OriginalPlacement is not { } placement) return;
            attachment.OriginalPlacement = null;
            attachment.MovementOrigin = null;
            // 抑制恢复产生的 Location/StateChanged，直到布局处理完毕才让正常位置记忆继续。
            attachment.TemporaryGeometry = true;
            // 隐藏/最小化时恢复布局不能把启动器重新弹到前台。
            if (!owner.IsVisible) placement.ShowCmd = 0; // SW_HIDE
            else if (owner.WindowState == WindowState.Minimized) placement.ShowCmd = 2; // SW_SHOWMINIMIZED
            SetWindowPlacement(new WindowInteropHelper(owner).Handle, ref placement);
            var generation = ++attachment.Generation;
            owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (attachment.Generation != generation || attachment.OriginalPlacement != null) return;
                attachment.TemporaryGeometry = false;
                attachment.OriginalState = null;
            }));
        }
        bool Eligible() => IsWindowMotionEnabled;
        void UpdateMotion()
        {
            movement.Interval = TimeSpan.FromMilliseconds(Interval);
            if (!Eligible()) { movement.Stop(); RestoreGeometry(); }
            else if (!closed && subscribed && CanInteract(owner)) movement.Start();
            else movement.Stop();
        }
        void Dispatch(Action action)
        {
            if (closed || owner.Dispatcher.HasShutdownStarted) return;
            if (owner.Dispatcher.CheckAccess()) action();
            else owner.Dispatcher.BeginInvoke(new Action(() => { if (!closed) action(); }));
        }
        void StateChanged() => Dispatch(() => { RefreshFlagVisibility(owner); UpdateMotion(); ScheduleRecoveryPrompt(); });
        var storageWarningShown = false;
        void StorageWarning(string message) => Dispatch(() =>
        {
            if (storageWarningShown) return;
            storageWarningShown = true;
            LauncherLogService.AppendLine("[AprilFools] " + message);
            ToastService.ShowWarning(message);
        });
        void PreferencesSaved() => Dispatch(AprilFoolsService.RefreshPreferences);
        void AnimationChanged() => Dispatch(UpdateMotion);
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateMotion();
        void WindowChanged(object? sender, EventArgs e) => UpdateMotion();
        void Closing(object? sender, CancelEventArgs e)
        {
            // MainWindow 可先将关闭转成最小化/托盘；遵守已取消的关闭结果。
            if (e.Cancel) { UpdateMotion(); return; }
            movement.Stop(); RestoreGeometry();
        }
        void PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F1 || !AprilFoolsService.IsActive) return;
            e.Handled = true;
            AprilFoolsService.DismissForToday();
        }
        void Loaded(object sender, RoutedEventArgs e)
        {
            if (closed || subscribed) return;
            subscribed = true;
            AprilFoolsService.StateChanged += StateChanged;
            AprilFoolsService.PersistenceWarningRaised += StorageWarning;
            if (AprilFoolsService.PersistenceWarning is { } warning) StorageWarning(warning);
            AnimationSettingsService.Changed += AnimationChanged;
            if (config != null) config.Saved += PreferencesSaved;
            watchdog.Start();
            StateChanged();
        }
        void Unloaded(object sender, RoutedEventArgs e)
        {
            subscribed = false;
            AprilFoolsService.StateChanged -= StateChanged;
            AprilFoolsService.PersistenceWarningRaised -= StorageWarning;
            AnimationSettingsService.Changed -= AnimationChanged;
            if (config != null) config.Saved -= PreferencesSaved;
            watchdog.Stop(); movement.Stop(); RestoreGeometry();
            _recoveryTimer?.Stop(); _recoveryTimer = null;
        }
        void Closed(object? sender, EventArgs e)
        {
            closed = true;
            Unloaded(owner, new RoutedEventArgs());
            owner.Loaded -= Loaded; owner.Unloaded -= Unloaded;
            owner.IsVisibleChanged -= VisibilityChanged;
            owner.StateChanged -= WindowChanged; owner.Activated -= WindowChanged; owner.Deactivated -= WindowChanged;
            owner.Closing -= Closing; owner.PreviewKeyDown -= PreviewKeyDown; owner.Closed -= Closed;
            // WindowPlacementService 的 Closed 订阅可能排在后面，仍需读取原最大化状态。
            // 弱键表不会延长窗口生命期；所有事件和计时器已经在上面解绑。
        }
        watchdog.Tick += (_, _) =>
        {
            if (closed || !subscribed) return;
            AprilFoolsService.EnsureTodaysStateLoaded();
            RefreshFlagVisibility(owner); UpdateMotion();
        };
        movement.Tick += (_, _) =>
        {
            if (!Eligible() || !CanInteract(owner)) { UpdateMotion(); return; }
            var hwnd = new WindowInteropHelper(owner).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (attachment.OriginalPlacement == null)
            {
                var original = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(hwnd, ref original)) return;
                attachment.OriginalPlacement = original;
                attachment.OriginalState = owner.WindowState;
                attachment.TemporaryGeometry = true;
                attachment.Generation++;
                if (owner.WindowState == WindowState.Maximized) owner.WindowState = WindowState.Normal;
            }
            if (!GetWindowRect(hwnd, out var rectangle)) return;
            attachment.MovementOrigin ??= rectangle;
            var area = Screen.FromHandle(hwnd).WorkingArea;
            var width = Math.Min(rectangle.Right - rectangle.Left, area.Width);
            var height = Math.Min(rectangle.Bottom - rectangle.Top, area.Height);
            var intensity = AnimationSettingsService.Intensity(AnimationKind.WindowFullscreen);
            var targetX = area.Left + Rng.NextDouble() * Math.Max(0, area.Width - width);
            var targetY = area.Top + Rng.NextDouble() * Math.Max(0, area.Height - height);
            var origin = attachment.MovementOrigin.Value;
            var fitted = AprilFoolsMotionPolicy.Fit(new Box(
                origin.Left + (targetX - origin.Left) * intensity,
                origin.Top + (targetY - origin.Top) * intensity, width, height),
                new Box(area.Left, area.Top, area.Width, area.Height));
            SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(fitted.X), (int)Math.Round(fitted.Y),
                (int)Math.Round(fitted.Width), (int)Math.Round(fitted.Height), 0x0014); // NOZORDER | NOACTIVATE
        };
        owner.Loaded += Loaded; owner.Unloaded += Unloaded;
        owner.IsVisibleChanged += VisibilityChanged;
        owner.StateChanged += WindowChanged; owner.Activated += WindowChanged; owner.Deactivated += WindowChanged;
        owner.Closing += Closing; owner.PreviewKeyDown += PreviewKeyDown; owner.Closed += Closed;
        RefreshFlagVisibility(owner);
        if (owner.IsLoaded) Loaded(owner, new RoutedEventArgs());
    }

    private static void RefreshFlagVisibility(MainWindow owner)
        => owner.AprilFoolsFlagButton.Visibility = AprilFoolsService.IsActive ? Visibility.Visible : Visibility.Collapsed;
    private static void ScheduleRecoveryPrompt()
    {
        if (!AprilFoolsService.IsActive) { _recoveryTimer?.Stop(); _recoveryTimer = null; return; }
        if (_recoveryTimer != null) return;
        _recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _recoveryTimer.Tick += (_, _) =>
        {
            _recoveryTimer?.Stop();
            if (AprilFoolsService.IsActive)
                ReminderCenterService.Publish("april-fools-recovery", "愚人节彩蛋", "点击右下角白旗或按 F1 可立即结束今天的整蛊；设置 → 趣味与彩蛋关闭总开关会打开确认挑战，输入彩蛋码可跳过。");
        };
        _recoveryTimer.Start();
    }

    /// <summary>白旗直接结束当天整蛊，不进入文字游戏或闯关。</summary>
    public static void OnWhiteFlagClicked(MainWindow owner)
    {
        if (!AprilFoolsService.IsActive) return;
        AprilFoolsService.ClickWhiteFlag();
        ToastService.ShowInfo("今天的整蛊已经结束。");
    }

    /// <returns>仅显式允许的非视觉彩蛋可能拦截；视觉整蛊中真正调用启动入口则结束整蛊并继续。</returns>
    public static bool NotifyLaunchRequested(Window owner)
    {
        if (AprilFoolsService.HasMotionEffects)
        {
            AprilFoolsService.DismissForToday();
            ToastService.ShowSuccess("哇哇哇，你竟然点到了！今天不整蛊你了。");
            return false;
        }
        return TryInterceptLaunchOrDownload(owner);
    }

    /// <summary>旧名称保留兼容下载调用点；这里不将下载误判成成功启动。</summary>
    public static bool TryInterceptLaunchOrDownload(Window owner)
    {
        if (!AprilFoolsService.TryGetLaunchOrDownloadInterception(out var effect)) return false;
        switch (effect)
        {
            case AprilFoolsService.Effect.RickRoll1: OpenUrl(RickRoll1Url); break;
            case AprilFoolsService.Effect.RickRoll2: OpenUrl(RickRoll2Url); break;
            case AprilFoolsService.Effect.FakeWin32Error:
                MessageBoxDialog.ShowInfo("这是已开启的愚人节模拟报错彩蛋，并非真实故障。\n\n模拟错误：launcher.dll 第 41 行出现意料之外的橡皮鸭。按 F1 可恢复。", "愚人节模拟报错");
                break;
            case AprilFoolsService.Effect.LauncherRefuses:
                MessageBoxDialog.ShowInfo(AprilFoolsService.RandomRefusalText() + "\n\n这是已开启的愚人节彩蛋；按 F1 可恢复。", "愚人节彩蛋");
                break;
            default: return false;
        }
        return true;
    }
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { LauncherLogService.AppendLine("[AprilFools] 无法打开已允许的视频：" + ex.GetType().Name); }
    }

    public static void AttachHomeTileEffects(Panel tileGrid, Button launchTile, Button quickStartTile)
    {
        if (HomeAttachments.TryGetValue(tileGrid, out _)) return;
        HomeAttachments.Add(tileGrid, new object());
        var subscribed = false;
        Window? window = null;
        var transforms = new Dictionary<Button, (Transform Original, TransformGroup Group, TranslateTransform Offset, int Z)>();
        var dodgeTimer = new DispatcherTimer(DispatcherPriority.Input, tileGrid.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        var tileTimer = new DispatcherTimer(DispatcherPriority.Background, tileGrid.Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        var activityTimer = new DispatcherTimer(DispatcherPriority.Background, tileGrid.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        bool CanAnimate(AnimationKind kind) => subscribed && tileGrid.IsLoaded && tileGrid.IsVisible
            && CanInteract(window) && AnimationSettingsService.IsEnabled(kind);
        bool DodgeEnabled() => AprilFoolsService.Has(AprilFoolsService.Effect.DodgeLaunchButton)
            && AnimationSettingsService.IsEnabled(AnimationKind.ControlFeedback);
        bool ChaosEnabled() => AprilFoolsService.Has(AprilFoolsService.Effect.TileChaos)
            && AnimationSettingsService.IsEnabled(AnimationKind.Page);
        TranslateTransform Offset(Button button)
        {
            if (transforms.TryGetValue(button, out var existing) && ReferenceEquals(button.RenderTransform, existing.Group)) return existing.Offset;
            var original = button.RenderTransform ?? Transform.Identity;
            var offset = new TranslateTransform();
            var group = new TransformGroup(); group.Children.Add(original); group.Children.Add(offset);
            transforms[button] = (original, group, offset, Panel.GetZIndex(button));
            button.SetCurrentValue(UIElement.RenderTransformProperty, group);
            return offset;
        }
        void Reset(Button button)
        {
            if (!transforms.Remove(button, out var saved)) return;
            saved.Offset.X = saved.Offset.Y = 0;
            if (ReferenceEquals(button.RenderTransform, saved.Group)) button.SetCurrentValue(UIElement.RenderTransformProperty, saved.Original);
            Panel.SetZIndex(button, saved.Z);
        }
        Rect VisibleArea()
        {
            var area = new Rect(0, 0, Math.Max(0, tileGrid.ActualWidth), Math.Max(0, tileGrid.ActualHeight));
            for (DependencyObject? ancestor = VisualTreeHelper.GetParent(tileGrid); ancestor != null; ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                if (ancestor is not FrameworkElement element || (!element.ClipToBounds && element is not ScrollContentPresenter)) continue;
                var topLeft = element.TranslatePoint(new Point(), tileGrid);
                area.Intersect(new Rect(topLeft, new Size(Math.Max(0, element.ActualWidth), Math.Max(0, element.ActualHeight))));
                if (area.IsEmpty) break;
            }
            return area;
        }
        bool Bounds(Button button, out Rect rectangle)
        {
            rectangle = Rect.Empty;
            if (!button.IsVisible || button.ActualWidth <= 0 || button.ActualHeight <= 0) return false;
            try { rectangle = button.TransformToAncestor(tileGrid).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight)); return true; }
            catch (InvalidOperationException) { return false; }
        }
        void UpdateTimers()
        {
            tileTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, Interval * .8));
            // 弹窗暂停后由低频检查恢复，不留下已经停止却永远无法重新启动的避让计时器。
            if (subscribed && tileGrid.IsLoaded && tileGrid.IsVisible && window is { IsVisible: true, IsActive: true }
                && window.WindowState != WindowState.Minimized && (DodgeEnabled() || ChaosEnabled())) activityTimer.Start();
            else activityTimer.Stop();
            if (DodgeEnabled() && CanAnimate(AnimationKind.ControlFeedback)) dodgeTimer.Start(); else dodgeTimer.Stop();
            if (ChaosEnabled() && CanAnimate(AnimationKind.Page)) tileTimer.Start(); else tileTimer.Stop();
            foreach (var button in transforms.Keys.ToArray())
                if (!ChaosEnabled() && (!DodgeEnabled() || (button != launchTile && button != quickStartTile))) Reset(button);
        }
        activityTimer.Tick += (_, _) => UpdateTimers();
        dodgeTimer.Tick += (_, _) =>
        {
            if (!DodgeEnabled() || !CanAnimate(AnimationKind.ControlFeedback)) { UpdateTimers(); return; }
            var area = VisibleArea();
            if (area.IsEmpty) return;
            var intensity = AnimationSettingsService.Intensity(AnimationKind.ControlFeedback);
            var gap = Math.Clamp(ConfigService.Active?.Config.AprilFoolsMouseAvoidanceDistance ?? 100, 40, 200) * intensity;
            var mouse = Mouse.GetPosition(tileGrid);
            foreach (var button in new[] { launchTile, quickStartTile })
            {
                if (!Bounds(button, out var bounds)) continue;
                if (bounds.Width > area.Width || bounds.Height > area.Height) { Reset(button); continue; }
                var current = new Box(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                if (AprilFoolsMotionPolicy.Distance(new(mouse.X, mouse.Y), current) > gap) continue;
                var offset = Offset(button);
                var originX = bounds.X - offset.X; var originY = bounds.Y - offset.Y;
                var minX = Math.Max(area.Left, originX - area.Width * intensity);
                var minY = Math.Max(area.Top, originY - area.Height * intensity);
                var maxX = Math.Min(area.Right - bounds.Width, originX + area.Width * intensity);
                var maxY = Math.Min(area.Bottom - bounds.Height, originY + area.Height * intensity);
                if (maxX < minX || maxY < minY) continue;
                var allowed = new Box(minX, minY, maxX - minX + bounds.Width, maxY - minY + bounds.Height);
                var target = AprilFoolsMotionPolicy.Evade(current, allowed, new MotionPoint(mouse.X, mouse.Y), gap, Rng);
                offset.X += target.X - bounds.X; offset.Y += target.Y - bounds.Y;
                Panel.SetZIndex(button, 5);
            }
        };
        tileTimer.Tick += (_, _) =>
        {
            if (!ChaosEnabled() || !CanAnimate(AnimationKind.Page)) { UpdateTimers(); return; }
            var area = VisibleArea(); if (area.IsEmpty) return;
            var intensity = AnimationSettingsService.Intensity(AnimationKind.Page);
            foreach (var button in tileGrid.Children.OfType<Button>())
            {
                // 两种效果同时启用时，启动入口的位置完全由避让定时器管理。
                if (DodgeEnabled() && (button == launchTile || button == quickStartTile)) continue;
                if (!Bounds(button, out var bounds)) continue;
                if (bounds.Width > area.Width || bounds.Height > area.Height) { Reset(button); continue; }
                var offset = Offset(button);
                var originX = bounds.X - offset.X; var originY = bounds.Y - offset.Y;
                var target = AprilFoolsMotionPolicy.Fit(new Box(originX + (Rng.NextDouble() - .5) * 60 * intensity,
                    originY + (Rng.NextDouble() - .5) * 60 * intensity, bounds.Width, bounds.Height),
                    new Box(area.X, area.Y, area.Width, area.Height));
                offset.X += target.X - bounds.X; offset.Y += target.Y - bounds.Y;
            }
        };
        void PreferencesChanged()
        {
            if (!subscribed || tileGrid.Dispatcher.HasShutdownStarted) return;
            if (tileGrid.Dispatcher.CheckAccess()) UpdateTimers();
            else tileGrid.Dispatcher.BeginInvoke(new Action(() => { if (subscribed) UpdateTimers(); }));
        }
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTimers();
        void WindowChanged(object? sender, EventArgs e) => UpdateTimers();
        void SizeChanged(object sender, SizeChangedEventArgs e) { foreach (var button in transforms.Keys.ToArray()) Reset(button); UpdateTimers(); }
        void OwnerClosed(object? sender, EventArgs e) => Unloaded(tileGrid, new RoutedEventArgs());
        void Loaded(object sender, RoutedEventArgs e)
        {
            if (subscribed) return;
            subscribed = true; window = Window.GetWindow(tileGrid);
            AprilFoolsService.StateChanged += PreferencesChanged;
            AnimationSettingsService.Changed += PreferencesChanged;
            if (window != null)
            {
                window.IsVisibleChanged += VisibilityChanged; window.StateChanged += WindowChanged;
                window.Activated += WindowChanged; window.Deactivated += WindowChanged; window.Closed += OwnerClosed;
            }
            UpdateTimers();
        }
        void Unloaded(object sender, RoutedEventArgs e)
        {
            subscribed = false;
            AprilFoolsService.StateChanged -= PreferencesChanged;
            AnimationSettingsService.Changed -= PreferencesChanged;
            dodgeTimer.Stop(); tileTimer.Stop(); activityTimer.Stop();
            foreach (var button in transforms.Keys.ToArray()) Reset(button);
            if (window != null)
            {
                window.IsVisibleChanged -= VisibilityChanged; window.StateChanged -= WindowChanged;
                window.Activated -= WindowChanged; window.Deactivated -= WindowChanged; window.Closed -= OwnerClosed;
                window = null;
            }
        }
        tileGrid.Loaded += Loaded; tileGrid.Unloaded += Unloaded; tileGrid.SizeChanged += SizeChanged;
        tileGrid.IsVisibleChanged += VisibilityChanged;
        if (tileGrid.IsLoaded) Loaded(tileGrid, new RoutedEventArgs());
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPLACEMENT
    { public int Length, Flags, ShowCmd; public POINT MinPosition, MaxPosition; public RECT NormalPosition; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rectangle);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
