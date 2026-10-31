namespace XCL2.App.Models;

public enum AiActivityKind { SubAgent, CodeChange, FileEdit, Command, ReasoningSummary, Reasoning, RequestParameters }
public enum AiActivityStatus { Pending, Running, Completed, Failed, Denied, Cancelled }

/// <summary>一次真实操作或供应商公开返回内容的快照；不将普通回答推测为内部执行记录。</summary>
public sealed class AiAssistantActivity
{
    public string? ChangeId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AiActivityKind Kind { get; set; }
    public AiActivityStatus Status { get; set; } = AiActivityStatus.Running;
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public string? FilePath { get; set; }
    public string? BeforeCode { get; set; }
    public string? AfterCode { get; set; }
    public AiFileDiff? FileDiff { get; set; }
    public string? Command { get; set; }
    public string? OriginalCommand { get; set; }
    public string? CommandConversion { get; set; }
    public string? Model { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }

    public AiAssistantActivity Clone()
    {
        var clone = (AiAssistantActivity)MemberwiseClone();
        clone.FileDiff = FileDiff?.Clone();
        return clone;
    }

    /// <summary>进行中 / 等待中的记录永不过期；已结束的记录按完成时间（无则创建时间）计算。</summary>
    public bool IsExpired(long retentionSeconds, DateTime? nowUtc = null)
    {
        if (retentionSeconds <= 0 || Status is AiActivityStatus.Pending or AiActivityStatus.Running) return false;
        var finished = CompletedUtc ?? CreatedUtc;
        return ((nowUtc ?? DateTime.UtcNow) - finished).TotalSeconds > retentionSeconds;
    }

    public bool IsVisible(AiAssistantConfig config) => !IsExpired(config.ActivityRetentionSeconds) && Kind switch
    {
        AiActivityKind.SubAgent => config.ShowSubAgents,
        AiActivityKind.CodeChange => config.ShowCodeChanges,
        AiActivityKind.FileEdit => config.ShowEditingFiles,
        AiActivityKind.Command => config.ShowCommands,
        AiActivityKind.ReasoningSummary => config.ShowReasoningSummary,
        AiActivityKind.Reasoning => config.ShowReasoning,
        AiActivityKind.RequestParameters => true,
        _ => false
    };
}

/// <summary>执行记录保留时长的输入解析与显示：空、0、∞、A、不限制都表示不限制。</summary>
public static class AiActivityRetention
{
    public static readonly string[] UnitNames = { "秒", "分钟", "小时", "天" };
    private static readonly long[] UnitSeconds = { 1, 60, 3600, 86400 };
    public const long MaxSeconds = 3650L * 86400;

    /// <returns>false 表示输入不是有效数字；seconds 为 0 表示不限制。</returns>
    public static bool TryParse(string? text, int unitIndex, out long seconds)
    {
        seconds = 0;
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value is "0" or "∞" or "不限制" or "不限" or "永久"
            || value.Equals("A", StringComparison.OrdinalIgnoreCase)) return true;
        if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number < 0) return false;
        var unit = UnitSeconds[Math.Clamp(unitIndex, 0, UnitSeconds.Length - 1)];
        seconds = number > MaxSeconds / unit ? MaxSeconds : number * unit;
        return true;
    }

    /// <summary>选择能整除的最大单位显示，例如 172800 秒显示为 2 天。</summary>
    public static (string Text, int UnitIndex) Format(long seconds)
    {
        if (seconds <= 0) return ("∞", 3);
        for (int i = UnitSeconds.Length - 1; i >= 0; i--)
            if (seconds % UnitSeconds[i] == 0) return ((seconds / UnitSeconds[i]).ToString(System.Globalization.CultureInfo.InvariantCulture), i);
        return (seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
    }

    public static bool Prune(List<AiAssistantActivity>? activities, long retentionSeconds)
        => activities != null && retentionSeconds > 0 && activities.RemoveAll(a => a.IsExpired(retentionSeconds)) > 0;
}
