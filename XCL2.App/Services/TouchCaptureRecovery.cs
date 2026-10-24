namespace XCL2.App.Services;

/// <summary>Observe native hide/recenter transitions even when the overlay was enabled after joining.
/// Never moves the pointer to manufacture capture evidence.</summary>
internal sealed class TouchCaptureRecovery
{
    private bool _sawVisible;
    private long? _hiddenSince;
    public bool Captured { get; private set; }

    /// <summary>
    /// A null GLFW cursor or a verified transparent LWJGL cursor is a backend hide signal,
    /// independent of Windows hiding an ordinary arrow after touch. On fullscreen attach or
    /// focus return there need not be a previous menu anchor or a new Joined log transition.
    /// Require fresh, stable center samples. Loading retains the existing visible-to-hidden
    /// recovery, but cannot use backend hiding to override touch suppression.
    /// </summary>
    public bool ObserveBackendCursor(bool showing, bool suppressed, bool backendHidden,
        bool centered, bool loading, bool outside, bool menuRequested, long now)
    {
        if (outside || menuRequested) { Reset(); return false; }
        if (loading)
            return Observe(showing, suppressed, centered, loading: true, outside: false, now);
        return Observe(showing, suppressed && !backendHidden, centered,
            loading: false, outside: false, now);
    }

    public bool Observe(bool showing, bool suppressed, bool centered, bool loading, bool outside, long now)
    {
        if (outside) { Reset(); return false; }
        if (suppressed) { _hiddenSince = null; return Captured; }
        if (showing) { _sawVisible = true; _hiddenSince = null; Captured = false; return false; }
        if (Captured) return true;
        if (!centered || (loading && !_sawVisible)) { _hiddenSince = null; return false; }
        if (!_hiddenSince.HasValue || now < _hiddenSince.Value) { _hiddenSince = now; return false; }
        // A native visible->hidden transition is stronger than attaching to an already hidden window.
        if (now - _hiddenSince.Value >= (_sawVisible ? 200 : 700)) { Captured = true; _sawVisible = false; }
        return Captured;
    }

    public void BeginLoading() { Captured = false; _hiddenSince = null; }
    public void Reset() { _sawVisible = false; _hiddenSince = null; Captured = false; }
}
