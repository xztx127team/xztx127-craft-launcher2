using XCL2.App.Models;
using XCL2.App.Services;
using Box = XCL2.App.Services.AprilFoolsMotionPolicy.Box;
using Point = XCL2.App.Services.AprilFoolsMotionPolicy.Point;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var area = new Box(-1400, -200, 1200, 800);
var random = new Random(41);
for (var i = 0; i < 2000; i++)
{
    var button = new Box(area.X + random.NextDouble() * 800, area.Y + random.NextDouble() * 500, 240, 160);
    var mouse = new Point(button.X + 5, button.Y + 20);
    var result = AprilFoolsMotionPolicy.Evade(button, area, mouse, 100, random);
    Check(result.X >= area.X && result.Y >= area.Y && result.Right <= area.Right + .000001 && result.Bottom <= area.Bottom + .000001,
        "All candidates stay within negative-coordinate monitor bounds");
    Check(AprilFoolsMotionPolicy.Distance(mouse, result) >= 100, "Rectangle edge, not center, respects gap when feasible");
}
var crowded = new Box(0, 0, 210, 100);
var current = new Box(0, 0, 200, 100);
var farthest = AprilFoolsMotionPolicy.Evade(current, crowded, new Point(0, 50), 100, random);
Check(farthest.X == 10, "Insufficient space chooses furthest visible candidate");
Check(AprilFoolsMotionPolicy.Distance(new Point(200, 70), new Box(0, 0, 250, 140)) == 0, "Pointer within large button is dangerous despite distant center");
var fitted = AprilFoolsMotionPolicy.Fit(new Box(9000, -9000, 1800, 1200), area);
Check(fitted == area, "Oversized window fits exact current working area");
var config = ConfigService.Active!.Config;
Check(config.EnableAprilFoolsEasterEggs && config.AprilFoolsExitChallengeEnabled, "Approved master and whiteflag challenge defaults");
Check(!config.AprilFoolsExternalVideos && !config.AprilFoolsFakeErrors && !config.AprilFoolsLaunchRefusal, "Intrusive effects opt in");
AprilFoolsService.EnableDebugAprilFools();
AprilFoolsService.EnsureTodaysStateLoaded();
Check(AprilFoolsService.HasMotionEffects, "Default random selection contains visual effects");
Check(!AprilFoolsService.TryGetLaunchOrDownloadInterception(out _), "Visual effects never intercept download hook");
AprilFoolsService.DismissForToday();
AprilFoolsService.EnsureTodaysStateLoaded();
Check(!AprilFoolsService.IsActive, "F1/launch dismissal cannot resurrect through refresh");
AprilFoolsService.ActivateRandomSessionEffect();
config.EnableAprilFoolsEasterEggs = false;
Check(!AprilFoolsService.IsActive, "Master preference stops session as well as date effects");
config.EnableAprilFoolsEasterEggs = true;
RegistryConfigService.Disabled = true;
AprilFoolsService.RefreshPreferences();
Check(!AprilFoolsService.IsActive, "Legacy registry off switch applies to session effects");
var registryReads = RegistryConfigService.Reads;
for (var i = 0; i < 500; i++) _ = AprilFoolsService.ActiveEffects;
Check(RegistryConfigService.Reads == registryReads, "Animation frames use registry cache");
RegistryConfigService.Disabled = false;
AprilFoolsService.RefreshPreferences();
Check(AprilFoolsService.IsActive, "Saved switch changes are immediate");
config.EnableAprilFoolsEasterEggs = false;
Check(!AprilFoolsService.IsActive, "Master switch remains immediate without registry refresh");
Console.WriteLine("AprilFoolsMotionRegression passed.");

namespace XCL2.App
{
    public static class App { public static string DataDir => Path.Combine(Path.GetTempPath(), "XCL2-AprilMotionRegression"); }
}
namespace XCL2.App.Services
{
    public sealed class ConfigService
    {
        public static ConfigService? Active { get; } = new();
        public AppConfig Config { get; } = new();
    }
    public static class RegistryConfigService
    {
        public static bool Disabled;
        public static int Reads;
        public static int GetInt(string name, int fallback) { Reads++; return Disabled ? 1 : fallback; }
    }
}
namespace XCL2.App.Models
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class ExperiencePreferenceAttribute(string group, string label) : Attribute
    {
        public string Description { get; set; } = "";
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public string Unit { get; set; } = "";
    }
}
