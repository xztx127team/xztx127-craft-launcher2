using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Agent 动画预设写回真实强度，设置页滑杆与实际动效保持一致。</summary>
public static class AiAgentAnimationService
{
    public static event Action<AppConfig>? ProfileApplied;

    public static bool ApplyProfile(AppConfig config, AiAgentAnimationProfile profile)
    {
        if (config.AiAssistant.WorkMode != AiWorkMode.Agent
            || profile is not (AiAgentAnimationProfile.LowPower or AiAgentAnimationProfile.HighPerformance))
            return false;

        var intensity = profile == AiAgentAnimationProfile.LowPower ? 20d : 100d;
        var changed = false;
        foreach (var (kind, _) in AnimationSettingsService.Options)
        {
            // 起始不透明度不是性能强度，保留用户的透明效果偏好。
            if (kind == AnimationKind.Transparency) continue;
            var option = AnimationSettingsService.GetOption(config, kind);
            changed |= option.IntensityPercent != intensity;
            option.IntensityPercent = intensity;
            config.AnimationSettings[kind.ToString()] = option;
        }
        // 用户手动关闭的分类与总开关仍有效；预设只联动强度。
        AnimationSettingsService.ClearPreview();
        AnimationSettingsService.NotifyChanged();
        ProfileApplied?.Invoke(config);
        return changed;
    }
}
