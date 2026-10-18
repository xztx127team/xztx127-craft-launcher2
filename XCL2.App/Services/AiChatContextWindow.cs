using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>A bounded suffix of ordinary Chat messages. Never imports execution history or its summaries.</summary>
internal static class AiChatContextWindow
{
    internal const int MaximumMessages = 80;
    internal const int MaximumImages = 8;
    internal const long MaximumImageBytes = AiAttachmentService.MaxMessageImageBytes;
    internal sealed record Selection(IReadOnlyList<AiChatMessage> Messages, int OmittedMessages);

    internal static Selection Select(IReadOnlyList<AiChatMessage> history, AiChatMessage? currentUser, int tokenBudget)
    {
        var eligible = history.Where(message => message != null && message.Role != AiMessageRole.System
            && (message.WorkMode == AiWorkMode.Chat || ReferenceEquals(message, currentUser))).ToList();
        var selected = new List<AiChatMessage>();
        long tokens = 0, imageBytes = 0;
        int images = 0;
        foreach (var message in eligible.AsEnumerable().Reverse())
        {
            var attachments = (message.Attachments ?? new()).Where(item => item.Kind == AiAttachmentKind.Image).ToList();
            long messageImagesBytes = attachments.Sum(item => (long)(item.ImageBase64?.Length ?? 0) * 3 / 4);
            long messageTokens = (long)AiTokenEstimator.Estimate(message.Content ?? "") + attachments.Count * 1024L + 8;
            bool fits = selected.Count < MaximumMessages && messageTokens <= tokenBudget - tokens
                && attachments.Count <= MaximumImages - images && messageImagesBytes <= MaximumImageBytes - imageBytes;
            if (!fits)
            {
                if (ReferenceEquals(message, currentUser))
                    throw new InvalidOperationException("这条纯聊天消息超过当前上下文容量，请拆分文字或减少图片后重试；没有发送给模型。");
                break; // Keep a coherent recent suffix; never replace a skipped turn with older data.
            }
            selected.Add(message); tokens += messageTokens; images += attachments.Count; imageBytes += messageImagesBytes;
        }
        selected.Reverse();
        // Do not present an orphaned assistant reply as if it opened the remaining conversation.
        while (selected.Count > 0 && selected[0].Role == AiMessageRole.Assistant) selected.RemoveAt(0);
        return new(selected, eligible.Count - selected.Count);
    }
}
