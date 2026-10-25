using System.Text.Json.Serialization;

namespace XCL2.App.Models;

/// <summary>单类动画的用户偏好；强度 0 等同于关闭；弹窗和通知使用独立秒数，其它分类沿用全局播放速度。</summary>
public sealed class AnimationOption
{
    private double _intensityPercent = 100;
    private double _durationSeconds = 0.2;

    public bool Enabled { get; set; } = true;

    // 人工编辑过的配置可能含非有限值，恢复默认值以免把 NaN 传给动画或滑块。
    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double IntensityPercent
    {
        get => _intensityPercent;
        set => _intensityPercent = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 100;
    }
    /// <summary>弹窗 / 通知的完整单次过渡时长，独立于通知停留时间与全局播放速度。</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double DurationSeconds
    {
        get => _durationSeconds;
        set => _durationSeconds = double.IsFinite(value) ? Math.Clamp(value, 0.05, 2) : 0.2;
    }
}
