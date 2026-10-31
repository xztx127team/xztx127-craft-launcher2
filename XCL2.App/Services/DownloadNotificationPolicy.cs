using System.Globalization;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>仅控制下载完成提醒的时段；不更改下载行为或模组更新偏好。</summary>
public static class DownloadNotificationPolicy
{
    public static bool IsValidTime(string? value) => TimeOnly.TryParseExact(value,
        new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    public static bool IsQuiet(AppConfig config, DateTime localNow)
    {
        if (!config.Ux50DownloadQuietHours ||
            !TimeOnly.TryParseExact(config.Ux50DownloadQuietStart, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(config.Ux50DownloadQuietEnd, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) return false;
        var now = TimeOnly.FromDateTime(localNow);
        return start == end || (start < end ? now >= start && now < end : now >= start || now < end);
    }
}
