namespace XCL2.App.Services;

public sealed class SettingsScrollHintPolicy
{
    private long? _start;
    private long _last;
    private bool _shown;
    public bool Observe(long now, bool userMoved)
    {
        if (_shown || !userMoved) return false;
        if (!_start.HasValue || now < _last || now - _last > 1500) _start = now;
        _last = now;
        if (now - _start.Value <= 5000) return false;
        _shown = true;
        return true;
    }
    public void Reset() { _start = null; _last = 0; _shown = false; }
}
