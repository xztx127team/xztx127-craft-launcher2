using XCL2.App.Services;

internal static class LegacySuppressedCaptureCases
{
    public static void Run(Action<bool, string> check)
    {
        var recovery = new TouchCaptureRecovery();
        bool Sample(long at, bool backendHidden = true, bool centered = true,
            bool loading = false, bool outside = false, bool menuRequested = false, bool showing = false,
            bool suppressed = true)
            => recovery.ObserveBackendCursor(showing, suppressed, backendHidden, centered,
                loading, outside, menuRequested, at);

        // Fullscreen/whole-desktop clip + CURSOR_SUPPRESSED + null backend cursor.
        // There is no off-center menu anchor and attaching does not create a Joined transition.
        check(TouchInputModePolicy.Classify(true, true, false, false, true, false,
            worldJoined: true, cursorCentered: true) == null,
            "fullscreen suppressed native sample alone remains unknown");
        check(!Sample(1000) && !Sample(1699), "fullscreen attach waits for stable hidden center samples");
        check(Sample(1700), "backend-hidden fullscreen touch recovers without a menu anchor or Joined edge");
        check(Sample(1800, centered: false), "confirmed capture survives genuine look movement away from center");

        recovery.Reset(); // same reset performed when Alt+Tab hides the overlay
        check(!Sample(2000) && !Sample(2699) && Sample(2700),
            "Alt+Tab return builds fresh stable evidence instead of remaining in menu mode");

        recovery.Reset();
        check(!Sample(3000, backendHidden: false) && !Sample(5000, backendHidden: false),
            "touch-suppressed ordinary arrow never establishes capture");
        check(!Sample(6000) && !Sample(6500, centered: false) && !Sample(6700) && !Sample(7399) && Sample(7400),
            "off-center transient restarts the hidden-center stability interval");

        recovery.Reset();
        check(!Sample(8000, showing: true, backendHidden: false, suppressed: false), "visible cursor revokes recovery");
        check(!Sample(8100, loading: true) && !Sample(10000, loading: true),
            "loading cannot override touch suppression even after a visible menu or a long stable center");
        check(!Sample(10100) && !Sample(10299) && Sample(10300),
            "leaving loading requires fresh hidden samples before recovery");

        check(!Sample(11000, menuRequested: true) && !recovery.Captured,
            "explicit menu intent revokes previously recovered capture");
        check(!Sample(13000, menuRequested: true), "menu request cannot accumulate hidden-center evidence");
        check(!Sample(13100) && !Sample(13799) && Sample(13800),
            "after menu intent ends a full fresh stability interval is required");
        check(!Sample(14000, outside: true) && !recovery.Captured && !Sample(16000, outside: true),
            "outside-world phase cannot establish or retain recovered capture");
        check(!Sample(17000) && !Sample(16999) && !Sample(17698) && Sample(17699),
            "clock rollback restarts rather than accelerates the recovery interval");

        // Older clients may never emit a Joined line. Preserve the original unsuppressed
        // visible-to-hidden recovery rather than requiring a log transition that cannot arrive.
        recovery.Reset();
        check(!Sample(20000, loading: true, showing: true, backendHidden: false, suppressed: false),
            "an unsuppressed visible cursor during loading records the existing native transition evidence");
        check(!Sample(20100, loading: true, suppressed: false) &&
            !Sample(20299, loading: true, suppressed: false) && Sample(20300, loading: true, suppressed: false),
            "a stalled Loading log preserves unsuppressed visible-to-hidden stable-center recovery");
        recovery.Reset();
        check(!Sample(21000, loading: true, suppressed: false) &&
            !Sample(23000, loading: true, suppressed: false),
            "hidden loading samples without a prior visible cursor still cannot establish capture");
    }
}
