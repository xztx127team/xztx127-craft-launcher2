using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class FeatureSearchBox
{
    [Flags]
    private enum ResizeEdge
    {
        None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8,
        TopLeft = Left | Top, TopRight = Right | Top, BottomLeft = Left | Bottom, BottomRight = Right | Bottom
    }
    private FrameworkElement? _layoutHost;
    private FeatureSearchLayout _entryLayout = FeatureSearchLayout.Default;
    private FeatureSearchPlacement _entryPlacement;
    private FeatureSearchLayout _gestureOriginalLayout;
    private FeatureSearchPlacement _gestureOriginalPlacement;
    private Point _gestureStart;
    private ResizeEdge _resizeEdge;
    private DispatcherTimer? _longPressTimer;
    private readonly FeatureSearchLongPressDrag _positionDrag = new();
    private long _gestureStartedAt;
    private bool _gestureCaptured;
    private bool _endingGesture;
    private bool _normalPressAndHoldEnabled;
    private bool _normalFlicksEnabled;
    internal bool IsEditingLayout { get; private set; }
    internal event Action<FeatureSearchLayout, bool>? LayoutEdited;

    private void InitializeLayoutEditing()
    {
        // ButtonBase 和模板子控件可能已处理鼠标事件；编辑手势仍须收到完整的按下/移动/释放。
        SearchEntry.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(LayoutEditor_MouseDown), true);
        SearchEntry.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(LayoutEditor_MouseMove), true);
        SearchEntry.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(LayoutEditor_MouseUp), true);
        SearchEntry.LostMouseCapture += (_, e) =>
        {
            if (!_endingGesture && ReferenceEquals(e.OriginalSource, SearchEntry) && !SearchEntry.IsMouseCaptured)
                CancelEditGesture();
        };
        SearchEntry.MouseLeave += (_, _) => { if (!_gestureCaptured) SearchEntry.Cursor = Cursors.Arrow; };
    }

    internal void UpdateLayoutGeometry(FeatureSearchLayout layout, FeatureSearchPlacement placement,
        FrameworkElement? host, bool floating)
    {
        _entryLayout = layout.Normalize();
        _entryPlacement = placement;
        if (!ReferenceEquals(_layoutHost, host))
        {
            if (_layoutHost != null) _layoutHost.SizeChanged -= LayoutHost_SizeChanged;
            _layoutHost = host;
            if (_layoutHost != null) _layoutHost.SizeChanged += LayoutHost_SizeChanged;
        }
        _floating = floating;
    }

    private void LayoutHost_SizeChanged(object sender, SizeChangedEventArgs e) => _owner?.RefreshFeatureSearchForHostResize();

    internal void SetLayoutEditing(bool enabled)
    {
        if (!enabled) CancelEditGesture();
        if (enabled && !IsEditingLayout)
        {
            _normalPressAndHoldEnabled = Stylus.GetIsPressAndHoldEnabled(SearchEntry);
            _normalFlicksEnabled = Stylus.GetIsFlicksEnabled(SearchEntry);
            // WPF 的触笔长按右键/轻拂识别会延迟或中断提升后的鼠标拖动。
            // 仅在编辑布局期间使用本控件自己的长按手势。
            Stylus.SetIsPressAndHoldEnabled(SearchEntry, false);
            Stylus.SetIsFlicksEnabled(SearchEntry, false);
        }
        else if (!enabled && IsEditingLayout)
        {
            Stylus.SetIsPressAndHoldEnabled(SearchEntry, _normalPressAndHoldEnabled);
            Stylus.SetIsFlicksEnabled(SearchEntry, _normalFlicksEnabled);
        }
        IsEditingLayout = enabled;
        LayoutEditFrame.Visibility = enabled && SearchEntry.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        SearchEntry.Cursor = enabled ? Cursors.SizeAll : Cursors.Arrow;
        SearchEntry.ToolTip = enabled
            ? "编辑搜索框：拖动边缘调整大小；按住中间 500 毫秒后拖动位置；Esc 取消本次拖动。完成后保存设置。"
            : "搜索功能（Ctrl+K 或 Ctrl+F）";
        if (enabled) { Close(); EmptyClickHint.Visibility = Visibility.Collapsed; }
    }

    internal bool CancelEditGesture()
    {
        if (!_gestureCaptured) return false;
        FinishLayoutGesture(cancel: true);
        return true;
    }

    private ResizeEdge EdgeAt(Point position)
    {
        const double hit = 7;
        ResizeEdge edge = ResizeEdge.None;
        if (position.X <= hit) edge |= ResizeEdge.Left;
        else if (position.X >= SearchEntry.ActualWidth - hit) edge |= ResizeEdge.Right;
        if (position.Y <= hit) edge |= ResizeEdge.Top;
        else if (position.Y >= SearchEntry.ActualHeight - hit) edge |= ResizeEdge.Bottom;
        return edge;
    }

    private static Cursor CursorFor(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
        ResizeEdge.Top or ResizeEdge.Bottom => Cursors.SizeNS,
        ResizeEdge.TopLeft or ResizeEdge.BottomRight => Cursors.SizeNWSE,
        ResizeEdge.TopRight or ResizeEdge.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.SizeAll
    };

    private void LayoutEditor_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEditingLayout || _layoutHost == null || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true; // 编辑状态的短按也不会搜索或触发 Button.Click。
        CancelEditGesture();
        SearchEntry.Focus();
        _gestureStart = e.GetPosition(_layoutHost);
        _gestureOriginalLayout = _entryLayout;
        _gestureOriginalPlacement = _entryPlacement;
        _resizeEdge = EdgeAt(e.GetPosition(SearchEntry));
        _positionDrag.End();
        _gestureStartedAt = Stopwatch.GetTimestamp();
        _gestureCaptured = SearchEntry.CaptureMouse();
        if (!_gestureCaptured) return;
        SearchEntry.Cursor = CursorFor(_resizeEdge);
        if (_resizeEdge != ResizeEdge.None) return;
        _positionDrag.Begin(_gestureStart.X, _gestureStart.Y);
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = FeatureSearchLongPressDrag.HoldDuration };
        _longPressTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_longPressTimer, timer)) return;
            if (_gestureCaptured && Mouse.LeftButton == MouseButtonState.Pressed)
                _positionDrag.TryActivate(Stopwatch.GetElapsedTime(_gestureStartedAt));
        };
        timer.Start();
    }

    private void LayoutEditor_MouseMove(object sender, MouseEventArgs e)
    {
        if (!IsEditingLayout) return;
        if (!_gestureCaptured) { SearchEntry.Cursor = CursorFor(EdgeAt(e.GetPosition(SearchEntry))); return; }
        e.Handled = true;
        if (_layoutHost == null || e.LeftButton != MouseButtonState.Pressed) { CancelEditGesture(); return; }
        var point = e.GetPosition(_layoutHost);
        double dx = point.X - _gestureStart.X, dy = point.Y - _gestureStart.Y;
        if (_resizeEdge == ResizeEdge.None)
        {
            // Input 可以先于定时器 Tick 到达。用单调时钟检查真正的 500ms，
            // 避免已长按的第一次移动反而把整个手势永久取消。
            var delta = _positionDrag.Move(point.X, point.Y, Stopwatch.GetElapsedTime(_gestureStartedAt));
            if (delta == null) return;
            dx = delta.Value.X;
            dy = delta.Value.Y;
        }
        double hostWidth = Math.Max(1, _layoutHost.ActualWidth), hostHeight = Math.Max(1, _layoutHost.ActualHeight);
        double topBound = _floating ? FeatureSearchLayoutPolicy.FloatingTop : 0;
        var original = _gestureOriginalPlacement;
        double left = original.X, top = original.Y, right = left + original.Width, bottom = top + original.Height;
        if (_positionDrag.IsDragging) { left += dx; right += dx; top += dy; bottom += dy; }
        else
        {
            double minWidth = Math.Min(160, hostWidth), minHeight = Math.Min(28, hostHeight);
            if (_resizeEdge.HasFlag(ResizeEdge.Left))
                left = Math.Clamp(left + dx, Math.Max(0, right - 720), Math.Max(0, right - minWidth));
            if (_resizeEdge.HasFlag(ResizeEdge.Right))
                right = Math.Clamp(right + dx, left + minWidth, Math.Max(left + minWidth, Math.Min(hostWidth, left + 720)));
            if (_resizeEdge.HasFlag(ResizeEdge.Top))
                top = Math.Clamp(top + dy, Math.Max(topBound, bottom - 120), Math.Max(topBound, bottom - minHeight));
            if (_resizeEdge.HasFlag(ResizeEdge.Bottom))
                bottom = Math.Clamp(bottom + dy, top + minHeight, Math.Max(top + minHeight, Math.Min(hostHeight, top + 120)));
        }
        var placement = new FeatureSearchPlacement(left, top, right - left, bottom - top);
        var layout = FeatureSearchLayoutPolicy.FromPlacement(placement, _gestureOriginalLayout, _floating, hostWidth, hostHeight);
        if (layout != _entryLayout) LayoutEdited?.Invoke(layout, false);
    }

    private void LayoutEditor_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsEditingLayout || !_gestureCaptured) return;
        e.Handled = true;
        FinishLayoutGesture(cancel: false);
    }

    private void FinishLayoutGesture(bool cancel)
    {
        var changed = _entryLayout != _gestureOriginalLayout;
        _longPressTimer?.Stop();
        _longPressTimer = null;
        _gestureCaptured = false;
        _positionDrag.End();
        _endingGesture = true;
        try { if (SearchEntry.IsMouseCaptured) SearchEntry.ReleaseMouseCapture(); }
        finally { _endingGesture = false; }
        if (changed) LayoutEdited?.Invoke(cancel ? _gestureOriginalLayout : _entryLayout, true);
        SearchEntry.Cursor = IsEditingLayout ? Cursors.SizeAll : Cursors.Arrow;
    }
}
