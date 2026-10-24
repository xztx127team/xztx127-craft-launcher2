namespace XCL2.App.Models
{
    public sealed class GameProcessInfo
    {
        public string GameDir { get; set; } = "";
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public bool UserRequestedClose { get; set; }
        public string Output { get; set; } = "";
        public string GetOutputSnapshot() => Output;
    }
}
namespace XCL2.App.Services
{
    public record JvmArgIssue(string Title);
    public static class JvmArgumentDiagnostics { public static IReadOnlyList<JvmArgIssue> Detect(string _) => Array.Empty<JvmArgIssue>(); }
    public static class OpenGlTroubleshooter { public static bool IsOpenGlFailure(string _) => false; }
}
