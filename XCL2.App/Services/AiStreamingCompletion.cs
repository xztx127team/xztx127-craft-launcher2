using System.IO;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Incremental SSE decoding. A partial control marker is never shown as answer text.</summary>
internal static class AiStreamingCompletion
{
    internal static string VisibleText(string text)
    {
        var start = text.IndexOf("<<XCL2_", StringComparison.Ordinal);
        if (start >= 0) return text[..start].TrimEnd();
        const string prefix = "<<XCL2_";
        for (int n = Math.Min(prefix.Length - 1, text.Length); n > 0; n--)
            if (text.EndsWith(prefix[..n], StringComparison.Ordinal)) return text[..^n];
        return text;
    }

    internal static async Task<string> ReadAsync(Stream stream, Action<string>? content,
        Action<string, string>? reasoning, CancellationToken ct, Action<AiUsageAccounting.Usage>? usage = null,
        Action<JsonElement>? providerError = null, Action? responseStarted = null, bool anthropic = false)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var lines = new AiResponseLimits.BoundedLineReader(reader);
        var answer = new StringBuilder();
        var thought = new StringBuilder();
        var summary = new StringBuilder();
        var frame = new StringBuilder();
        bool finished = false;
        bool completionSeen = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastPublish = -32;
        int publishedLength = -1;
        long lastReasoningPublish = -100;
        int publishedThoughtLength = 0;
        int publishedSummaryLength = 0;
        bool receivedOutput = false;
        void PublishAnswer(bool force = false)
        {
            if (answer.Length == 0 || publishedLength == answer.Length) return;
            if (!force && publishedLength >= 0 && clock.ElapsedMilliseconds - lastPublish < 32) return;
            publishedLength = answer.Length; lastPublish = clock.ElapsedMilliseconds;
            content?.Invoke(VisibleText(answer.ToString()));
        }
        void PublishReasoning(bool force = false)
        {
            if (publishedThoughtLength == thought.Length && publishedSummaryLength == summary.Length) return;
            // As the drawer grows, reduce string allocations and layout updates, not its content.
            int publishInterval = (int)Math.Clamp(((long)thought.Length + summary.Length) / 1000, 100, 750);
            if (!force && (publishedThoughtLength > 0 || publishedSummaryLength > 0)
                && clock.ElapsedMilliseconds - lastReasoningPublish < publishInterval) return;
            publishedThoughtLength = thought.Length; publishedSummaryLength = summary.Length;
            lastReasoningPublish = clock.ElapsedMilliseconds;
            reasoning?.Invoke(thought.ToString(), summary.ToString());
        }
        void Dispatch()
        {
            if (frame.Length == 0) return;
            var data = frame.ToString().Trim(); frame.Clear();
            if (data == "[DONE]") { finished = true; return; }
            // Some compatible providers omit the empty line between complete data objects.
            // Decode every complete envelope in order, while retaining real partial output on error.
            AiProviderJson.ParseFrames(data, "AI 流式接口", allowSequence: true, onFrame: DispatchFrame);
        }
        void DispatchFrame(JsonElement root)
        {
            var usageSnapshot = AiUsageAccounting.Parse(root);
            if (usageSnapshot != null) usage?.Invoke(usageSnapshot);
            if (root.TryGetProperty("error", out var error))
            {
                providerError?.Invoke(root);
                throw new IOException("流式接口返回错误：" + error.ToString()[..Math.Min(600, error.ToString().Length)]);
            }
            if (anthropic)
            {
                var type = root.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : "";
                if (type == "message_start")
                {
                    if (root.TryGetProperty("message", out var start))
                    {
                        var startUsage = AiUsageAccounting.Parse(start);
                        if (startUsage != null) usage?.Invoke(startUsage);
                    }
                    return;
                }
                if (type == "message_stop") { finished = true; return; }
                string text = "", thinking = "";
                string? nativeFinish = null;
                if (type == "content_block_start" && root.TryGetProperty("content_block", out var block)
                    && block.ValueKind == JsonValueKind.Object)
                {
                    var blockType = block.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String ? bt.GetString() : "";
                    if (blockType is "server_tool_use" or "tool_use" or "redacted_thinking")
                    {
                        if (!receivedOutput) { receivedOutput = true; responseStarted?.Invoke(); }
                    }
                    if (blockType == "web_search_tool_result" && block.TryGetProperty("content", out var result)
                        && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("type", out var resultType)
                        && resultType.GetString() == "web_search_tool_result_error")
                        throw new InvalidOperationException("联网搜索执行失败。当前模型或接口可能不支持搜索，建议在 AI 设置中关闭联网搜索后重试。");
                    if (blockType == "text" && block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) text = t.GetString() ?? "";
                    if (blockType == "thinking" && block.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String) thinking = th.GetString() ?? "";
                }
                else if (type == "content_block_delta" && root.TryGetProperty("delta", out var d)
                    && d.ValueKind == JsonValueKind.Object)
                {
                    var deltaType = d.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() : "";
                    if (deltaType == "text_delta" && d.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) text = t.GetString() ?? "";
                    if (deltaType == "thinking_delta" && d.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String) thinking = th.GetString() ?? "";
                    // Tool arguments, signatures and hidden thinking never become text/actions.
                }
                else if (type == "message_delta" && root.TryGetProperty("delta", out var md)
                    && md.ValueKind == JsonValueKind.Object && md.TryGetProperty("stop_reason", out var sr)
                    && sr.ValueKind == JsonValueKind.String)
                {
                    var nativeStop = sr.GetString();
                    if (nativeStop is "pause_turn" or "tool_use")
                        throw new InvalidOperationException("Anthropic 回答尚未完成，已保留内容；请关闭联网搜索或检查接口工具支持。未执行不完整指令。");
                    nativeFinish = nativeStop == "max_tokens" ? "length" : nativeStop;
                }
                else return;
                root = JsonSerializer.SerializeToElement(new { choices = new[] {
                    new { delta = new { content = text, reasoning_content = thinking }, finish_reason = nativeFinish }
                } });
            }
            if (!root.TryGetProperty("choices", out var choices)) return;
            if (choices.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("AI 流式接口的 choices 必须是数组。请检查接口兼容性。");
            if (choices.GetArrayLength() == 0) return;
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("AI 流式接口的 choice 必须是对象。请检查接口兼容性。");
            var stopReason = choice.TryGetProperty("finish_reason", out var stop) && stop.ValueKind == JsonValueKind.String ? stop.GetString() : null;
            var delta = choice.TryGetProperty("delta", out var streamed) ? streamed
                : choice.TryGetProperty("message", out var message) ? message : default;
            if (delta.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
                throw new InvalidOperationException("AI 流式接口的 delta/message 必须是对象。请检查接口兼容性。");
            var parsed = AiCompletionContentParser.Parse(delta, preserveWhitespace: true);
            if (!receivedOutput && (parsed.Content.Length > 0 || parsed.Reasoning.Length > 0 || parsed.ReasoningSummary.Length > 0
                || HasGeneratedPayload(delta)))
            {
                receivedOutput = true;
                // Signal even a hidden control prefix, before throttled UI publication, so callers
                // cannot retry and replay a partially received response or command.
                responseStarted?.Invoke();
            }
            long incomingLength = (long)parsed.Content.Length + parsed.Reasoning.Length + parsed.ReasoningSummary.Length;
            long receivedLength = (long)answer.Length + thought.Length + summary.Length;
            if (incomingLength > AiResponseLimits.MaxGeneratedChars - receivedLength)
                throw new AiResponseLimitException("回答及推理内容超过累计长度上限，已保留收到的内容；未执行不完整指令。");
            if (parsed.Content.Length > 0)
            {
                answer.Append(parsed.Content);
                PublishAnswer();
            }
            if (parsed.Reasoning.Length > 0 || parsed.ReasoningSummary.Length > 0)
            {
                thought.Append(parsed.Reasoning);
                summary.Append(parsed.ReasoningSummary);
                PublishReasoning();
            }
            if (stopReason is "length" or "content_filter")
                throw new IOException("回答被接口截断，已保留收到的内容；未执行不完整指令。");
            if (stopReason != null) completionSeen = true; // usage may arrive in a later, choices:[] frame
        }
        try
        {
            while (true)
            {
                var line = await lines.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) { Dispatch(); break; }
                if (line.Length == 0) { Dispatch(); if (finished) break; continue; }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var value = line[5..]; if (value.StartsWith(' ')) value = value[1..];
                if (value.Trim() == "[DONE]") { Dispatch(); finished = true; break; }
                if ((long)value.Length + Environment.NewLine.Length > AiResponseLimits.MaxSseFrameChars - frame.Length)
                    throw new AiResponseLimitException("SSE 帧超过长度上限，已保留收到的内容；未执行不完整指令。");
                frame.AppendLine(value);
                // Publish a complete JSON object immediately, including providers that omit
                // blank separators. Genuine multiline JSON waits until it is complete.
                if (AiProviderJson.IsCompleteObject(frame.ToString())) Dispatch();
            }
        }
        finally { PublishAnswer(force: true); PublishReasoning(force: true); }
        if (!finished && !completionSeen) throw new IOException("流式连接提前中断，已保留部分回答；未执行不完整指令。");
        return answer.ToString();
    }

    private static bool HasGeneratedPayload(JsonElement delta)
    {
        if (delta.ValueKind != JsonValueKind.Object) return false;
        // Opaque/encrypted reasoning and structured output must also stop automatic replay.
        // We inspect presence only: these fields are never decoded, displayed or executed here.
        foreach (var name in new[] { "content", "reasoning_content", "reasoning", "reasoning_summary",
            "reasoning_details", "tool_calls", "function_call", "refusal", "audio" })
        {
            if (!delta.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
                || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0
                || value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any()) return true;
        }
        return false;
    }
}
