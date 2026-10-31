using XCL2.App.Services;

var assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
var normal = AiSideDrawerLayout.Place(1200, 850, true);
Check(normal.Width is >= 360 and <= 420, "Normal drawer must remain narrow");
Check(normal.X >= 700, "Drawer must dock on the right and leave the page available");
Check(normal.Height <= 760 && normal.Y >= 0, "Default panel should fit vertically");
var dragged = AiSideDrawerLayout.Place(1200, 850, false, 140, 80);
Check(dragged.X == 140 && dragged.Y == 80, "Dragging should preserve a valid requested position");
var reset = AiSideDrawerLayout.Place(1200, 850, true, 140, 80);
Check(reset == normal, "Reset must restore right docking");
foreach (var (width, height) in new[] { (700d, 520d), (380d, 400d), (200d, 160d), (1d, 1d), (0d, 0d) })
{
    var bounds = AiSideDrawerLayout.Place(width, height, false, 99999, -99999);
    Check(bounds.Width >= 0 && bounds.Width <= width && bounds.Height >= 0 && bounds.Height <= height, "Panel overflowed a reduced viewport");
    Check(bounds.X >= 0 && bounds.Y >= 0 && bounds.X + bounds.Width <= width && bounds.Y + bounds.Height <= height, "Dragged panel escaped its bounds");
}
var invalid = AiSideDrawerLayout.Place(double.NaN, double.PositiveInfinity, false, double.NaN, double.NegativeInfinity);
Check(double.IsFinite(invalid.X) && double.IsFinite(invalid.Y) && double.IsFinite(invalid.Width) && double.IsFinite(invalid.Height), "Invalid geometry must not reach WPF");
var clamped = AiSideDrawerLayout.Place(500, 350, false, 800, 900);
Check(clamped.X + clamped.Width <= 500 && clamped.Y + clamped.Height <= 350, "Resize must keep the title and footer reachable");
Console.WriteLine($"AI side drawer layout: {assertions} assertions passed.");
