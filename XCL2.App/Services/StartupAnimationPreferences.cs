using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>多开提示早于配置服务创建；仅读取动画字段，绝不触发配置迁移、账户读取或写盘。</summary>
public sealed class StartupAnimationPreferences
{
    public bool EnableUiAnimations { get; set; } = true;
    public bool EnablePageAnimations { get; set; } = true;
    public bool FollowSystemReducedMotion { get; set; } = true;
    public bool LowPerformanceMode { get; set; }
    public bool CompletelyDisableAnimations { get; set; }
    public double AnimationSpeedPercent { get; set; } = 75;
    public NoAnimationOptimization NoAnimationOptimization { get; set; } = global::XCL2.App.Models.NoAnimationOptimization.Balanced;
    public Dictionary<string, AnimationOption>? AnimationSettings { get; set; }

    public static StartupAnimationPreferences Read(IEnumerable<string> candidatePaths)
    {
        foreach (var path in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                var preferences = JsonSerializer.Deserialize<StartupAnimationPreferences>(stream);
                if (preferences == null) continue;
                if (preferences.AnimationSettings != null)
                {
                    // 手工配置可能同时包含 Popup/popup；与正式配置一样按读取顺序最后一项生效。
                    var normalized = new Dictionary<string, AnimationOption>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in preferences.AnimationSettings) normalized[pair.Key] = pair.Value;
                    preferences.AnimationSettings = normalized;
                }
                return preferences;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // 损坏的主副本尝试下一份；配置问题不能让“多开选择”本身崩溃。
            }
        }
        return new StartupAnimationPreferences();
    }
}
