using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>Recognizes planning promises only to ask for a valid plan; prose never becomes tool authority.</summary>
internal static class AiAgentPlanRecovery
{
    private static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string Bounded(string? text)
    {
        var value = (text ?? "").Trim();
        return value.Length > 12000 ? value[..12000] : value;
    }

    internal static bool IsBlockingReply(string? text)
    {
        var value = Bounded(text);
        // A genuine question, refusal or request for missing input remains a normal answer.
        return Match(value, @"[?？]\s*$")
            || Match(value, @"^(?:抱歉[，,\s]*)?(?:我(?:无法|不能)|无法继续|不能继续|不支持|拒绝|没有权限|无权)")
            || Match(value, @"请(?:你)?(?:先)?(?:提供|确认|选择|补充|告诉|开启|打开)|需要(?:你|用户).{0,20}(?:提供|确认|选择|开启)")
            || Match(value, @"^(?:sorry[,\s]*)?(?:i (?:cannot|can't)|i am unable|unable to|please (?:provide|confirm|choose)|could you|can you)");
    }

    internal static bool HasPlanningPromise(string? text)
    {
        var value = Bounded(text);
        if (value.Length == 0 || IsBlockingReply(value)) return false;
        var plan = Match(value, @"计划|规划|(?:^|[^A-Za-z])plan(?:ning)?(?:[^A-Za-z]|$)");
        if (!plan) return false;
        return (Match(value, @"本轮|这轮|接下来|现在|首先|我(?:会|将|先|准备)|先(?:补|建|立|创建|保存|制定|列|把)|继续")
                && Match(value, @"保存|落盘|写入|提交|创建|立|补|生成|建立|制定|更新|列出"))
            || Match(value, @"(?:计划|规划).{0,20}(?:已保存|已创建|已建立|已提交|已完成)|(?:已保存|已创建|已建立|已提交).{0,20}(?:计划|规划)")
            || Match(value, @"(?:\bi(?:'ll| will)|let me|\bfirst|\bnext|\bnow).{0,100}\b(?:create|save|write|make|prepare|record|update)\b.{0,80}\bplan\b")
            || Match(value, @"\bplan (?:is|has been) (?:saved|created|recorded)\b");
    }

    internal static bool NeedsPlanAfterUnmarkedReply(string? text, bool recoveryPending)
    {
        var value = Bounded(text);
        if (IsBlockingReply(value)) return false;
        if (HasPlanningPromise(value)) return true;
        // An explicit unwrapped plan still needs the documented boundary; it is not silently parsed.
        if (Match(value, @"(?:^|\n)\s*(?:Plan|计划|执行计划|任务计划)\s*[:：]\s*(?:\n|$)")) return true;
        return recoveryPending && (value.Length == 0 || Match(value,
            @"^(?:好(?:的)?|收到|明白|了解|继续|ok|understood|sure)[.。！!，,\s]*$"));
    }

    internal static string BuildFeedback(string error)
        => "系统：上一个请求未执行。当前 Agent 尚未保存本轮计划，先修复计划阶段。原因：" + error
            + "\n下一条回复只提交一个完整的文本 Plan，不要 JSON，不要 HTML，不要文件内容，不要 Shell 命令，"
            + "不要同时输出 XCL2_ACCESS，也不要只说‘先建立计划’。请使用下面的边界，替换为本任务的实际步骤：\n"
            + "<<XCL2_PLAN>>\n○ [step-1] 当前要执行的步骤\n– [step-2] 验证结果\n<</XCL2_PLAN>>\n"
            + "已完成步骤继续标 ✓ 并保留证据和原 ID；有必要的缺失信息可以明确询问，无法执行可以说明限制。"
            + "系统确认计划已保存后，下一轮再按既有权限执行具体工具；不要重发已执行操作，不要声称未执行的操作已完成。";
}
