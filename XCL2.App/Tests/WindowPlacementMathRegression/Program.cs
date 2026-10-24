using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
var defaults = new LauncherWindowPlacement();
Check(defaults.Width == 500 && defaults.Height == 320, "First-launch/reset defaults must be 500x320 DIP.");
Check(new AppConfig().LauncherWindowPlacement == null && new AppConfig().RememberWindowPlacement,
    "Missing placement must remain distinguishable from a saved rectangle; remembering defaults on.");
var omitted = JsonSerializer.Deserialize<LauncherWindowPlacement>("{}")!;
Check(omitted.Width == 500 && omitted.Height == 320, "Older partial settings inherit compact defaults.");
const string savedJson = """
    {"Monitor":"DISPLAY2","Width":700,"Height":450,"HorizontalPosition":0.25,"VerticalPosition":0.75,"Maximized":true}
    """;
var saved = JsonSerializer.Deserialize<LauncherWindowPlacement>(savedJson)!;
var roundTrip = JsonSerializer.Deserialize<LauncherWindowPlacement>(JsonSerializer.Serialize(saved))!;
Check(roundTrip.Width == 700 && roundTrip.Height == 450 && roundTrip.Maximized
    && roundTrip.Monitor == "DISPLAY2" && roundTrip.HorizontalPosition == .25 && roundTrip.VerticalPosition == .75,
    "Saved size, monitor, position and maximized state must survive configuration round trips.");

var screen = new WindowPlacementBounds(0, 0, 1920, 1040);
var resolved = WindowPlacementGeometry.Resolve(saved, screen, 1, 420, 280);
Check(resolved == new WindowPlacementBounds(305, 442, 700, 450),
    "Valid saved 700x450 must not be replaced by the new default.");
Check(saved.Width == 700 && saved.Height == 450 && saved.Maximized, "Geometry resolution must not mutate saved preferences.");
var legacy = WindowPlacementGeometry.Resolve(new() { Width = 1000, Height = 640 }, screen, 1, 420, 280);
Check(legacy.Width == 1000 && legacy.Height == 640, "Existing larger windows retain their saved dimensions.");
var highDpi = WindowPlacementGeometry.Resolve(saved, new(0, 0, 2560, 1400), 1.5, 420, 280);
Check(highDpi.Width == 1050 && highDpi.Height == 675, "Saved dimensions are DIP, scaled once for the destination monitor.");
var leftMonitor = WindowPlacementGeometry.Resolve(saved, new(-1920, -200, 1920, 1040), 1, 420, 280);
Check(leftMonitor.Left == -1615 && leftMonitor.Top == 242,
    "Secondary monitors with negative origins retain proportional positions.");
var tiny = WindowPlacementGeometry.Resolve(new() { Width = 50, Height = 60 }, screen, 1, 420, 280);
Check(tiny.Width == 420 && tiny.Height == 280, "Invalidly small saved windows must respect usable minimums.");
var smallScreen = WindowPlacementGeometry.Resolve(saved, new(10, 20, 360, 240), 2, 420, 280);
Check(smallScreen == new WindowPlacementBounds(10, 20, 360, 240),
    "Work area takes priority over minimums on an unusually small display.");
var oversize = WindowPlacementGeometry.Resolve(new() { Width = 9000, Height = 9000 }, screen, 1, 420, 280);
Check(oversize == screen, "Oversized saved rectangles must fit the available work area.");
var invalid = WindowPlacementGeometry.Resolve(new()
    { Width = double.NaN, Height = double.PositiveInfinity, HorizontalPosition = double.NaN, VerticalPosition = double.NaN },
    screen, double.NaN, 420, 280);
Check(invalid == new WindowPlacementBounds(710, 360, 500, 320), "Non-finite saved dimensions recover to centered compact defaults.");
var zero = WindowPlacementGeometry.Resolve(new() { Width = 0, Height = -1 }, screen, 1, 420, 280);
Check(zero.Width == 500 && zero.Height == 320, "Zero/negative dimensions use defaults rather than producing unusable windows.");
var clampedPosition = WindowPlacementGeometry.Resolve(new() { HorizontalPosition = -1, VerticalPosition = 2 }, screen, 1, 420, 280);
Check(clampedPosition.Left == 0 && clampedPosition.Top == 720, "Corrupt relative positions are clamped inside the work area.");
Check(WindowPlacementGeometry.RelativePosition(305, 1220) == .25,
    "Capturing a valid position reverses placement resolution.");
Check(WindowPlacementGeometry.RelativePosition(30, 0) == 0,
    "A window filling its work area has a stable saved position.");
Console.WriteLine($"PASS {checks} window-placement geometry/serialization checks.");
