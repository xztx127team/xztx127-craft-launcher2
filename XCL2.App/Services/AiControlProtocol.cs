using System.Globalization;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Parses one explicit terminal control block. A text Plan is normalized to update_task; malformed requests never execute.</summary>
internal static class AiControlProtocol
{
    internal sealed record Result(string? Kind, string DisplayText, JsonElement Request, string? Error)
    {
        internal bool HasMarker => Kind != null;
        internal bool IsValid => HasMarker && Error == null;
    }

    internal static Result Parse(string? reply)
    {
        var text = reply ?? "";
        int start = -1;
        string? kind = null;
        foreach (var candidate in new[] { "ACCESS", "ASK", "AGENT", "PLAN" })
        {
            var marker = "<<XCL2_" + candidate + (candidate == "PLAN" ? "" : ":");
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && (start < 0 || index < start)) { start = index; kind = candidate; }
        }
        if (start < 0) return new(null, text, default, null);
        var display = text[..start].TrimEnd();
        var block = text[start..].TrimEnd();
        var fenceError = UnwrapTerminalFence(text, start, ref display, ref block);
        if (fenceError != null) return new(kind == "PLAN" ? "ACCESS" : kind, display, default, fenceError);
        if (kind == "PLAN")
        {
            var plan = AiPlanProtocol.ParseBlock(block);
            return new("ACCESS", display, plan.Request, plan.Error);
        }
        if (!block.EndsWith(">>", StringComparison.Ordinal))
            return new(kind, display, default, "控制标记必须完整并位于回复末尾，未执行。请重新生成一个完整标记。");
        var prefixLength = ("<<XCL2_" + kind + ":").Length;
        var payload = block[prefixLength..^2];
        if (payload.Length > 1_600_000) return new(kind, display, default, "工具请求超过长度上限，未执行。");
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new(kind, display, default, "控制标记中的 JSON 必须是一个对象，未执行。");
            if (HasDuplicateProperties(doc.RootElement))
                return new(kind, display, default, "JSON 含重复字段，无法确定实际参数，未执行。请每个字段只提供一次。");
            var request = doc.RootElement.Clone();
            string? error = kind switch
            {
                "ACCESS" when string.IsNullOrWhiteSpace(String(request, "action")) => "工具 action 必须是非空字符串，未执行。",
                "AGENT" when string.IsNullOrWhiteSpace(String(request, "task")) || String(request, "task")!.Length > 6000 => "子任务 task 必须是 1 至 6000 字符的字符串，未执行。",
                "ASK" when !HasQuestion(request) => "提问必须包含非空 question，或包含问题对象的 questions 数组，未显示提问卡片。",
                _ => null
            };
            return new(kind, display, request, error);
        }
        catch (JsonException ex)
        {
            // Do not remove braces, pick the first of several commands, or guess invalid arguments.
            // The caller can feed this precise diagnostic back to the model without ending the turn.
            return new(kind, display, default, $"控制标记 JSON 不合法（第 {(ex.LineNumber ?? 0) + 1} 行，字节 {ex.BytePositionInLine ?? 0}）。只允许一个 JSON 对象及一种控制标记；检查多余括号、逗号和转义字符。未执行。");
        }
    }

    internal static string Strip(string? reply, string kind)
    {
        var parsed = Parse(reply);
        return parsed.Kind == kind ? parsed.DisplayText : reply ?? "";
    }

    internal static string? ValidateAccessArguments(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object) return "工具参数必须是 JSON 对象。";
        foreach (var name in new[] { "action", "key", "path", "content", "shell", "command", "type", "sha256", "old_text", "new_text", "glob", "query", "next_action", "notes" })
            if (request.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return $"参数 {name} 必须是字符串，实际为 {value.ValueKind}。";
        if (request.TryGetProperty("value", out var setting) && setting.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            return "参数 value 必须是字符串、数值或布尔值。";
        foreach (var name in new[] { "timeout_seconds", "start_line", "line_count", "limit", "x", "y", "process_id" })
            if (request.TryGetProperty(name, out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
                return $"参数 {name} 必须是 32 位整数。";
        foreach (var name in new[] { "regex", "case_sensitive" })
            if (request.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return $"参数 {name} 必须是布尔值 true 或 false。";
        var action = String(request, "action");
        if (action is not ("apply_setting" or "read_config" or "request_directory" or "read_workspace_file"
            or "write_workspace_file" or "delete_workspace_file" or "read_file_range" or "registry_read" or "registry_write"
            or "patch_workspace_file" or "search_project" or "list_project_files" or "read_log" or "list_workspace" or "update_task" or "run_command" or "mouse_move" or "mouse_click" or "mouse_right_click" or "key_press" or "keyboard_type" or "window_open" or "window_close"))
            return "未知工具 action：" + action + "。请使用已公开的工具名称。";
        string[] required = action switch
        {
            "apply_setting" => new[] { "key" },
            "read_config" => new[] { "key" },
            "request_directory" or "read_workspace_file" or "write_workspace_file" or "delete_workspace_file" or "read_file_range" or "registry_read" or "registry_write" => new[] { "path" },
            "patch_workspace_file" => new[] { "path", "sha256", "old_text" },
            "window_open" => new[] { "path" },
            "key_press" => new[] { "key" },
            "search_project" => new[] { "path", "query" },
            "list_project_files" => new[] { "path" },
            "read_log" or "list_workspace" or "update_task" or "run_command" => Array.Empty<string>(),
            _ => new[] { "action" }
        };
        foreach (var name in required)
            if (string.IsNullOrWhiteSpace(String(request, name))) return $"参数 {name} 缺失或为空。";
        if (action == "write_workspace_file" && !HasString(request, "content")) return "write_workspace_file 缺少字符串 content。";
        if (action == "patch_workspace_file" && !HasString(request, "new_text")) return "patch_workspace_file 缺少字符串 new_text。";
        if (action == "run_command" && string.IsNullOrWhiteSpace(String(request, "command")) && string.IsNullOrWhiteSpace(String(request, "content")))
            return "run_command 缺少 command。";
        if (action == "apply_setting" && (!request.TryGetProperty("value", out var settingValue) || settingValue.ValueKind == JsonValueKind.Null))
            return "apply_setting 缺少 value。";
        if (action == "registry_write")
        {
            if (!HasString(request, "key")) return "registry_write 缺少字符串 key；默认值请明确传空字符串。";
            var text = Scalar(request, "value");
            if (text == null) return "registry_write 缺少 value。";
            var type = (String(request, "type") ?? "String").ToLowerInvariant();
            if (type is not ("string" or "expandstring" or "multistring" or "dword" or "qword"))
                return "registry_write 的 type 只能是 String / ExpandString / MultiString / DWord / QWord。";
            if (type == "dword" && !uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return "DWord 的 value 必须是 0 至 4294967295 的整数。";
            if (type == "qword" && !ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return "QWord 的 value 必须是 0 至 18446744073709551615 的整数。";
        }
        if (action == "keyboard_type" && (!HasString(request, "content") || String(request, "content")!.Length is < 1 or > 2000))
            return "keyboard_type 的 content 必须是 1 至 2000 字符的字符串。";
        if (action == "mouse_move" && (!request.TryGetProperty("x", out _) || !request.TryGetProperty("y", out _)))
            return "mouse_move 需要整数坐标 x 和 y。";
        if (action == "window_close" && (!request.TryGetProperty("process_id", out var processId) || processId.GetInt32() <= 0))
            return "window_close 需要正整数 process_id。";
        if (action == "update_task") return ValidateTaskArguments(request);
        return null;
    }

    private static string? ValidateTaskArguments(JsonElement request)
    {
        if (String(request, "notes")?.Length > 20000 || String(request, "next_action")?.Length > 10000)
            return "任务 notes 或 next_action 超过长度上限。";
        if (request.TryGetProperty("remove_step_ids", out var removals))
        {
            if (!request.TryGetProperty("steps", out _) || removals.ValueKind != JsonValueKind.Array
                || removals.GetArrayLength() > AiPlanProtocol.MaxSteps)
                return "remove_step_ids 必须随完整 steps 一起提供，且为最多 100 个步骤 ID 的数组。";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in removals.EnumerateArray())
                if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
                    || id.GetString()!.Length > 128 || !ids.Add(id.GetString()!.Trim()))
                    return "remove_step_ids 中的步骤 ID 必须唯一、非空且不超过 128 字符。";
        }
        if (!request.TryGetProperty("steps", out var steps)) return null;
        return AiPlanProtocol.ValidateSteps(steps);
    }

    private static string? UnwrapTerminalFence(string text, int start, ref string display, ref string block)
    {
        // A complete fence may wrap the ONE terminal control block. Never extract a control
        // marker from a larger source-code example, nor strip arbitrary trailing content.
        var prefix = text[..start];
        int markerLine = prefix.LastIndexOf('\n') + 1;
        if (!string.IsNullOrWhiteSpace(prefix[markerLine..]) || markerLine == 0) return null;
        var before = prefix.TrimEnd();
        int openingStart = before.LastIndexOf('\n') + 1;
        var opening = before[openingStart..].Trim();
        if (!TryFence(opening, out var fence, out var language)) return null;
        if (language.Length > 0 && language.ToLowerInvariant() is not ("text" or "json" or "plan" or "xcl2" or "plaintext"))
            return "控制标记的代码围栏只支持 text / json / plan / xcl2 / plaintext，未执行。";
        // Hide the opening fence as well as the control payload in the transcript.
        display = before[..openingStart].TrimEnd();
        var lastLine = block.LastIndexOf('\n');
        if (lastLine < 0 || !block[(lastLine + 1)..].Trim().Equals(fence, StringComparison.Ordinal))
            return "控制标记的代码围栏必须完整闭合，并位于回复末尾，未执行。";
        block = block[..lastLine].TrimEnd();
        return null;
    }

    private static bool TryFence(string line, out string fence, out string language)
    {
        fence = ""; language = "";
        if (line.Length < 3 || line[0] is not ('`' or '~')) return false;
        var length = 0;
        while (length < line.Length && line[length] == line[0]) length++;
        if (length < 3) return false;
        fence = line[..length]; language = line[length..].Trim();
        return true;
    }

    internal static string? String(JsonElement request, string name)
        => request.ValueKind == JsonValueKind.Object && request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string? Scalar(JsonElement request, string name)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.GetRawText() : null;
    }

    private static bool HasString(JsonElement request, string name)
        => request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;

    private static bool HasQuestion(JsonElement request)
    {
        bool Valid(JsonElement question) => question.ValueKind == JsonValueKind.Object
            && !string.IsNullOrWhiteSpace(String(question, "question") ?? String(question, "text") ?? String(question, "title"));
        return request.TryGetProperty("questions", out var questions)
            ? questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() > 0 && questions.EnumerateArray().All(Valid)
            : Valid(request);
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }
}
