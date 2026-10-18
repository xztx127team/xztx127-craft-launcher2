namespace XCL2.App.Services;

/// <summary>只有空闲时连续、明确的样本才允许改变控制区；触点和未知样本都会中断确认。</summary>
internal sealed class TouchModeStability
{
    private bool? _candidate;
    private long _since;

    public void Reset() => _candidate = null;

    public bool? Observe(bool currentMenu, bool? observedMenu, bool hasActiveContact,
        long now, int gameStabilityMilliseconds, int menuStabilityMilliseconds)
    {
        if (hasActiveContact || !observedMenu.HasValue || observedMenu == currentMenu)
        {
            Reset();
            return null;
        }
        if (_candidate != observedMenu || now < _since)
        {
            _candidate = observedMenu;
            _since = now;
            return null;
        }
        var required = observedMenu.Value ? menuStabilityMilliseconds : gameStabilityMilliseconds;
        if (now - _since < required) return null;
        Reset();
        return observedMenu;
    }
}
