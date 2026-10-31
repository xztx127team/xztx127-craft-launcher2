using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace XCL2.App.Services;

/// <summary>
/// 原生标题栏主题、DWM 窗口过渡、F11 全屏和多显示器工作区边界。
/// 主窗口保留标准窗口样式并用 WindowChrome 绘制自定义标题栏；弹窗使用系统标题栏。
/// </summary>
public static class WindowChromeService
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // 早期 Windows 10 Insider build 用的编号

    /// <summary>
    /// 每次 ThemeService.Apply 切换深浅色模式时，同一批"遍历所有已打开窗口"的刷新逻辑里
    /// 一并调用该方法，保证标题栏跟着实时切换，不需要重新打开窗口。
    ///
    /// 窗口首次创建时的标题栏应用不需要单独接线：App.xaml.cs 的 OnStartup 里用
    /// EventManager.RegisterClassHandler 在 Window 类型这一级统一注册了 SourceInitialized
    /// 处理器，项目里所有 Window 子类（MainWindow + 20 多个弹窗）都会自动应用一次，
    /// 新增窗口类也不需要额外接入该逻辑。
    /// </summary>
    public static void ApplyTitleBarTheme(Window window, bool isDark)
    {
        if (!WindowsCompatibilityService.Current.SupportsSystemTheme) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return; // 句柄还没创建（窗口还没 Show 过），交给 App.xaml.cs 的 SourceInitialized 处理器再调一次

        var useDark = isDark && WindowsCompatibilityService.Current.SupportsNativeDarkTitleBar ? 1 : 0;
        // 两个 attribute 编号都尝试设置：新系统认新编号(20)、旧编号会返回失败但无副作用；
        // 极少数老 Windows 10 版本只认旧编号(19)。返回值不为 0 代表调用失败（比如运行在
        // Windows 7/8，系统压根没有该 DWM attribute），静默忽略即可——非深色标题栏
        // 场景下窗口退化回系统默认外观，不影响功能，不弹错误打扰用户。
        TrySetDwmAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, useDark);
        TrySetDwmAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, useDark);
    }

    /// <summary>当前操作交给 DWM；逐类应用偏好，关闭一种动画不会关闭其它窗口动画。</summary>
    public static void ApplyNativeTransitionPreference(Window window, AnimationKind kind = AnimationKind.WindowOpen)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var disabled = !WindowsCompatibilityService.Current.IsEnabled && AnimationSettingsService.IsEnabled(kind) ? 0 : 1;
        TrySetDwmAttribute(hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, disabled);
    }

    private static void TrySetDwmAttribute(IntPtr hwnd, int attribute, int value)
    {
        try { _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)); }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
    }

    // ===== F11 全屏（仅 MainWindow 使用） =====

    private static WindowStyle _savedStyle;
    private static WindowState _savedState;
    private static ResizeMode _savedResizeMode;
    private static bool _isFullScreen;
    private static bool _changingFullScreen;
    private static Window? _fullScreenWindow;

    /// <summary>Raised after F11 has finished its synchronous style/state changes.</summary>
    public static event Action<Window>? FullScreenChanged;

    public static bool IsFullScreenGeometryTemporary(Window window)
        => ReferenceEquals(window, _fullScreenWindow) && (_isFullScreen || _changingFullScreen);

    public static WindowState? GetPreFullScreenState(Window window)
        => IsFullScreenGeometryTemporary(window) ? _savedState : null;

    /// <summary>当前是否处于 F11 全屏状态，供 MainWindow 判断要不要在其它地方
    /// （比如 Esc 键、双击标题栏等）额外处理，目前只有 ToggleFullScreen 自己读写。</summary>
    public static bool IsFullScreen => _isFullScreen;

    /// <summary>
    /// 切换 MainWindow 的全屏状态。进全屏前保存 WindowStyle/WindowState/ResizeMode
    /// 三项（不只是 WindowState，因为全屏还需要去掉系统边框，光 Maximized 本身
    /// 窗口边框和标题栏依然存在），退出时原样恢复，不影响用户之前手动调整过的
    /// 正常窗口大小/位置（Normal 状态下的 Width/Height/Left/Top 全程没有被这里
    /// 动过，WindowState 在 Normal ⇄ Maximized 之间的记忆是 WPF 自带的行为）。
    /// </summary>
    public static void ToggleFullScreen(Window window)
    {
        if (_changingFullScreen || (_isFullScreen && !ReferenceEquals(window, _fullScreenWindow))) return;
        _changingFullScreen = true;
        try
        {
            if (!_isFullScreen)
            {
                _fullScreenWindow = window;
                _savedStyle = window.WindowStyle;
                _savedState = window.WindowState;
                _savedResizeMode = window.ResizeMode;
                window.Closed -= FullScreenWindow_Closed;
                window.Closed += FullScreenWindow_Closed;

                // Placement observers must ignore every intermediate Normal/Maximized state.
                _isFullScreen = true;
                if (window.WindowState == WindowState.Maximized) window.WindowState = WindowState.Normal;
                window.ResizeMode = ResizeMode.NoResize;
                window.WindowStyle = WindowStyle.None;
                window.WindowState = WindowState.Maximized;
            }
            else
            {
                _isFullScreen = false;
                window.WindowState = WindowState.Normal;
                window.WindowStyle = _savedStyle;
                window.ResizeMode = _savedResizeMode;
                window.WindowState = _savedState;
                window.Closed -= FullScreenWindow_Closed;
            }
        }
        finally
        {
            _changingFullScreen = false;
            if (!_isFullScreen) _fullScreenWindow = null;
            FullScreenChanged?.Invoke(window);
        }
    }

    private static void FullScreenWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(window, _fullScreenWindow)) return;
        window.Closed -= FullScreenWindow_Closed;
        _isFullScreen = _changingFullScreen = false;
        _fullScreenWindow = null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    /// <summary>给自绘标题栏窗口（目前只有 MainWindow）接上"最大化尊重任务栏"的修复钩子。
    /// 在窗口构造函数里、SourceInitialized 事件触发时调用一次即可，此后每次用户最大化
    /// 窗口（点按钮/双击标题栏/拖到屏幕顶部）都会自动生效，不需要每次手动调。</summary>
    public static void EnableWorkAreaAwareMaximize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return; // 必须在 SourceInitialized 之后调用，这里兜底一下避免空句柄崩溃

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook((IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            // 任务栏、Alt+F4、系统菜单和标题栏双击也使用相同的动画偏好。
            // 这里只配置 DWM，不截断命令，关闭确认仍由 WPF Closing 处理。
            if (msg == 0x0112 /* WM_SYSCOMMAND */)
            {
                var command = wParam.ToInt64() & 0xFFF0;
                AnimationKind? kind = command switch
                {
                    0xF020 => AnimationKind.WindowMinimize,
                    0xF030 => AnimationKind.WindowFullscreen,
                    0xF120 => window.WindowState == WindowState.Minimized
                        ? AnimationKind.WindowRestore : AnimationKind.WindowFullscreen,
                    0xF060 => AnimationKind.WindowClose,
                    _ => null
                };
                if (kind.HasValue) ApplyNativeTransitionPreference(window, kind.Value);
            }
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            // 我们自己把 WM_GETMINMAXINFO 标记为 handled 后，WPF / WindowChrome 后续默认处理
            // 就不会再有机会把 Window.MinWidth / MinHeight 写进 ptMinTrackSize。此前 MainWindow
            // XAML 虽然声明了最小尺寸，实际上仍能被原生拖拽缩到更小，标题栏右侧按钮就会被
            // 挤出/裁掉，看起来像“离奇消失”。这里显式把 WPF 的 DIP 最小尺寸换算成当前显示器
            // 的物理像素并写回原生结构，确保 854x480 在 100%/125%/150% 等 DPI 下都真正生效。
            var dpi = VisualTreeHelper.GetDpi(window);
            if (!double.IsNaN(window.MinWidth) && !double.IsInfinity(window.MinWidth) && window.MinWidth > 0)
                mmi.ptMinTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX));
            if (!double.IsNaN(window.MinHeight) && !double.IsInfinity(window.MinHeight) && window.MinHeight > 0)
                mmi.ptMinTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY));

            var monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    // ptMaxPosition/ptMaxSize 要用"相对显示器左上角"的偏移，而不是屏幕
                    // 绝对坐标——多屏且非主屏在左/上方向有负坐标时，直接抄绝对坐标会导致
                    // 窗口最大化后跑到别的显示器上，这里统一减去 rcMonitor 的偏移量。
                    // F11 的状态只属于主窗口；独立 AI 聊天最大化仍应避开任务栏。
                    var bounds = _isFullScreen && ReferenceEquals(window, Application.Current?.MainWindow)
                        ? info.rcMonitor : info.rcWork;
                    mmi.ptMaxPosition.X = bounds.Left - info.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = bounds.Top - info.rcMonitor.Top;
                    mmi.ptMaxSize.X = bounds.Right - bounds.Left;
                    mmi.ptMaxSize.Y = bounds.Bottom - bounds.Top;
                }
            }

            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
            return IntPtr.Zero;
        });
    }
}
