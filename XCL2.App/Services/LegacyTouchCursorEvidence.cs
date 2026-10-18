namespace XCL2.App.Services;

/// <summary>
/// 无客户端日志的旧后端：仅观察真实菜单定位之后游戏自己的回中，不发送探测输入。
/// 启动时静止在中心、触摸抑制和单个中心样本都不是抓取证据。由旧悬浮层 UI 线程使用。
/// </summary>
internal sealed class LegacyTouchCursorEvidence
{
    private (int X, int Y, long At)? _menuAnchor;
    private long? _centerSince;
    public bool Captured { get; private set; }

    public void MenuMoved(int x, int y, long now)
    {
        _menuAnchor = (x, y, now);
        _centerSince = null;
        Captured = false;
    }

    public bool Observe(int x, int y, int centerX, int centerY, bool showing, bool suppressed, long now)
    {
        // 手指隐藏箭头不能建立新证据；已经确认的抓取仍须由可见菜单/失焦撤销。
        if (suppressed) { _centerSince = null; return Captured; }
        var centered = Math.Abs((long)x - centerX) <= 3 && Math.Abs((long)y - centerY) <= 3;
        if (showing)
        {
            Captured = false;
            _centerSince = null;
            // 可见菜单已经静止在中心时，不能随后拿更早的离中心坐标伪造回中。
            if (centered) _menuAnchor = null;
            return false;
        }
        if (Captured) return true;
        if (_menuAnchor is not { } anchor || now < anchor.At || now - anchor.At > 180_000 ||
            (Math.Abs((long)anchor.X - centerX) < 16 && Math.Abs((long)anchor.Y - centerY) < 16) || !centered)
        {
            _centerSince = null;
            return false;
        }
        if (!_centerSince.HasValue || now < _centerSince.Value) { _centerSince = now; return false; }
        if (now - _centerSince.Value >= 100) Captured = true;
        return Captured;
    }

    public void ClearCapture() { Captured = false; _centerSince = null; }
    public void Reset() { ClearCapture(); _menuAnchor = null; }
}
