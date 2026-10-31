using System.Runtime.InteropServices;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

Check(!LegacyTouchCursorVisibility.IsVisible(IntPtr.Zero, true), "SHOWING without a cursor is not visible");
Check(LegacyTouchCursorVisibility.HasVisiblePixels(IntPtr.Zero) == null, "zero handle is not proof of a blank shape");
Check(!LegacyTouchCursorVisibility.IsVisible(new IntPtr(123), false), "hidden cursor never requires GDI inspection");
Check(!LegacyTouchCursorVisibilityPolicy.IsVisible(true, true, false), "transparent LWJGL cursor is hidden despite SHOWING");
Check(LegacyTouchCursorVisibilityPolicy.IsVisible(true, true, true), "visible menu cursor stays visible");
Check(LegacyTouchCursorVisibilityPolicy.IsVisible(true, true, null), "failed shape inspection cannot manufacture capture evidence");
Check(!LegacyTouchCursorVisibilityPolicy.IsVisible(true, false, null), "stale SHOWING flag with zero handle stays hidden");

const int first = 0x123456, second = 0xedcba9;
Check(!LegacyTouchCursorVisibilityPolicy.ChangesRgb([first, unchecked((int)0xff123456)], first),
    "reserved alpha-byte changes do not make a blank cursor visible");
Check(LegacyTouchCursorVisibilityPolicy.ChangesRgb([first, first ^ 0x000100], first),
    "one altered visible pixel is enough");
Check(LegacyTouchCursorVisibilityPolicy.ChangesRgb([first ^ 0x00ffffff], first),
    "monochrome XOR cursor is visible");
Check(!LegacyTouchCursorVisibilityPolicy.ChangesRgb([first], first) &&
    LegacyTouchCursorVisibilityPolicy.ChangesRgb([first], second),
    "opaque cursor matching one background is detected by the complementary background");
Check(!LegacyTouchCursorVisibilityPolicy.ChangesRgb([second, second], second),
    "transparent output preserves both backgrounds");

var blank = LegacyTouchCursorVisibilityPolicy.Resolve(true, true, true, false);
Check(!blank.Showing && !blank.Suppressed, "confirmed blank shape remains hidden independently of touch suppression");
var arrow = LegacyTouchCursorVisibilityPolicy.Resolve(true, true, true, true);
Check(arrow.Showing && arrow.Suppressed, "ordinary menu arrow keeps its touch-suppressed state");
var failedShape = LegacyTouchCursorVisibilityPolicy.Resolve(true, true, true, null);
Check(failedShape.Showing && failedShape.Suppressed, "GDI failure preserves touch-suppressed uncertainty");
var missingCursor = LegacyTouchCursorVisibilityPolicy.Resolve(true, true, false, null);
Check(!missingCursor.Showing && missingCursor.Suppressed, "zero cursor clears showing but cannot override suppression");

bool? Classify((bool Showing, bool Suppressed) state, bool joined, bool centered)
    => TouchInputModePolicy.Classify(false, true, true, state.Showing, state.Suppressed,
        false, joined, centered);
Check(Classify(blank, true, true) == false, "joined centered fullscreen world recognizes LWJGL blank cursor");
Check(Classify(blank, false, true) == null, "blank cursor alone does not establish a joined world");
Check(Classify(blank, true, false) == null, "blank off-center cursor alone does not establish capture");
Check(Classify(arrow, true, true) == null, "touching an ordinary menu arrow never authorizes camera input");
Check(Classify(failedShape, true, true) == null, "unavailable cursor shape cannot bypass suppression");
var menuArrow = LegacyTouchCursorVisibilityPolicy.Resolve(true, false, true, true);
Check(Classify(menuArrow, true, true) == true, "actual visible menu arrow still selects the menu after joining");

var recovery = new TouchCaptureRecovery();
Check(!recovery.Observe(blank.Showing, blank.Suppressed, true, true, false, 0) &&
    !recovery.Observe(blank.Showing, blank.Suppressed, true, true, false, 1000),
    "loading blank cursor cannot manufacture a capture transition");
recovery.Reset();
Check(!recovery.Observe(blank.Showing, blank.Suppressed, true, false, false, 2000) &&
    !recovery.Observe(blank.Showing, blank.Suppressed, true, false, false, 2699) &&
    recovery.Observe(blank.Showing, blank.Suppressed, true, false, false, 2700),
    "blank cursor recovery still requires the full native centered stability period");
Check(!recovery.Observe(menuArrow.Showing, menuArrow.Suppressed, true, false, false, 2800) && !recovery.Captured,
    "opening a visible menu clears recovered capture");
Check(!recovery.Observe(arrow.Showing, arrow.Suppressed, true, false, false, 3000) &&
    !recovery.Observe(arrow.Showing, arrow.Suppressed, true, false, false, 5000),
    "touch suppression of a menu arrow does not become a hidden transition over time");
Check(!recovery.Observe(blank.Showing, blank.Suppressed, true, false, true, 6000),
    "outside-world evidence resets even a blank cursor");

if (OperatingSystem.IsWindows()) NativeCursorCases.Run(Check);
else Console.WriteLine("SKIP: native CreateCursor / DrawIconEx checks require Windows");
Console.WriteLine($"PASS: {checks} legacy cursor visibility checks");

internal static class NativeCursorCases
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")]
    private static extern IntPtr CreateCursor(IntPtr instance, int hotspotX, int hotspotY,
        int width, int height, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);
    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr cursor);

    public static void Run(Action<bool, string> check)
    {
        var andMask = Enumerable.Repeat((byte)0xff, 128).ToArray();
        var xorMask = new byte[128];
        var blank = CreateCursor(GetModuleHandle(null), 0, 0, 32, 32, andMask, xorMask);
        check(blank != IntPtr.Zero, "native transparent cursor is created");
        IntPtr inverted = IntPtr.Zero;
        try
        {
            check(LegacyTouchCursorVisibility.HasVisiblePixels(blank) == false,
                "actual native AND-FF/XOR-00 cursor draws no pixels on either background");
            check(!LegacyTouchCursorVisibility.IsVisible(blank, true),
                "native transparent cursor overrides a stale SHOWING flag");
            var arrow = LoadCursor(IntPtr.Zero, new IntPtr(32512)); // IDC_ARROW; shared, never destroyed here.
            check(arrow != IntPtr.Zero, "system arrow cursor loads");
            check(LegacyTouchCursorVisibility.HasVisiblePixels(arrow) == true,
                "actual system arrow draws visible pixels");
            check(LegacyTouchCursorVisibility.IsVisible(arrow, true), "native arrow remains a menu cursor");
            inverted = CreateCursor(GetModuleHandle(null), 0, 0, 32, 32, andMask, andMask);
            check(inverted != IntPtr.Zero, "native XOR cursor is created");
            check(LegacyTouchCursorVisibility.HasVisiblePixels(inverted) == true,
                "actual native XOR-inverting cursor is not mistaken for transparent");
        }
        finally
        {
            if (inverted != IntPtr.Zero) DestroyCursor(inverted);
            DestroyCursor(blank);
        }
    }
}
