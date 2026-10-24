using XCL2.App.Models;

namespace XCL2.App.Services;

public enum StartupDownloadNoticeChoice
{
    ThisStartup,
    Suppress,
}

/// <summary>One startup evaluation, with an explicit persistent opt-out for this fixed announcement.</summary>
public sealed class StartupDownloadNoticePolicy
{
    public const string AnnouncementId = "2026-10-pcl-download-notice";
    public const string Message = "因为某“中式家长”认为pcl让孩子充值服务器，pcl2下载游戏功能已经关闭，下载功能大家切用且珍惜吧";
    public const string ThisStartupLabel = "我知道了（下回启动还弹出这个）";
    public const string SuppressLabel = "以后不再提示（不再提示此公告）";

    private bool _evaluated;

    /// <summary>Call after startup language selection. Claim the startup before presenting UI, so nested dispatch cannot repeat it.</summary>
    public bool TryBegin(string? currentLanguageCode, AppConfig config)
    {
        if (_evaluated) return false;
        _evaluated = true;
        return string.Equals(currentLanguageCode, "zh-Hans", StringComparison.Ordinal)
            && config.SuppressedStartupAnnouncementIds?.Contains(AnnouncementId, StringComparer.Ordinal) != true;
    }

    /// <summary>A temporary dismissal never writes configuration. Failed persistence restores the unsuppressed in-memory state.</summary>
    public static void ApplyChoice(AppConfig config, StartupDownloadNoticeChoice choice, Action persist)
    {
        if (choice == StartupDownloadNoticeChoice.ThisStartup) return;
        if (choice != StartupDownloadNoticeChoice.Suppress) throw new ArgumentOutOfRangeException(nameof(choice));

        var previous = config.SuppressedStartupAnnouncementIds;
        var suppressed = previous == null ? new List<string>() : new List<string>(previous);
        if (!suppressed.Contains(AnnouncementId, StringComparer.Ordinal)) suppressed.Add(AnnouncementId);
        config.SuppressedStartupAnnouncementIds = suppressed;
        try { persist(); }
        catch
        {
            config.SuppressedStartupAnnouncementIds = previous!;
            throw;
        }
    }
}
