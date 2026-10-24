using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>
/// 26.3 第 4 快照以前的 GLFW/LWJGL 兼容层。
/// 原生输入层在触摸提升前接管，视角只取触点增量，不回中、不使用绝对定位。
/// 菜单与视角在落指时确定，手势中途不会因为触摸隐藏光标而切换模式。
/// </summary>
public partial class TouchOverlayWindow : Window, IGlobalWindowTreatmentExempt, ITouchOverlayWindow
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref ModeCursorInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct ModeCursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WM_SETCURSOR = 0x0020;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_POINTERACTIVATE = 0x024B;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private IntPtr _gameHwnd;
    private readonly int _gamePid;
    private readonly Func<bool> _canTakeOver;
    private readonly bool _forceTouchpad;
    private readonly LegacyTouchWorldEvidence _worldEvidence;
    private LegacyTouchWorldPhase _lastObservedWorldPhase;
    private IntPtr _overlayHwnd;
    private HwndSource? _source;
    private LegacyTouchInputSurface? _inputSurface;
    private LegacyMouseMessageKind? _surfaceMouseKind;
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    // 合并当前消息批次后尽快发送，不用 WM_TIMER 的 8ms 等待叠加到触摸设备帧间隔。
    // 最多一个待执行操作，透明窗口没有渲染帧时也能投递。
    private DispatcherOperation? _inputFlushOperation;
    private readonly HashSet<ushort> _heldKeys = new();
    private readonly HashSet<TouchInputInjector.MouseButton> _heldButtons = new();
    private readonly Dictionary<ushort, int> _keyReferences = new();
    private readonly Dictionary<TouchInputInjector.MouseButton, int> _buttonReferences = new();
    private readonly Dictionary<Border, (ushort[] Keys, TouchInputInjector.MouseButton? Button)> _activeBindings = new();
    private readonly double _buttonScale;
    private bool _releasingHeld;
    private bool _settingsOpen;
    private long _explicitModeUntil;
    private long _menuTransitionUntil;
    private readonly Dictionary<int, (TouchDevice Device, Border Key)> _touchKeys = new();
    // 保护所有触点，包括按键、触摸板以及尚未被子控件捕获的第一帧。
    private readonly HashSet<int> _promotionContacts = new();
    private readonly List<Border> _keys = new();
    private Border? _mouseKey;
    private (int Left, int Top, int Width, int Height)? _lastClientBounds;
    private long _foregroundMissingSince = -1;

    // GLFW 光标形状刷新有节流与严格超时；其余状态通过本地 Win32 读取。
    private readonly LegacyGameCursorProbe _gameCursorProbe = new();
    private long _nextCursorProbeAt;
    private LegacyTouchInputBridge _legacyInput;
    private readonly TouchModeStability _modeStability = new();
    private readonly LegacyTouchModeRecoveryPolicy _modeRecovery = new();
    private long _windowStableSince = -1;
    private long _modeChangeAllowedAfter;
    private const int GameModeStabilityMilliseconds = 100;
    private const int MenuModeStabilityMilliseconds = 150;
    private const int ContactSettleMilliseconds = 180;
    private long _menuRequestedUntil;

    // 一根手指负责菜单指针/视角，其他手指仍可独立按住虚拟键。
    private TouchDevice? _viewTouch;
    private Point _viewDragStart;
    private Point _viewDragLast;
    private DateTime _viewDragTime;
    private bool _viewDragMoved;
    private bool _viewDragIsMenu;
    private bool _menuPointerDown;
    private Point _menuPointerScreen;
    private bool _menuCarryDrag;
    private const double MenuDragThreshold = 12;
    private const int MenuPickUpHoldMilliseconds = 350;
    private bool _menuMode = true;
    private readonly TouchCaptureRecovery _captureRecovery = new();
    private bool _hidden;
    private bool _closingDown;
    private double _sensitivity;

    private bool HasActiveContact => _inputSurface?.HasContacts == true || _promotionContacts.Count != 0 ||
        _viewTouch != null || _padTouch != null || _touchKeys.Count != 0 || _mouseKey != null;

    private bool HasActiveMenuGesture => (_viewTouch != null && _viewDragIsMenu) ||
        (_padTouch != null && _padIsMenu) || _menuPointerDown;

    private bool IsGameForeground => !_closingDown && _canTakeOver() && _gameHwnd != IntPtr.Zero &&
        GetForegroundWindow() == _gameHwnd && !IsIconic(_gameHwnd);

    public TouchOverlayWindow(IntPtr gameHwnd, double buttonScale, double opacityPercent, double sensitivity,
        int gamePid, Func<bool> canTakeOver, bool forceTouchpad = false)
        : this(gameHwnd, buttonScale, opacityPercent, sensitivity, gamePid, canTakeOver, new LegacyTouchWorldEvidence(), forceTouchpad) { }

    internal TouchOverlayWindow(IntPtr gameHwnd, double buttonScale, double opacityPercent, double sensitivity, int gamePid,
        Func<bool> canTakeOver, LegacyTouchWorldEvidence worldEvidence, bool forceTouchpad = false)
    {
        InitializeComponent();
        _gameHwnd = gameHwnd;
        _legacyInput = new LegacyTouchInputBridge(gameHwnd, worldEvidence);
        _gamePid = gamePid;
        _canTakeOver = canTakeOver;
        _forceTouchpad = forceTouchpad;
        _worldEvidence = worldEvidence;
        _lastObservedWorldPhase = worldEvidence.Phase;
        _modeRecovery.ResetContext(_lastObservedWorldPhase);
        _sensitivity = sensitivity <= 0 ? 1.4 : sensitivity;
        _buttonScale = !double.IsFinite(buttonScale) || buttonScale <= 0 ? 1.0 : buttonScale;
        RootGrid.Opacity = Math.Clamp(opacityPercent <= 0 ? 85 : opacityPercent, 20, 100) / 100.0;

        RestoreKey.Tag = new TouchControlDefinition { Id = "restore", Mode = TouchControlMode.Command, Binding = "SHOW" };
        HookKey(RestoreKey);
        ModeKey.Tag = new TouchControlDefinition { Id = "legacy-mode", Mode = TouchControlMode.Command,
            Binding = "MODE", Visibility = TouchControlVisibility.Always };
        HookKey(ModeKey);
        RebuildControls();
        ControlCanvas.SizeChanged += (_, _) => PositionControls();
        TouchControlSettingsService.Changed += OnControlsChanged;
        ViewPad.TouchDown += ViewPad_TouchDown;
        ViewPad.TouchMove += ViewPad_TouchMove;
        ViewPad.TouchUp += ViewPad_TouchUp;
        ViewPad.LostTouchCapture += ViewPad_LostTouchCapture;
        TouchpadPad.TouchDown += TouchpadPad_TouchDown;
        TouchOverlaySettingsShortcut.Attach(TouchpadPad, OpenTouchSettings, "触摸并拖动以移动指针或调整游戏视角。");
        TouchpadPad.TouchMove += TouchpadPad_TouchMove;
        TouchpadPad.TouchUp += TouchpadPad_TouchUp;
        TouchpadPad.LostTouchCapture += TouchpadPad_LostTouchCapture;
        AddHandler(PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, e) =>
        {
            if (_hidden || !IsGameForeground) return;
            _promotionContacts.Add(e.TouchDevice.Id);
            TouchMousePromotionFilter.ProtectContacts(_overlayHwnd, true);
        }), handledEventsToo: true);
        AddHandler(PreviewTouchUpEvent, new EventHandler<TouchEventArgs>((_, e) =>
        {
            _promotionContacts.Remove(e.TouchDevice.Id);
            TouchMousePromotionFilter.ProtectContacts(_overlayHwnd, _promotionContacts.Count > 0);
        }), handledEventsToo: true);
        AddHandler(PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>((_, e) =>
        {
            if (_promotionContacts.Contains(e.TouchDevice.Id))
                TouchMousePromotionFilter.ProtectContacts(_overlayHwnd, true);
        }), handledEventsToo: true);
        AddHandler(LostTouchCaptureEvent, new EventHandler<TouchEventArgs>((_, e) =>
        {
            var device = e.TouchDevice;
            // ViewPad 会在同一轮输入后恢复意外丢失的捕获；等恢复机会结束再清理。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (device.Captured is DependencyObject captured && ReferenceEquals(GetWindow(captured), this)) return;
                if (_promotionContacts.Remove(device.Id))
                    TouchMousePromotionFilter.ProtectContacts(_overlayHwnd, _promotionContacts.Count > 0);
            }));
        }), handledEventsToo: true);
        ApplyOverlayVisibility();

        _followTimer.Tick += (_, _) => FollowGameWindow();
        Loaded += (_, _) =>
        {
            FollowGameWindow();
            if (!_closingDown) _followTimer.Start();
        };
        Closed += (_, _) => Teardown();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _overlayHwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(_overlayHwnd, GWL_EXSTYLE);
        SetWindowLong(_overlayHwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
        _source = HwndSource.FromHwnd(_overlayHwnd)
            ?? throw new InvalidOperationException("旧版触屏窗口初始化失败。");
        _source.AddHook(WindowProc);
        _inputSurface = new LegacyTouchInputSurface(this, _source,
            () => !_hidden && !_settingsOpen && IsVisible && IsGameForeground,
            OnSurfaceMouseMessage, ReleaseMouseKey, ShutdownOverlay);
        LauncherLogService.AppendLine("[旧版触屏] 已启用原生触摸入口（WM_POINTER），触点不再提升为系统鼠标移动。");
    }

    private void OnSurfaceMouseMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        var kind = TouchInputInjector.ClassifyMouseMessage(_overlayHwnd, _gameHwnd, message, lParam);
        if (kind == LegacyMouseMessageKind.Rejected) return;
        // Physical clicks on virtual keys retain native mouse capture. They must not also reach
        // the game's attack binding, even when the pointer leaves the key before release.
        if (kind == LegacyMouseMessageKind.Physical && message == 0x0202 && _mouseKey != null)
        {
            ReleaseMouseKey();
            return;
        }
        var previousKind = _surfaceMouseKind;
        _surfaceMouseKind = kind;
        try
        {
            var handled = false;
            WindowProc(_overlayHwnd, message, wParam, lParam, ref handled);
            if (handled || kind != LegacyMouseMessageKind.Physical || message is not (0x0201 or 0x0203) ||
                _hidden || !IsGameForeground ||
                !TouchInputInjector.TryGetMouseMessagePosition(_overlayHwnd, message, lParam, out var x, out var y)) return;
            if (InputHitTest(PointFromScreen(new Point(x, y))) is not Border key || !_keys.Contains(key)) return;
            ReleaseMouseKey();
            ResetModeCandidate();
            _mouseKey = key;
            if (_inputSurface?.CaptureMouse() != true) { _mouseKey = null; return; }
            if (!KeyHasTouch(key)) OnKeyPressed(key, true);
        }
        finally { _surfaceMouseKind = previousKind; }
    }

    private void ObserveWorldPhase()
    {
        var phase = _worldEvidence.Phase;
        if (phase == _lastObservedWorldPhase) return;
        var resetMode = _modeRecovery.ObservePhase(phase, Environment.TickCount64);
        if (phase == LegacyTouchWorldPhase.Outside) { _legacyInput.CursorEvidence.Reset(); _captureRecovery.Reset(); }
        else if (phase == LegacyTouchWorldPhase.Loading) { _legacyInput.CursorEvidence.ClearCapture(); _captureRecovery.BeginLoading(); }
        _lastObservedWorldPhase = phase;
        if (resetMode)
        {
            ReleaseHeld();
            _menuMode = true;
            _explicitModeUntil = _menuRequestedUntil = 0;
            ApplyOverlayVisibility();
        }
    }

    private bool TryReadVisibleGameCursor(out bool visible)
    {
        var info = new ModeCursorInfo { Size = Marshal.SizeOf<ModeCursorInfo>() };
        visible = false;
        if (!GetCursorInfo(ref info)) return false;
        var state = LegacyTouchCursorVisibilityPolicy.Resolve((info.Flags & 1) != 0,
            (info.Flags & 2) != 0, info.Cursor != IntPtr.Zero,
            LegacyTouchCursorVisibility.HasVisiblePixels(info.Cursor));
        visible = state.Showing && !state.Suppressed;
        return true;
    }

    /// <summary>观察原生约束和后端光标；只在真实 Joined 过渡后允许一次有限恢复。</summary>
    private bool? ReadGameCursorMode(out bool nativeLookConfirmed, out bool joinedRecoveryConfirmed)
    {
        nativeLookConfirmed = false;
        joinedRecoveryConfirmed = false;
        if (!IsGameForeground) return null;
        var phase = _worldEvidence.Phase;
        var now = Environment.TickCount64;
        // 覆盖层截获 WM_SETCURSOR 后，全局光标图像可能已过时。只请 GLFW 应用其
        // 当前模式的光标，不移动指针、不模拟点击，也不把发送成功当成游戏抓取。
        _gameCursorProbe.TryRefresh(_gameHwnd);
        var mode = TouchInputInjector.ReadGameMenuMode(_gameHwnd, worldJoined: phase == LegacyTouchWorldPhase.Joined,
            cursorEvidence: _legacyInput.CursorEvidence, recovery: _captureRecovery, phase: phase,
            menuRequested: now < _menuRequestedUntil);
        // 保留证据来源：稳定的一次性 Joined 恢复只可保留按键触点，不能抢走菜单拖动。
        nativeLookConfirmed = mode == false;
        if (!_menuMode) _modeRecovery.CancelJoinedRecovery();
        var cursorKnown = TryReadVisibleGameCursor(out var cursorVisible);
        joinedRecoveryConfirmed = _modeRecovery.ShouldRecoverLook(cursorKnown, cursorVisible,
            _windowStableSince >= 0 && now - _windowStableSince >= 400,
            now < _menuRequestedUntil, now);
        if (joinedRecoveryConfirmed) mode = false;
        // Loading 仍阻止单纯隐藏光标；无日志路径已被真实回中确认时，触摸抑制应保留模式。
        if (!mode.HasValue && (phase == LegacyTouchWorldPhase.Outside ||
            (phase == LegacyTouchWorldPhase.Loading && !_legacyInput.CursorEvidence.Captured && !_captureRecovery.Captured))) mode = true;
        return IsGameForeground ? mode : null;
    }

    private void FlushPendingInput()
    {
        _inputFlushOperation = null;
        if (_closingDown || _hidden || !IsGameForeground)
        {
            _legacyInput.Reset(); _legacyInput.CursorEvidence.Reset(); return;
        }
        if (!_legacyInput.FlushMenu()) { CancelViewGesture(); CancelPadGesture(); }
        _legacyInput.FlushLook();
    }

    private void ScheduleInputFlush()
    {
        if (_inputFlushOperation?.Status == DispatcherOperationStatus.Pending || _closingDown || _hidden || !IsGameForeground) return;
        // BeginInvoke remains asynchronous. Input priority can itself be deferred while native
        // touch messages remain queued; this bounded, non-repeating flush must not wait for idle.
        _inputFlushOperation = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(FlushPendingInput));
    }

    private void CancelInputFlush()
    {
        _inputFlushOperation?.Abort();
        _inputFlushOperation = null;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_closingDown || !_canTakeOver()) return IntPtr.Zero;
        if (message is WM_MOUSEACTIVATE or WM_POINTERACTIVATE)
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE / PA_NOACTIVATE，保留输入但不抢焦点。
        }
        if (message == WM_SETCURSOR)
        {
            // 不让 WPF 覆盖游戏的光标，也不把覆盖层的系统光标更新重复投递给游戏。
            // 光标是否真正可见由旧路径的形状检测判断，不依赖重放 WM_SETCURSOR。
            handled = true;
            return new IntPtr(1);
        }
        if (!TouchInputInjector.IsMouseMessage(message)) return IntPtr.Zero;

        // 旧后端在调用 SetCursorPos 时已经更新基线。触摸同步、刷新和回中产生的
        // 无来源移动再次异步投递，会被误算成手指到中心的巨大视角差值。
        // 已认证注入即使在此处丢失来源，也可通过一次性消息凭证恢复；只分类一次。
        var messageKind = _surfaceMouseKind ?? TouchInputInjector.ClassifyMouseMessage(hwnd, _gameHwnd, message, lParam);
        if (messageKind == LegacyMouseMessageKind.Rejected)
        {
            handled = true;
            return IntPtr.Zero;
        }

        // 只转交自身注入和明确的硬件鼠标输入，虚拟键触摸不产生视角位移。
        if (!_hidden && !_menuMode && IsGameForeground && message == 0x0200)
        {
            if (LegacyTouchMousePolicy.CanForwardGameplayMove(messageKind,
                HasActiveContact || TouchMousePromotionFilter.ContactsProtected(_overlayHwnd)))
                GlfwMouseMessageRelay.Forward(hwnd, _gameHwnd, message, wParam, lParam, messageKind);
            handled = true;
            return IntPtr.Zero;
        }

        // An already-issued camera packet that outlived a mode switch is never a menu pointer.
        if (messageKind == LegacyMouseMessageKind.Look)
        {
            handled = true;
            return IntPtr.Zero;
        }
        var injected = messageKind == LegacyMouseMessageKind.Injected;
        if (injected)
        {
            // SendInput 鼠标输入也会命中置顶的悬浮层。必须在 WPF 处理前截住并转给游戏，
            // 否则一次模拟点击会再次触发虚拟键/拖动，形成重复点击甚至输入循环。
            if (IsGameForeground)
                TouchInputInjector.ForwardMouseMessage(hwnd, _gameHwnd, message, wParam, lParam, messageKind);
            handled = true;
        }
        else if (!_hidden && IsGameForeground &&
            TouchInputInjector.TryGetMouseMessagePosition(hwnd, message, lParam, out var x, out var y) &&
            ReferenceEquals(InputHitTest(PointFromScreen(new Point(x, y))), ViewPad))
        {
            // 外接鼠标在画面区域直接使用原来的鼠标消息，不把鼠标移动再模拟成第二份视角位移。
            TouchInputInjector.ForwardMouseMessage(hwnd, _gameHwnd, message, wParam, lParam, messageKind);
            // 同样接受真实鼠标的离中心菜单移动；游戏自己的回中消息不能覆盖该锚点。
            if (_menuMode && message == 0x0200 && _lastClientBounds is { } bounds &&
                (Math.Abs(x - (bounds.Left + bounds.Width / 2)) >= 16 ||
                 Math.Abs(y - (bounds.Top + bounds.Height / 2)) >= 16))
                _legacyInput.CursorEvidence.MenuMoved(x, y, Environment.TickCount64);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void FollowGameWindow()
    {
        if (_closingDown) return;
        if (!_canTakeOver()) { ShutdownOverlay(); return; }
        if (_settingsOpen) return;
        // 游戏切换全屏或重建原生窗口后，继续绑定同一个进程的新窗口。
        if (_gamePid > 0 && (!IsWindow(_gameHwnd) ||
            !TouchGameWindow.BelongsTo(_gameHwnd, _gamePid) || GetForegroundWindow() != _gameHwnd))
        {
            var current = TouchGameWindow.Find(_gamePid, _gameHwnd);
            if (current != IntPtr.Zero && current != _gameHwnd)
            {
                ReleaseHeld();
                TouchMousePromotionFilter.Disable(_overlayHwnd);
                _gameHwnd = current;
                _legacyInput = new LegacyTouchInputBridge(current, _worldEvidence);
                _lastClientBounds = null;
                _menuMode = true;
                _captureRecovery.Reset();
                _gameCursorProbe.Reset();
                _lastObservedWorldPhase = _worldEvidence.Phase;
                _modeRecovery.ResetContext(_lastObservedWorldPhase);
                _windowStableSince = -1;
                _explicitModeUntil = 0;
                ApplyOverlayVisibility();
            }
        }
        if (_gameHwnd == IntPtr.Zero || !IsWindow(_gameHwnd))
        {
            ReleaseHeld();
            _inputSurface?.Hide();
            TouchMousePromotionFilter.Disable(_overlayHwnd);
            Visibility = Visibility.Hidden;
            return;
        }
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero && !IsIconic(_gameHwnd))
        {
            // 窗口切换期间 GetForegroundWindow 可短暂返回 0；不要一次采样就撤销全部触点。
            ResetModeCandidate();
            if (_foregroundMissingSince < 0) _foregroundMissingSince = Environment.TickCount64;
            if (Environment.TickCount64 - _foregroundMissingSince < 1000) return;
        }
        else _foregroundMissingSince = -1;

        if (foreground != _gameHwnd || IsIconic(_gameHwnd))
        {
            _inputSurface?.Hide();
            if (Visibility != Visibility.Hidden)
            {
                ReleaseHeld();
                _promotionContacts.Clear();
                _captureRecovery.Reset();
                _gameCursorProbe.Reset();
                _lastObservedWorldPhase = _worldEvidence.Phase;
                _modeRecovery.ResetContext(_lastObservedWorldPhase);
                _menuMode = true;
                _windowStableSince = -1;
                _explicitModeUntil = _menuRequestedUntil = 0;
                ApplyOverlayVisibility();
                Visibility = Visibility.Hidden;
                _lastClientBounds = null;
            }
            TouchMousePromotionFilter.Disable(_overlayHwnd);
            return;
        }

        // 全屏切换/调整尺寸期间客户区可能短暂不可读；保留上一帧，不让虚拟键闪退。
        if (!TouchInputInjector.TryGetClientAreaOnScreen(_gameHwnd, out var left, out var top, out var right, out var bottom) ||
            right <= left || bottom <= top) return;

        // 原生客户区像素直接对齐，避免跨屏或 DPI 缩放时重复换算坐标；不覆盖游戏标题栏。
        // 只在显示、尺寸/位置变化或置顶状态丢失时调整，避免每 100ms 两次重排整个分层窗口。
        var bounds = (Left: left, Top: top, Width: right - left, Height: bottom - top);
        var showing = Visibility != Visibility.Visible;
        if (showing || _lastClientBounds != bounds) _windowStableSince = Environment.TickCount64;
        if (showing) Visibility = Visibility.Visible;
        if (showing || _lastClientBounds != bounds || (GetWindowLong(_overlayHwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
        {
            if (_lastClientBounds.HasValue && _lastClientBounds != bounds) ReleaseHeld();
            if (SetWindowPos(_overlayHwnd, HWND_TOPMOST, left, top, bounds.Width, bounds.Height, SWP_NOACTIVATE))
                _lastClientBounds = bounds;
        }
        if (!_hidden) _inputSurface?.Show(left, top, bounds.Width, bounds.Height);
        if (_hidden) TouchMousePromotionFilter.Disable(_overlayHwnd);
        else TouchMousePromotionFilter.Enable(_gameHwnd, _overlayHwnd, OnPhysicalKeyDown);
        SyncInputMode();
        TouchMousePromotionFilter.LogDiagnostics(_gameHwnd, _menuMode);
    }

    private void ResetModeCandidate()
    {
        _modeStability.Reset();
        _modeChangeAllowedAfter = Environment.TickCount64 + ContactSettleMilliseconds;
    }

    private void SyncInputMode()
    {
        // 即使手动模式阻止原生探测，新加载/退出世界仍必须撤销上一场景的手动选择。
        if (IsGameForeground) ObserveWorldPhase();
        var now = Environment.TickCount64;
        if (_closingDown || _hidden || !IsVisible || !IsGameForeground ||
            _modeRecovery.MenuOverride.HasValue || now < _modeChangeAllowedAfter || now < _explicitModeUntil)
        {
            _modeStability.Reset();
            return;
        }
        if (!TouchInputModePolicy.ShouldProbeLegacyMode(_menuMode, HasActiveContact))
        {
            _modeStability.Reset();
            return;
        }
        if (now < _nextCursorProbeAt) return;
        _nextCursorProbeAt = now + 100;
        try
        {
            var menu = ReadGameCursorMode(out var nativeLookConfirmed, out var joinedRecoveryConfirmed);
            var preserveControls = TouchInputModePolicy.AllowLegacyLookDuringContact(_menuMode, menu,
                nativeLookConfirmed, joinedRecoveryConfirmed, HasActiveMenuGesture);
            var confirmed = _modeStability.Observe(_menuMode, menu, HasActiveContact, now,
                GameModeStabilityMilliseconds,
                now < _menuRequestedUntil ? 100 : MenuModeStabilityMilliseconds,
                allowDuringContact: preserveControls);
            if (!confirmed.HasValue) return;
            if (preserveControls && !confirmed.Value) EnterLookPreservingControls();
            else { ReleaseHeld(); _menuMode = confirmed.Value; }
            _modeRecovery.CancelJoinedRecovery();
            LauncherLogService.AppendLine($"[触屏模式] 后端={TouchGameWindow.Detect(_gameHwnd)}，状态={(_menuMode ? "菜单" : "游戏视角")}");
            ApplyOverlayVisibility();
        }
        catch { _modeStability.Reset(); }
    }

    private void AdoptLookBeforeGesture()
    {
        var now = Environment.TickCount64;
        if (!_menuMode || _modeRecovery.MenuOverride.HasValue || _viewTouch != null || _padTouch != null ||
            now < _explicitModeUntil || now < _menuRequestedUntil || !IsGameForeground) return;
        // 新落指前已有原生抓取，或 Joined 恢复已满足隐藏/窗口稳定期，直接沿用。
        // 尚未建立的手势不能因原生触点先登记而错误地发送菜单绝对定位和点击。
        var mode = ReadGameCursorMode(out var nativeLookConfirmed, out var joinedRecoveryConfirmed);
        if (!TouchInputModePolicy.AllowLegacyLookDuringContact(_menuMode, mode,
            nativeLookConfirmed, joinedRecoveryConfirmed, HasActiveMenuGesture)) return;
        EnterLookPreservingControls();
        _modeRecovery.CancelJoinedRecovery();
        _modeStability.Reset();
        ApplyOverlayVisibility();
    }

    private void EnterLookPreservingControls()
    {
        // MenuButton(false) normally flushes a queued absolute menu move. Once native capture
        // is confirmed, discard that stale coordinate before releasing the menu button.
        _legacyInput.ResetPendingInput();
        ReleaseMenuPointer();
        _menuCarryDrag = false;
        // 世界在双指操作中获得抓取时，右指从当前坐标重新起算，左手按键继续保持。
        // 标记已移动，抬指不再补发一次攻击/菜单点击。
        if (_viewTouch is { } view)
        {
            _viewDragStart = _viewDragLast = view.GetTouchPoint(ViewPad).Position;
            _viewDragIsMenu = false; _viewDragMoved = true;
        }
        if (_padTouch is { } pad)
        {
            _padStart = _padLast = pad.GetTouchPoint(TouchpadPad).Position;
            _padIsMenu = false; _padMoved = true;
            _padRemainderX = _padRemainderY = 0;
        }
        _menuMode = false;
    }

    private void ApplyOverlayVisibility()
    {
        ModeKeyText.Text = _modeRecovery.Selection switch
        {
            LegacyTouchModeSelection.Look => "手动 · 视角",
            LegacyTouchModeSelection.Menu => "手动 · 菜单",
            _ => _menuMode ? "自动 · 菜单" : "自动 · 视角"
        };
        ViewPad.Visibility = _hidden ? Visibility.Collapsed : Visibility.Visible;
        ControlCanvas.Visibility = _hidden ? Visibility.Collapsed : Visibility.Visible;
        // 游戏保持原显示设置；从窗口列表明确开启时直接显示触摸板。
        var padVisible = !_hidden && (_forceTouchpad || TouchControlSettingsService.TouchpadEnabled);
        TouchpadPad.Visibility = padVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!padVisible && _padTouch != null) CancelPadGesture();
        foreach (var key in _keys)
        {
            if (ReferenceEquals(key, RestoreKey) || key.Tag is not TouchControlDefinition c) continue;
            key.Visibility = c.Enabled && (c.Visibility == TouchControlVisibility.Always ||
                (c.Visibility == TouchControlVisibility.Menu) == _menuMode) ? Visibility.Visible : Visibility.Collapsed;
        }
        ModeKey.Visibility = _hidden ? Visibility.Collapsed : Visibility.Visible;
        RestoreKey.Visibility = _hidden ? Visibility.Visible : Visibility.Collapsed;
        PositionControls();
    }

    private void OnControlsChanged()
    {
        if (_closingDown) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(OnControlsChanged)); return; }
        RebuildControls();
    }

    private void RebuildControls()
    {
        ReleaseHeld();
        ControlCanvas.Children.Clear();
        _keys.RemoveAll(k => !ReferenceEquals(k, RestoreKey) && !ReferenceEquals(k, ModeKey));
        foreach (var control in TouchControlSettingsService.Load())
        {
            var key = new Border
            {
                Name = "Touch_" + System.Text.RegularExpressions.Regex.Replace(control.Id, "[^a-zA-Z0-9_]", "_"),
                Style = (Style)FindResource(control.Height <= 40 ? "SmallKeyStyle" : "PadKeyStyle"),
                Margin = new Thickness(0), Tag = control,
                ToolTip = control.Mode == TouchControlMode.Command ? control.Label : control.Binding,
                Child = new TextBlock { Text = control.Label.Replace("\\n", "\n"),
                    Style = (Style)FindResource(control.Height <= 40 ? "SmallKeyTextStyle" : "KeyTextStyle") }
            };
            ControlCanvas.Children.Add(key);
            HookKey(key);
        }
        PositionControls();
        ApplyOverlayVisibility();
    }

    private void PositionControls()
    {
        var width = ControlCanvas.ActualWidth;
        var height = ControlCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        // Keep the selector near the top even in narrow/scaled windows. Reserve its row only
        // when top-anchored controls would overlap it; persisted custom coordinates stay intact.
        var modeBounds = new Rect(Math.Max(0, (width - ModeKey.Width) / 2), 8, Math.Min(width, ModeKey.Width), ModeKey.Height);
        var topRowOffset = 0.0;
        foreach (var c in ControlCanvas.Children.OfType<Border>()
            .Where(k => k.Visibility == Visibility.Visible).Select(k => k.Tag).OfType<TouchControlDefinition>())
        {
            if (c.Anchor is not (TouchControlAnchor.TopLeft or TouchControlAnchor.TopRight)) continue;
            var w = Math.Min(width, c.Width * _buttonScale);
            var x = c.Anchor == TouchControlAnchor.TopRight ? width - c.X * _buttonScale - w : c.X * _buttonScale;
            var keyBounds = new Rect(Math.Clamp(x, 0, Math.Max(0, width - w)), c.Y * _buttonScale,
                w, Math.Min(height, c.Height * _buttonScale));
            keyBounds.Inflate(4, 4);
            if (keyBounds.IntersectsWith(modeBounds)) { topRowOffset = modeBounds.Bottom + 4; break; }
        }
        foreach (var key in ControlCanvas.Children.OfType<Border>())
        {
            if (key.Tag is not TouchControlDefinition c) continue;
            key.Width = Math.Min(width, c.Width * _buttonScale);
            key.Height = Math.Min(height, c.Height * _buttonScale);
            var x = c.X * _buttonScale;
            var y = c.Y * _buttonScale;
            if (c.Anchor is TouchControlAnchor.TopLeft or TouchControlAnchor.TopRight) y += topRowOffset;
            if (c.Anchor is TouchControlAnchor.TopRight or TouchControlAnchor.BottomRight) x = width - x - key.Width;
            else if (c.Anchor == TouchControlAnchor.BottomCenter) x += (width - key.Width) / 2;
            if (c.Anchor is TouchControlAnchor.BottomLeft or TouchControlAnchor.BottomRight or TouchControlAnchor.BottomCenter)
                y = height - y - key.Height;
            Canvas.SetLeft(key, Math.Clamp(x, 0, Math.Max(0, width - key.Width)));
            Canvas.SetTop(key, Math.Clamp(y, 0, Math.Max(0, height - key.Height)));
            Panel.SetZIndex(key, c.ZIndex);
        }
    }

    // ===================== 虚拟按键 =====================

    private void HookKey(Border key)
    {
        _keys.Add(key);
        TouchOverlaySettingsShortcut.Attach(key, OpenTouchSettings,
            key.Tag is TouchControlDefinition control ? TouchOverlaySettingsShortcut.Describe(control) : "触屏虚拟按键。");
        key.TouchDown += Key_TouchDown;
        key.TouchUp += (_, e) => { e.Handled = true; ReleaseKeyTouch(e.TouchDevice.Id); };
        key.LostTouchCapture += (_, e) => RecoverKeyTouch(e.TouchDevice, key);
        key.MouseLeftButtonDown += Key_MouseDown;
        key.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (e.StylusDevice == null) ReleaseMouseKey();
        };
        key.LostMouseCapture += (_, _) => { if (ReferenceEquals(_mouseKey, key)) ReleaseMouseKey(); };
    }

    private bool KeyHasTouch(Border key) => _touchKeys.Values.Any(value => ReferenceEquals(value.Key, key));

    private void Key_TouchDown(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_closingDown || !IsGameForeground || sender is not Border key || _touchKeys.ContainsKey(e.TouchDevice.Id)) return;
        ResetModeCandidate();
        var alreadyDown = KeyHasTouch(key) || ReferenceEquals(_mouseKey, key);
        _touchKeys[e.TouchDevice.Id] = (e.TouchDevice, key);
        if (!key.CaptureTouch(e.TouchDevice)) { _touchKeys.Remove(e.TouchDevice.Id); return; }
        if (!alreadyDown) OnKeyPressed(key, true);
    }

    private void ReleaseKeyTouch(int id)
    {
        if (!_touchKeys.Remove(id, out var touch)) return;
        ResetModeCandidate();
        if (ReferenceEquals(touch.Device.Captured, touch.Key)) touch.Key.ReleaseTouchCapture(touch.Device);
        if (!KeyHasTouch(touch.Key) && !ReferenceEquals(_mouseKey, touch.Key)) OnKeyPressed(touch.Key, false);
    }

    private bool CanRecoverContact(TouchDevice touch)
        => !_closingDown && !_hidden && !_settingsOpen && IsGameForeground &&
           (!LegacyTouchInputSurface.IsNativeContact(touch) || _inputSurface?.OwnsContact(touch) == true);

    private void RecoverKeyTouch(TouchDevice touch, Border key)
    {
        LegacyTouchContactRecovery.Schedule(touch, key,
            () => _touchKeys.TryGetValue(touch.Id, out var held) && ReferenceEquals(held.Device, touch) && ReferenceEquals(held.Key, key),
            () => CanRecoverContact(touch), () => ReleaseKeyTouch(touch.Id));
    }

    private void Key_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.StylusDevice != null || TouchInputInjector.IsInjectedMouseMessage ||
            _closingDown || !IsGameForeground || sender is not Border key) return;
        ReleaseMouseKey();
        ResetModeCandidate();
        _mouseKey = key;
        if (!key.CaptureMouse()) { _mouseKey = null; return; }
        if (!KeyHasTouch(key)) OnKeyPressed(key, true);
    }

    private void ReleaseMouseKey()
    {
        var key = _mouseKey;
        _mouseKey = null;
        _inputSurface?.ReleaseMouse();
        if (key == null) return;
        ResetModeCandidate();
        if (key.IsMouseCaptured) key.ReleaseMouseCapture();
        if (!KeyHasTouch(key)) OnKeyPressed(key, false);
    }

    private void OnKeyPressed(Border sender, bool down)
    {
        if (_releasingHeld || (down && (_closingDown || !IsGameForeground))) return;
        if (sender.Tag is not TouchControlDefinition control) return;
        sender.Opacity = down ? 0.55 : 1.0;
        var name = control.Binding.Trim().ToUpperInvariant();
        if (control.Mode == TouchControlMode.Command)
        {
            if (down) HandleUiCommand(name);
            return;
        }
        if (control.Mode == TouchControlMode.Tap)
        {
            if (!down) return;
            if (control.MenuAction != TouchMenuAction.None) ApplyMenuAction(control.MenuAction);
            if (name is "WHEEL_UP" or "WHEEL_DOWN") TouchInputInjector.MouseWheel(name == "WHEEL_UP" ? 1 : -1);
            else { AcquireBinding(sender, name); ReleaseBinding(sender); }
            return;
        }
        if (control.Mode == TouchControlMode.Hold)
        {
            if (down) AcquireBinding(sender, name); else ReleaseBinding(sender);
            return;
        }
        if (!down) return;
        if (_activeBindings.ContainsKey(sender)) ReleaseBinding(sender); else AcquireBinding(sender, name);
        sender.Background = new SolidColorBrush(_activeBindings.ContainsKey(sender)
            ? Color.FromArgb(0xB3, 0x2E, 0x7D, 0x32) : Color.FromArgb(0x66, 0, 0, 0));
        sender.Opacity = 1;
    }

    private void AcquireBinding(Border owner, string name)
    {
        if (_activeBindings.ContainsKey(owner)) return;
        TouchInputInjector.MouseButton? button = name switch
        {
            "MOUSE_L" => TouchInputInjector.MouseButton.Left, "MOUSE_R" => TouchInputInjector.MouseButton.Right,
            "MOUSE_M" => TouchInputInjector.MouseButton.Middle, _ => null
        };
        var keys = button.HasValue ? Array.Empty<ushort>() : name.Split('+', StringSplitOptions.TrimEntries)
            .Select(TouchInputInjector.TryMapKey).Where(k => k.HasValue).Select(k => k!.Value).Distinct().ToArray();
        _activeBindings[owner] = (keys, button);
        foreach (var key in keys)
        {
            _keyReferences.TryGetValue(key, out var count); _keyReferences[key] = count + 1;
            if (count == 0) { _heldKeys.Add(key); TouchInputInjector.KeyDown(key); }
        }
        if (button is { } b)
        {
            _buttonReferences.TryGetValue(b, out var count); _buttonReferences[b] = count + 1;
            if (count == 0) { _heldButtons.Add(b); TouchInputInjector.MouseDown(b); }
        }
    }

    private void ReleaseBinding(Border owner)
    {
        if (!_activeBindings.Remove(owner, out var binding)) return;
        foreach (var key in Enumerable.Reverse(binding.Keys))
        {
            if (!_keyReferences.TryGetValue(key, out var count)) continue;
            if (count > 1) _keyReferences[key] = count - 1;
            else { _keyReferences.Remove(key); _heldKeys.Remove(key); TouchInputInjector.KeyUp(key); }
        }
        if (binding.Button is { } b && _buttonReferences.TryGetValue(b, out var references))
        {
            if (references > 1) _buttonReferences[b] = references - 1;
            else { _buttonReferences.Remove(b); _heldButtons.Remove(b); TouchInputInjector.MouseUp(b); }
        }
    }

    private void ApplyMenuAction(TouchMenuAction action)
    {
        ReleaseHeld();
        var manualMenu = _modeRecovery.ApplyMenuAction(action, _menuMode);
        if (manualMenu.HasValue)
        {
            _menuMode = manualMenu.Value;
            ApplyOverlayVisibility();
        }
        // 自动模式下按键意图仍不是抓取确认；手动模式按明确的菜单动作保持手动选择。
        var now = Environment.TickCount64;
        _menuTransitionUntil = now + 180;
        _explicitModeUntil = now + 180;
        _menuRequestedUntil = now + 2000;
        _modeStability.Reset();
    }

    private void OnPhysicalKeyDown(ushort vk)
    {
        if (_closingDown || _settingsOpen || _hidden || !IsGameForeground) return;
        var control = _keys.Select(k => k.Tag).OfType<TouchControlDefinition>().FirstOrDefault(c => c.Enabled &&
            c.MenuAction != TouchMenuAction.None && c.Mode == TouchControlMode.Tap &&
            TouchInputInjector.MatchesKeyBinding(c.Binding, vk));
        if (control != null) ApplyMenuAction(control.MenuAction);
        else if (vk == 0x1B) ApplyMenuAction(TouchMenuAction.Toggle);
    }

    private void HandleUiCommand(string command)
    {
        switch (command)
        {
            case "SENS+": _sensitivity = Math.Min(4.0, _sensitivity + 0.2); break;
            case "SENS-": _sensitivity = Math.Max(0.4, _sensitivity - 0.2); break;
            case "GRAB": if (_gameHwnd != IntPtr.Zero) SetForegroundWindow(_gameHwnd); break;
            case "HIDE": SetHidden(!_hidden); break;
            case "SHOW": SetHidden(false); break;
            case "MODE":
                ReleaseHeld();
                _modeRecovery.Cycle();
                if (_modeRecovery.MenuOverride is { } selectedMenu) _menuMode = selectedMenu;
                _captureRecovery.Reset();
                _explicitModeUntil = _menuRequestedUntil = 0;
                _menuTransitionUntil = Environment.TickCount64 + 180;
                ApplyOverlayVisibility();
                LauncherLogService.AppendLine($"[旧版触屏] 用户选择模式：{ModeKeyText.Text}");
                break;
            case "MENU":
                ReleaseHeld(); _menuMode = !_menuMode; _captureRecovery.Reset();
                _modeRecovery.Select(_menuMode ? LegacyTouchModeSelection.Menu : LegacyTouchModeSelection.Look);
                _explicitModeUntil = _menuRequestedUntil = 0;
                _menuTransitionUntil = Environment.TickCount64 + 180;
                ApplyOverlayVisibility(); break;
            case "KEYBOARD":
                ReleaseHeld();
                try { ScreenKeyboardService.Show(); }
                catch (Exception ex) { ToastService.ShowWarning("屏幕键盘打开失败：" + ex.Message); }
                break;
            case "SETTINGS":
                // 离开当前触摸回调再显示可激活的设置窗口，防止遗留触摸捕获。
                ReleaseHeld();
                _ = Dispatcher.BeginInvoke(new Action(OpenTouchSettings));
                break;
        }
    }

    private void OpenTouchSettings()
    {
        if (_settingsOpen || _closingDown) return;
        _settingsOpen = true;
        ReleaseHeld();
        _lastObservedWorldPhase = _worldEvidence.Phase;
        _modeRecovery.ResetContext(_lastObservedWorldPhase);
        _menuMode = true;
        _windowStableSince = -1;
        ApplyOverlayVisibility();
        _inputSurface?.Hide();
        TouchMousePromotionFilter.Disable(_overlayHwnd);
        Visibility = Visibility.Hidden;
        try { new TouchControlsSettingsWindow { Topmost = true }.ShowDialog(); }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[触屏设置] 打开失败：" + ex.Message);
            ToastService.ShowWarning("触屏设置打开失败：" + ex.Message);
        }
        finally
        {
            _settingsOpen = false;
            _lastClientBounds = null;
            if (!_closingDown && IsWindow(_gameHwnd)) { SetForegroundWindow(_gameHwnd); FollowGameWindow(); }
        }
    }

    private void SetHidden(bool hidden)
    {
        ReleaseHeld();
        _promotionContacts.Clear();
        _hidden = hidden;
        ResetModeCandidate();
        ApplyOverlayVisibility();
        if (hidden) _inputSurface?.Hide();
        else if (IsGameForeground && _lastClientBounds is { } bounds)
            _inputSurface?.Show(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        if (hidden || !IsGameForeground) TouchMousePromotionFilter.Disable(_overlayHwnd);
        else TouchMousePromotionFilter.Enable(_gameHwnd, _overlayHwnd, OnPhysicalKeyDown);
    }

    // ===================== 菜单指针 / 视角 =====================

    private void ViewPad_TouchDown(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_hidden || _closingDown || !IsGameForeground || _viewTouch != null) return;
        if (Environment.TickCount64 < _menuTransitionUntil) return;
        SyncInputMode();
        AdoptLookBeforeGesture();
        // 落指沿用已确认模式。第二根手指不得重新探测并释放第一根手指的前进键。
        ResetModeCandidate();
        _viewTouch = e.TouchDevice;
        _viewDragStart = _viewDragLast = e.GetTouchPoint(ViewPad).Position;
        _viewDragTime = DateTime.UtcNow;
        _viewDragMoved = false;
        _viewDragIsMenu = _menuMode;
        _menuCarryDrag = false;
        if (_padTouch == null) _legacyInput.ResetPendingInput();
        if (!ViewPad.CaptureTouch(e.TouchDevice)) { _viewTouch = null; return; }
        if (_viewDragIsMenu)
        {
            if (!MoveMenuPointer(_viewDragStart) || !PressMenuPointer()) CancelViewGesture();
        }
    }

    private void ViewPad_LostTouchCapture(object? sender, TouchEventArgs e)
    {
        var touch = _viewTouch;
        if (touch == null || touch.Id != e.TouchDevice.Id) return;
        LegacyTouchContactRecovery.Schedule(touch, ViewPad, () => ReferenceEquals(_viewTouch, touch),
            () => CanRecoverContact(touch), CancelViewGesture);
    }

    private bool ContinueViewGesture(TouchDevice device)
    {
        if (_viewTouch?.Id != device.Id) return false;
        if (_hidden || _closingDown || IsIconic(_gameHwnd)) { CancelViewGesture(); return false; }
        var foreground = GetForegroundWindow();
        if (foreground != _gameHwnd)
        {
            if (foreground != IntPtr.Zero) CancelViewGesture();
            return false;
        }
        // 一次手势沿用落指时确认的模式。触摸造成的光标隐藏或临时约束变化不应
        // 中途松开鼠标；显式按 ESC/E、切换窗口和关闭悬浮层仍会统一取消手势。
        return true;
    }

    private void ViewPad_TouchMove(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (!ContinueViewGesture(e.TouchDevice))
        {
            // 短暂没有前台窗口时丢弃这一帧位移，恢复后不积累成一次突然转向。
            if (_viewTouch?.Id == e.TouchDevice.Id) _viewDragLast = e.GetTouchPoint(ViewPad).Position;
            return;
        }
        var point = e.GetTouchPoint(ViewPad).Position;
        var moved = (point - _viewDragStart).Length >= MenuDragThreshold;
        if (_viewDragIsMenu && !_viewDragMoved && moved && _menuPointerDown &&
            (DateTime.UtcNow - _viewDragTime).TotalMilliseconds >= MenuPickUpHoldMilliseconds)
        {
            // Java 背包的拿起和放下通常是两次点击。长按后再拖动时，先在原位置
            // 完成第一次点击，物品随指针移动，松手时在目标格完成第二次点击。
            // 立即开始拖动仍保持原生按住行为，用于创造栏滚动条、菜单滑块和分配物品。
            ReleaseMenuPointer();
            _menuCarryDrag = true;
        }
        if (moved) _viewDragMoved = true;
        if (_viewDragIsMenu)
        {
            if (!MoveMenuPointer(point, defer: true)) { CancelViewGesture(); return; }
        }
        else ApplyLook(ViewPad.PointToScreen(point) - ViewPad.PointToScreen(_viewDragLast));
        _viewDragLast = point;
    }

    private void ViewPad_TouchUp(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (!ContinueViewGesture(e.TouchDevice))
        {
            if (_viewTouch?.Id == e.TouchDevice.Id) CancelViewGesture();
            return;
        }
        var point = e.GetTouchPoint(ViewPad).Position;
        var tap = !_viewDragIsMenu && !_viewDragMoved && (point - _viewDragStart).Length < 12 &&
            (DateTime.UtcNow - _viewDragTime).TotalMilliseconds < 300;
        if (_viewDragIsMenu)
        {
            if (!MoveMenuPointer(point)) { CancelViewGesture(); return; }
            // 只有真正的长按搬运手势才补放置点击，普通单击与原生拖动绝不多点一次。
            // 移出游戏窗口后松手保留拿起状态，不向窗口外投递放置/丢弃点击。
            if (_menuCarryDrag && _viewDragMoved && IsMenuPointerInsideGame())
                PressMenuPointer();
        }
        if (!_viewDragIsMenu) _legacyInput.FlushLook();
        CancelViewGesture();
        // 菜单不使用短按判定：停留、滑块拖动、下拉选择也必须有对应的按下/抬起。
        if (tap && !_heldButtons.Contains(TouchInputInjector.MouseButton.Left))
            TouchInputInjector.MouseClick(TouchInputInjector.MouseButton.Left);
    }

    private bool MoveMenuPointer(Point point, bool defer = false)
    {
        var screen = ViewPad.PointToScreen(point);
        var x = (int)Math.Round(screen.X); var y = (int)Math.Round(screen.Y);
        var forceMenu = _modeRecovery.Selection == LegacyTouchModeSelection.Menu;
        if (!(defer ? _legacyInput.QueueMenu(x, y, _menuPointerDown, forceMenu)
            : _legacyInput.MoveMenu(x, y, _menuPointerDown, forceMenu))) return false;
        if (defer) ScheduleInputFlush();
        _menuPointerScreen = screen;
        return true;
    }

    private bool PressMenuPointer()
    {
        if (_menuPointerDown) return true;
        _menuPointerDown = _legacyInput.MenuButton(
            (int)Math.Round(_menuPointerScreen.X), (int)Math.Round(_menuPointerScreen.Y), true);
        return _menuPointerDown;
    }

    private void ReleaseMenuPointer()
    {
        if (!_menuPointerDown) return;
        _menuPointerDown = false;
        _legacyInput.MenuButton(
            (int)Math.Round(_menuPointerScreen.X), (int)Math.Round(_menuPointerScreen.Y), false);
    }

    private bool IsMenuPointerInsideGame()
    {
        return TouchInputInjector.TryGetClientAreaOnScreen(_gameHwnd, out var left, out var top,
                   out var right, out var bottom) &&
               _menuPointerScreen.X >= left && _menuPointerScreen.X < right &&
               _menuPointerScreen.Y >= top && _menuPointerScreen.Y < bottom;
    }

    // ===================== 触摸板（相对位移区） =====================
    // 手指在触摸板上滑动：菜单里移动指针（相对位移，限制在游戏客户区内），游戏里转动视角；
    // 轻点 = 鼠标左键。它与全屏的 ViewPad 互相独立，各自占一根手指，可同时使用。

    private TouchDevice? _padTouch;
    private Point _padStart;
    private Point _padLast;
    private DateTime _padTime;
    private bool _padMoved;
    private bool _padIsMenu;
    private bool _padPointerReady;
    private double _padRemainderX;
    private double _padRemainderY;

    private void TouchpadPad_TouchDown(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_hidden || _closingDown || !IsGameForeground || _padTouch != null) return;
        if (Environment.TickCount64 < _menuTransitionUntil) return;
        SyncInputMode();
        AdoptLookBeforeGesture();
        ResetModeCandidate();
        _padTouch = e.TouchDevice;
        _padStart = _padLast = e.GetTouchPoint(TouchpadPad).Position;
        _padTime = DateTime.UtcNow;
        _padMoved = false;
        _padIsMenu = _menuMode;
        _padRemainderX = _padRemainderY = 0;
        if (!TouchpadPad.CaptureTouch(e.TouchDevice)) { _padTouch = null; return; }
        if (_padIsMenu && !_padPointerReady && TouchInputInjector.TryGetClientAreaOnScreen(_gameHwnd,
                out var left, out var top, out var right, out var bottom))
        {
            // 第一次在菜单里使用触摸板：指针从游戏画面中心开始。
            _menuPointerScreen = new Point((left + right) / 2.0, (top + bottom) / 2.0);
            _padPointerReady = MoveMenuPointer(ViewPad.PointFromScreen(_menuPointerScreen));
        }
    }

    private void TouchpadPad_TouchMove(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_padTouch?.Id != e.TouchDevice.Id) return;
        if (_hidden || _closingDown || !IsGameForeground) { CancelPadGesture(); return; }
        var point = e.GetTouchPoint(TouchpadPad).Position;
        var delta = TouchpadPad.PointToScreen(point) - TouchpadPad.PointToScreen(_padLast);
        _padLast = point;
        if ((point - _padStart).Length >= 6) _padMoved = true;
        if (_padIsMenu)
        {
            if (!_padPointerReady) return;
            var fx = delta.X * _sensitivity + _padRemainderX;
            var fy = delta.Y * _sensitivity + _padRemainderY;
            var dx = Math.Truncate(fx);
            var dy = Math.Truncate(fy);
            _padRemainderX = fx - dx;
            _padRemainderY = fy - dy;
            if (!TouchInputInjector.TryGetClientAreaOnScreen(_gameHwnd, out var left, out var top, out var right, out var bottom))
                return;
            var target = new Point(
                Math.Clamp(_menuPointerScreen.X + dx, left, right - 1),
                Math.Clamp(_menuPointerScreen.Y + dy, top, bottom - 1));
            MoveMenuPointer(ViewPad.PointFromScreen(target), defer: true);
        }
        else ApplyLook(delta);
    }

    private void TouchpadPad_TouchUp(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_padTouch?.Id != e.TouchDevice.Id) return;
        var tap = !_padMoved && (DateTime.UtcNow - _padTime).TotalMilliseconds < 300;
        var wasMenu = _padIsMenu;
        if (wasMenu) _legacyInput.FlushMenu();
        else _legacyInput.FlushLook();
        CancelPadGesture();
        if (!tap || _hidden || _closingDown || !IsGameForeground) return;
        if (wasMenu)
        {
            // 与视角手势共用指针按下状态；有视角手势在进行时不插入点击。
            if (_viewTouch != null || !_padPointerReady) return;
            if (PressMenuPointer()) ReleaseMenuPointer();
        }
        else if (!_heldButtons.Contains(TouchInputInjector.MouseButton.Left))
            TouchInputInjector.MouseClick(TouchInputInjector.MouseButton.Left);
    }

    private void TouchpadPad_LostTouchCapture(object? sender, TouchEventArgs e)
    {
        var touch = _padTouch;
        if (touch?.Id != e.TouchDevice.Id) return;
        LegacyTouchContactRecovery.Schedule(touch, TouchpadPad, () => ReferenceEquals(_padTouch, touch),
            () => CanRecoverContact(touch), CancelPadGesture);
    }

    private void CancelPadGesture()
    {
        var touch = _padTouch;
        _padTouch = null;
        if (touch != null) ResetModeCandidate();
        _padRemainderX = _padRemainderY = 0;
        if (_viewTouch == null)
        {
            if (IsGameForeground && !_hidden) _legacyInput.ResetPendingInput();
            else _legacyInput.Reset();
        }
        if (touch != null && ReferenceEquals(touch.Captured, TouchpadPad)) TouchpadPad.ReleaseTouchCapture(touch);
    }

    private void ApplyLook(Vector screenDelta)
    {
        // 合并同一输入时段的采样并保留小数，不等待 WPF 重绘。
        _legacyInput.MoveLook(screenDelta.X * _sensitivity, screenDelta.Y * _sensitivity);
        ScheduleInputFlush();
    }

    private void CancelViewGesture()
    {
        var touch = _viewTouch;
        _viewTouch = null;
        if (touch != null) ResetModeCandidate();
        ReleaseMenuPointer();
        _menuCarryDrag = false;
        if (_padTouch == null)
        {
            if (IsGameForeground && !_hidden) _legacyInput.ResetPendingInput();
            else _legacyInput.Reset();
        }
        if (touch != null && ReferenceEquals(touch.Captured, ViewPad)) ViewPad.ReleaseTouchCapture(touch);
    }

    // ===================== 收尾 =====================

    private void ReleaseHeld()
    {
        CancelInputFlush();
        _legacyInput.Reset();
        if (_releasingHeld) return;
        // 模式切换/失焦/隐藏会主动取消捕获，不保证还收到 PreviewTouchUp。
        // 原触点仍在屏幕时，客户区几何过滤及短尾保护继续挡住合成鼠标事件。
        _promotionContacts.Clear();
        TouchMousePromotionFilter.ProtectContacts(_overlayHwnd, false);
        _releasingHeld = true;
        try
        {
            ResetModeCandidate();
            CancelViewGesture();
            CancelPadGesture();
            TouchInputInjector.ReleaseAll(_heldKeys, _heldButtons);
            _heldKeys.Clear(); _heldButtons.Clear();
            _keyReferences.Clear(); _buttonReferences.Clear(); _activeBindings.Clear();
            var touches = _touchKeys.Values.ToList();
            _touchKeys.Clear();
            foreach (var touch in touches)
                if (ReferenceEquals(touch.Device.Captured, touch.Key)) touch.Key.ReleaseTouchCapture(touch.Device);
            ReleaseMouseKey();
            foreach (var key in _keys)
            {
                key.Opacity = 1.0;
                if (key.Tag is TouchControlDefinition { Mode: TouchControlMode.Toggle })
                    key.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
            }
        }
        finally { _releasingHeld = false; }
    }

    private void Teardown()
    {
        if (_closingDown) return;
        _closingDown = true;
        _followTimer.Stop();
        CancelInputFlush();
        TouchControlSettingsService.Changed -= OnControlsChanged;
        ReleaseHeld();
        TouchMousePromotionFilter.Disable(_overlayHwnd);
        _inputSurface?.Dispose();
        _inputSurface = null;
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    public void ShutdownOverlay()
    {
        Teardown();
        try { Close(); } catch { /* 已经关闭 */ }
    }
}
