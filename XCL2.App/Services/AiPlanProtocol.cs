using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>One terminal text plan becomes update_task data, never an executable command or a permission change.</summary>
internal static class AiPlanProtocol
{
    internal const string OpenMarker = "<<XCL2_PLAN>>";
    internal const string CloseMarker = "<</XCL2_PLAN>>";
    internal const int MaxBlockChars = 64_000;
    internal const int MaxSteps = 100;
    internal sealed record Result(JsonElement Request, string? Error);
    internal sealed record Step(string Id, string Title, string Status, string Evidence);

    internal static Result ParseBlock(string block)
    {
        Result Invalid(string reason) => new(default, "Plan 未保存：" + reason);
        if (block.Length > MaxBlockChars) return Invalid("文本超过 64000 字符上限。请缩短计划后重试。");
        if (!block.StartsWith(OpenMarker, StringComparison.Ordinal) || !block.EndsWith(CloseMarker, StringComparison.Ordinal))
            return Invalid("请使用完整的 <<XCL2_PLAN>> 和 <</XCL2_PLAN>>，并把计划块放在回复末尾。");
        var payload = block[OpenMarker.Length..^CloseMarker.Length];
        if (payload.Contains("<<XCL2_", StringComparison.Ordinal) || payload.Contains(CloseMarker, StringComparison.Ordinal))
            return Invalid("每次回复只允许一个计划块，不能混入其他控制标记或命令请求。");
        var steps = new List<Dictionary<string, string>>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var removals = new List<string>();
        var nextAction = "";
        var notes = "";
        bool hasNext = false, hasNotes = false;
        int inProgress = 0;
        foreach (var rawLine in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (TryField(line, "remove_step", out var removeId))
            {
                if (removeId.StartsWith('['))
                {
                    if (!removeId.EndsWith(']')) return Invalid("remove_step 的步骤 ID 缺少 ]。");
                    removeId = removeId[1..^1].Trim();
                }
                if (string.IsNullOrWhiteSpace(removeId) || removeId.Length > 128 || removals.Contains(removeId, StringComparer.Ordinal)
                    || removals.Count >= MaxSteps) return Invalid("remove_step 需要唯一、非空的有效步骤 ID。");
                removals.Add(removeId);
                continue;
            }
            if (TryField(line, "next_action", out var fieldValue))
            {
                if (hasNext) return Invalid("next_action 只能出现一次。");
                nextAction = fieldValue; hasNext = true;
                if (nextAction.Length > 10000) return Invalid("next_action 超过 10000 字符。");
                continue;
            }
            if (TryField(line, "notes", out fieldValue))
            {
                if (hasNotes) return Invalid("notes 只能出现一次。");
                notes = fieldValue; hasNotes = true;
                if (notes.Length > 20000) return Invalid("notes 超过 20000 字符。");
                continue;
            }
            if (steps.Count >= MaxSteps) return Invalid("同时最多 100 个步骤。");
            if (!TryStatus(line, out var status, out var prefixLength))
                return Invalid("步骤必须以 ○ / ✓ / – / -、明确的复选框 [ ] / [x] / [~]，或 in_progress: / completed: / pending: 开头；受阻可用 blocked:。");
            if (status == "in_progress" && ++inProgress > 1) return Invalid("同时最多一个步骤正在进行（○ / in_progress）。");
            var title = line[prefixLength..].Trim();
            string? id = null;
            if (title.StartsWith('['))
            {
                var end = title.IndexOf(']');
                if (end < 0) return Invalid("步骤 ID 缺少 ]，请使用 [step-id] 标题。");
                id = title[1..end].Trim();
                title = title[(end + 1)..].Trim();
            }
            if (id != null && (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !ids.Add(id)))
                return Invalid("步骤 ID 必须唯一、非空且不超过 128 字符。");
            var evidence = "";
            const string evidenceSeparator = " | evidence:";
            var separator = title.IndexOf(evidenceSeparator, StringComparison.OrdinalIgnoreCase);
            if (separator >= 0)
            {
                evidence = title[(separator + evidenceSeparator.Length)..].Trim();
                title = title[..separator].TrimEnd();
            }
            if (string.IsNullOrWhiteSpace(title) || title.Length > 1000 || evidence.Length > 4000)
                return Invalid("步骤标题必须为 1 至 1000 字符，evidence 最多 4000 字符。");
            var step = new Dictionary<string, string> { ["title"] = title, ["status"] = status };
            if (separator >= 0) step["evidence"] = evidence;
            // Omitted IDs must remain omitted until the caller can match the previous saved plan.
            // Assigning plan-N here would give reordered steps another step's identity and evidence.
            if (id != null) step["id"] = id;
            steps.Add(step);
        }
        if (steps.Count == 0) return Invalid("至少需要一个明确步骤；普通回答无需输出 Plan 标记。");
        // Construct only the documented task fields. Neither the text body nor notes can set mode,
        // directory access, shell permission, or any other configuration value.
        const string action = "update_task";
        var requestData = new Dictionary<string, object> { ["action"] = action, ["steps"] = steps };
        if (removals.Count > 0) requestData["remove_step_ids"] = removals;
        if (hasNext) requestData["next_action"] = nextAction;
        if (hasNotes) requestData["notes"] = notes;
        var request = JsonSerializer.SerializeToElement(requestData);
        var error = AiControlProtocol.ValidateAccessArguments(request);
        return error == null ? new(request, null) : Invalid(error);
    }

    private static bool TryStatus(string line, out string status, out int prefixLength)
    {
        status = ""; prefixLength = 0;
        // Numbering or a Markdown bullet is presentation, never a substitute for a status.
        int offset = 0;
        var numberEnd = 0;
        while (numberEnd < line.Length && char.IsAsciiDigit(line[numberEnd])) numberEnd++;
        if (numberEnd > 0 && numberEnd < line.Length && line[numberEnd] is '.' or ')' or '、')
        {
            offset = numberEnd + 1;
            while (offset < line.Length && char.IsWhiteSpace(line[offset])) offset++;
            line = line[offset..];
        }
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
        {
            var rest = 2;
            while (rest < line.Length && char.IsWhiteSpace(line[rest])) rest++;
            if (TryRawStatus(line[rest..], out status, out var nestedLength))
            { prefixLength = offset + rest + nestedLength; return true; }
        }
        if (!TryRawStatus(line, out status, out var rawLength)) return false;
        prefixLength = offset + rawLength; return true;
    }

    private static bool TryRawStatus(string line, out string status, out int prefixLength)
    {
        status = ""; prefixLength = 0;
        foreach (var entry in new[] { ("[ ]", "pending"), ("[x]", "completed"), ("[X]", "completed"),
            ("[~]", "in_progress"), ("[/]", "in_progress"),
            ("○", "in_progress"), ("◯", "in_progress"), ("●", "in_progress"), ("◉", "in_progress"),
            ("✓", "completed"), ("✔", "completed"), ("✅", "completed"), ("☑", "completed"),
            ("–", "pending"), ("—", "pending"), ("−", "pending"), ("-", "pending"),
            ("in_progress:", "in_progress"), ("completed:", "completed"), ("pending:", "pending"), ("blocked:", "blocked") })
        {
            if (!line.StartsWith(entry.Item1, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Item1 == "-" && line.Length > 1 && !char.IsWhiteSpace(line[1])) continue;
            status = entry.Item2; prefixLength = entry.Item1.Length;
            if (prefixLength < line.Length && line[prefixLength] == '\uFE0F') prefixLength++;
            return true;
        }
        return false;
    }

    private static bool TryField(string line, string name, out string value)
    {
        value = "";
        if (line.Length <= name.Length || !line.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            || line[name.Length] is not (':' or '：')) return false;
        value = line[(name.Length + 1)..].Trim(); return true;
    }

    internal static string? ValidateSteps(JsonElement steps)
    {
        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() > MaxSteps)
            return "steps 必须是最多 100 项的步骤对象数组。";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int inProgress = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object) return "每个步骤必须是 JSON 对象。";
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? title = null;
            foreach (var property in step.EnumerateObject())
            {
                var name = property.Name.ToLowerInvariant();
                if (name is not ("id" or "title" or "status" or "evidence")) continue;
                if (!fields.Add(name)) return "步骤含重复字段：" + property.Name + "。";
                if (property.Value.ValueKind != JsonValueKind.String) return "步骤 " + property.Name + " 必须是字符串。";
                var text = property.Value.GetString()!;
                if (name == "id" && (string.IsNullOrWhiteSpace(text) || text.Length > 128 || !ids.Add(text.Trim())))
                    return "步骤 ID 必须唯一、非空且不超过 128 字符。";
                if (name == "status")
                {
                    if (text is not ("pending" or "in_progress" or "completed" or "blocked")) return "任务步骤状态无效。";
                    if (text == "in_progress" && ++inProgress > 1) return "同时最多一个步骤处于 in_progress。";
                }
                if (name == "title") title = text;
                if (name == "title" && text.Length > 1000 || name == "evidence" && text.Length > 4000)
                    return "任务步骤 title 或 evidence 超过长度上限。";
            }
            if (string.IsNullOrWhiteSpace(title)) return "每个步骤都必须包含非空字符串 title。";
        }
        return null;
    }

    /// <summary>Normalizes only explicit plan data, retaining identity and omitted state from saved steps.</summary>
    internal static IReadOnlyList<Step> NormalizeSteps(JsonElement steps, IReadOnlyList<Step>? existing = null)
    {
        var error = ValidateSteps(steps);
        if (error != null) throw new ArgumentException(error);
        existing ??= Array.Empty<Step>();
        var result = new List<Step>();
        // Reserve both old IDs and new explicit IDs so generated IDs cannot steal a later step's ID.
        var reserved = new HashSet<string>(existing.Select(step => step.Id), StringComparer.Ordinal);
        foreach (var raw in steps.EnumerateArray())
            if (StepString(raw, "id") is string explicitId) reserved.Add(explicitId.Trim());
        var used = new HashSet<string>(StringComparer.Ordinal);
        var titleCounts = steps.EnumerateArray().Select(raw => StepString(raw, "title")!.Trim())
            .GroupBy(title => title, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var raw in steps.EnumerateArray())
        {
            var title = StepString(raw, "title")!.Trim();
            var id = StepString(raw, "id")?.Trim();
            Step? previous = null;
            if (id != null)
                previous = existing.FirstOrDefault(step => step.Id == id);
            else
            {
                var matching = existing.Where(step => step.Title.Trim() == title).ToArray();
                if (titleCounts[title] == 1 && matching.Length == 1 && !used.Contains(matching[0].Id))
                {
                    previous = matching[0];
                    id = previous.Id;
                }
            }
            if (id == null)
            {
                var number = result.Count + 1;
                do { id = "plan-" + number++; } while (!reserved.Add(id));
            }
            if (!used.Add(id)) throw new ArgumentException("步骤 ID 必须唯一；请为重复标题提供明确且不同的 ID。");
            var status = StepString(raw, "status") ?? previous?.Status ?? "pending";
            var evidence = StepString(raw, "evidence") ?? previous?.Evidence ?? "";
            result.Add(new(id, title, status, evidence));
        }
        if (result.Count(step => step.Status == "in_progress") > 1)
            throw new ArgumentException("同时最多一个步骤处于 in_progress；请明确更新前一个正在进行步骤的状态。");
        error = ValidateSteps(JsonSerializer.SerializeToElement(result));
        if (error != null) throw new ArgumentException(error);
        return result;
    }

    internal static void EnsureCompleteReplacement(IReadOnlyList<Step> previous, IReadOnlyList<Step> proposed, JsonElement request)
    {
        var retained = new HashSet<string>(proposed.Select(step => step.Id), StringComparer.Ordinal);
        var removed = request.TryGetProperty("remove_step_ids", out var ids)
            ? new HashSet<string>(ids.EnumerateArray().Select(id => id.GetString()!.Trim()), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (removed.Any(id => !previous.Any(step => step.Id == id) || retained.Contains(id)))
            throw new ArgumentException("移除的步骤 ID 必须存在于旧计划，并且不能同时保留在新 steps 中。");
        var missing = previous.Where(step => !retained.Contains(step.Id) && !removed.Contains(step.Id)).Select(step => step.Id).ToArray();
        if (missing.Length > 0)
            throw new ArgumentException("完整计划遗漏了旧步骤，未保存。请保留全部原步骤；确需删除时用 remove_step_ids 明确列出 ID，文本协议可逐行写 remove_step: [ID]。遗漏 ID："
                + string.Join(", ", missing));
    }

    private static string? StepString(JsonElement step, string name)
    {
        foreach (var property in step.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value.GetString();
        return null;
    }
}
