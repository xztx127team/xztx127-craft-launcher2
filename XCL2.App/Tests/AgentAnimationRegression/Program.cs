using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool value, string message)
{
    checks++;
    if (!value) throw new InvalidOperationException(message);
}

var config = new AppConfig { AiAssistant = new() { WorkMode = AiWorkMode.Agent } };
config.AnimationSettings[AnimationKind.Popup.ToString()] =
    new AnimationOption { Enabled = false, IntensityPercent = 63 };
config.AnimationSettings[AnimationKind.Page.ToString()] =
    new AnimationOption { Enabled = true, IntensityPercent = 47 };
config.AnimationSettings[AnimationKind.Transparency.ToString()] = new AnimationOption { Enabled = false, IntensityPercent = 30 };
var notifications = 0;
AppConfig? notifiedConfig = null;
AiAgentAnimationService.ProfileApplied += Capture;
void Capture(AppConfig current) { notifications++; notifiedConfig = current; }
try
{
    Check(AiAgentAnimationService.ApplyProfile(config, AiAgentAnimationProfile.LowPower),
        "First low-power selection changes the persisted values.");
    Check(config.AnimationSettings.Count == AnimationSettingsService.Options.Count,
        "A profile covers every animation category, including missing legacy options.");
    foreach (var (kind, _) in AnimationSettingsService.Options)
        Check(config.AnimationSettings[kind.ToString()].IntensityPercent == (kind == AnimationKind.Transparency ? 30 : 20),
            $"Low-power intensity written for {kind}.");
    Check(!config.AnimationSettings[AnimationKind.Popup.ToString()].Enabled,
        "A manually disabled category remains disabled.");
    Check(config.AnimationSettings[AnimationKind.Page.ToString()].Enabled,
        "An enabled category remains enabled.");
    Check(notifications == 1 && ReferenceEquals(config, notifiedConfig),
        "The settings page receives the same updated configuration.");
    Check(AnimationSettingsService.ClearPreviewCalls == 1 && AnimationSettingsService.NotifyChangedCalls == 1,
        "The old preview is cleared and effective animation values are notified.");

    Check(!AiAgentAnimationService.ApplyProfile(config, AiAgentAnimationProfile.LowPower),
        "Reapplying an identical profile reports no persisted intensity change.");
    Check(AiAgentAnimationService.ApplyProfile(config, AiAgentAnimationProfile.HighPerformance),
        "High-performance changes all persisted intensities.");
    foreach (var (key, option) in config.AnimationSettings)
        Check(option.IntensityPercent == (key == "Transparency" ? 30 : 100), "High-performance intensity is 100 percent.");

    var serialized = JsonSerializer.Serialize(config);
    var reloaded = JsonSerializer.Deserialize<AppConfig>(serialized)!;
    Check(reloaded.AnimationSettings.All(pair => pair.Value.IntensityPercent == (pair.Key == "Transparency" ? 30 : 100)),
        "Intensity values survive configuration serialization.");
    Check(!reloaded.AnimationSettings[AnimationKind.Popup.ToString()].Enabled,
        "Disabled categories survive configuration serialization.");

    var before = JsonSerializer.Serialize(config.AnimationSettings);
    var beforeEvents = notifications;
    foreach (var mode in new[] { AiWorkMode.Build, AiWorkMode.Plan, (AiWorkMode)99 })
    {
        config.AiAssistant.WorkMode = mode;
        Check(!AiAgentAnimationService.ApplyProfile(config, AiAgentAnimationProfile.LowPower),
            $"{mode} does not apply Agent presets.");
    }
    config.AiAssistant.WorkMode = AiWorkMode.Agent;
    Check(!AiAgentAnimationService.ApplyProfile(config, AiAgentAnimationProfile.Balanced),
        "Balanced preserves the user's existing intensity values.");
    Check(!AiAgentAnimationService.ApplyProfile(config, (AiAgentAnimationProfile)99),
        "An unknown imported profile does not overwrite values.");
    Check(before == JsonSerializer.Serialize(config.AnimationSettings) && beforeEvents == notifications,
        "Ignored modes/profiles do not mutate preferences or raise profile events.");
}
finally { AiAgentAnimationService.ProfileApplied -= Capture; }

Console.WriteLine($"Agent animation regression passed: {checks} checks.");
