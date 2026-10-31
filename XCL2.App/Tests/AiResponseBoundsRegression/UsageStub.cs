using System.Text.Json;
namespace XCL2.App.Services;
// Only accounting is stubbed; all decoding, buffering, limits and cancellation use production code.
internal static class AiUsageAccounting
{
    internal sealed record Usage;
    internal static Usage? Parse(JsonElement root) => null;
}
