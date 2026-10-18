namespace XCL2.App.Services;

/// <summary>输入时段内合并位移；保留小数，不为每个触摸采样排入一次 SendInput。</summary>
internal sealed class LegacyTouchMotion
{
    private double _x, _y;
    public bool Pending => Math.Abs(_x) >= 1 || Math.Abs(_y) >= 1;
    public void Add(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        _x = Math.Clamp(_x + x, -32767, 32767);
        _y = Math.Clamp(_y + y, -32767, 32767);
    }
    public bool Take(out int x, out int y)
    {
        x = (int)Math.Truncate(_x); y = (int)Math.Truncate(_y);
        _x -= x; _y -= y;
        return x != 0 || y != 0;
    }
    public void Reset() => _x = _y = 0;
}
