using System.Text.Json;
using XCL2.App;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException(message);
    checks++;
}
var config = ConfigService.Active!.Config;
foreach (var kind in new[] { AnimationKind.Popup, AnimationKind.Toast })
{
    Check(AnimationSettingsService.GetOption(config, kind).IntensityPercent == 30, $"{kind} defaults to intensity 30");
    Check(AnimationSettingsService.Intensity(kind) == 0.3, $"{kind} effective default intensity is 30%");
    Check(AnimationSettingsService.Duration(180, kind) == TimeSpan.FromSeconds(.2), $"{kind} defaults to exactly .2s");
}
Check(AnimationSettingsService.GetOption(config, AnimationKind.Page).IntensityPercent == 100,
    "existing page animation defaults remain unchanged");
config.AnimationSettings["Popup"] = new AnimationOption { IntensityPercent = 100, DurationSeconds = .4 };
Check(AnimationSettingsService.Intensity(AnimationKind.Popup) == 1, "existing saved popup intensity is retained");
Check(AnimationSettingsService.Duration(160, AnimationKind.Popup) == TimeSpan.FromSeconds(.4), "popup exit uses explicit duration");
config.AnimationSpeedPercent = 25;
Check(AnimationSettingsService.Duration(180, AnimationKind.Popup) == TimeSpan.FromSeconds(.4), "global speed does not rescale popup seconds");
Check(AnimationSettingsService.Duration(240, AnimationKind.Page) == TimeSpan.FromMilliseconds(720), "page speed retains prior semantics");
Check(AnimationSettingsService.Duration(80, AnimationKind.SkinHurt) == TimeSpan.FromMilliseconds(80), "skin damage flash is not lengthened");
var option = new AnimationOption { DurationSeconds = double.NaN };
Check(option.DurationSeconds == .2, "non-finite timing restores .2s");
option.DurationSeconds = -5;
Check(option.DurationSeconds == .05, "negative timing clamps safely");
option.DurationSeconds = 10;
Check(option.DurationSeconds == 2, "long timing clamps to supported maximum");
var oldOption = JsonSerializer.Deserialize<AnimationOption>("{\"Enabled\":true,\"IntensityPercent\":63}")!;
Check(oldOption.DurationSeconds == .2 && oldOption.IntensityPercent == 63, "old JSON loads with timing default and original intensity");
var reloaded = JsonSerializer.Deserialize<AnimationOption>(JsonSerializer.Serialize(new AnimationOption { DurationSeconds = .35, IntensityPercent = 43 }))!;
Check(reloaded.DurationSeconds == .35 && reloaded.IntensityPercent == 43, "duration and intensity round-trip together");

var preview = new AppConfig();
preview.AnimationSettings["Popup"] = new AnimationOption { IntensityPercent = 45, DurationSeconds = .6 };
var notifications = 0;
AnimationSettingsService.Changed += () => notifications++;
AnimationSettingsService.SetPreview(preview);
Check(AnimationSettingsService.Intensity(AnimationKind.Popup) == .45 && AnimationSettingsService.Duration(180, AnimationKind.Popup).TotalSeconds == .6,
    "preview changes effective timing and intensity");
Check(config.AnimationSettings["Popup"].DurationSeconds == .4, "preview leaves saved settings untouched");
preview.AnimationSettings["Popup"].DurationSeconds = .7;
Check(AnimationSettingsService.Duration(180, AnimationKind.Popup).TotalSeconds == .6, "preview snapshot is independent of mutable editor state");
var before = notifications;
AnimationSettingsService.SetPreview(preview);
Check(notifications == before + 1, "changing only duration notifies running transitions");
AnimationSettingsService.SetPreview(preview);
Check(notifications == before + 1, "saving identical timing does not interrupt current transitions");
AnimationSettingsService.ClearPreview();
Check(AnimationSettingsService.Duration(180, AnimationKind.Popup).TotalSeconds == .4, "cancel preview restores saved timing");

config.CompletelyDisableAnimations = true;
Check(AnimationSettingsService.Duration(180, AnimationKind.Popup) == TimeSpan.Zero, "explicit no-animation mode overrides popup timing");
config.CompletelyDisableAnimations = false;
System.Windows.SystemParameters.ClientAreaAnimation = false;
Check(AnimationSettingsService.Duration(180, AnimationKind.Toast) == TimeSpan.Zero, "OS reduced motion is respected");
config.FollowSystemReducedMotion = false;
Check(AnimationSettingsService.Duration(180, AnimationKind.Toast).TotalSeconds == .2, "user can choose independent motion preferences");
App.StartupArgs.DisableAnimations = true;
Check(AnimationSettingsService.Duration(180, AnimationKind.Toast) == TimeSpan.Zero, "command-line disable overrides local preference");
App.StartupArgs.DisableAnimations = false;
config.AnimationSettings["Toast"] = new AnimationOption { Enabled = false };
Check(AnimationSettingsService.Intensity(AnimationKind.Toast) == 0 && AnimationSettingsService.Duration(180, AnimationKind.Popup).TotalSeconds == .4,
    "disabling toast does not disable dialogs");
var startupDirectory = Path.Combine(Path.GetTempPath(), "xcl-startup-motion-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(startupDirectory);
try
{
    var primary = Path.Combine(startupDirectory, "primary.json");
    var mirror = Path.Combine(startupDirectory, "mirror.json");
    File.WriteAllText(primary, "{invalid");
    var validMirror = "{\"AnimationSettings\":{\"popup\":{\"IntensityPercent\":42,\"DurationSeconds\":0.35}},\"CompletelyDisableAnimations\":false}";
    File.WriteAllText(mirror, validMirror);
    var startup = StartupAnimationPreferences.Read(new[] { primary, mirror });
    Check(startup.AnimationSettings!["Popup"].IntensityPercent == 42 && startup.AnimationSettings["Popup"].DurationSeconds == .35,
        "pre-main-window dialogs read saved case-insensitive timing after corrupt-primary fallback");
    Check(File.ReadAllText(primary) == "{invalid" && File.ReadAllText(mirror) == validMirror,
        "early preference lookup never modifies or migrates config files");
    File.WriteAllText(primary, "{\"AnimationSettings\":{\"Popup\":{\"IntensityPercent\":90},\"popup\":{\"IntensityPercent\":35,\"DurationSeconds\":0.25}}}");
    var duplicated = StartupAnimationPreferences.Read(new[] { primary, mirror });
    Check(duplicated.AnimationSettings!.Count == 1 && duplicated.AnimationSettings["Popup"].IntensityPercent == 35
        && duplicated.AnimationSettings["Popup"].DurationSeconds == .25,
        "duplicate case variants normalize deterministically without breaking early startup");
    File.WriteAllText(primary, "{\"CompletelyDisableAnimations\":true}");
    Check(StartupAnimationPreferences.Read(new[] { primary, mirror }).CompletelyDisableAnimations,
        "a valid primary file takes precedence over a mirror");
}
finally { Directory.Delete(startupDirectory, recursive: true); }
Console.WriteLine($"PASS: {checks} animation preference checks using production timing code.");
