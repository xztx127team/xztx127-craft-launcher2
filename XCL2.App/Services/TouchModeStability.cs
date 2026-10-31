namespace XCL2.App.Services;

/// <summary>连续明确样本才能切换。调用方可允许已确认的原生证据终止活动手势；未知样本始终中断确认。</summary>
internal sealed class TouchModeStability
{
    private bool? _candidate;
    private long _since;

    public void Reset() => _candidate = null;

    public bool? Observe(bool currentMenu, bool? observedMenu, bool hasActiveContact,
        long now, int gameStabilityMilliseconds, int menuStabilityMilliseconds, bool allowDuringContact = false)
    {
        if ((hasActiveContact && !allowDuringContact) || !observedMenu.HasValue || observedMenu == currentMenu)
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
