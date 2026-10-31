using System.Text;
using XCL2.App.Services;
var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
var buffer = new BoundedLogBuffer(1024, 512);
var discarded = new StringBuilder(); var complete = new StringBuilder();
long revision = -1;
Check(buffer.TryGetChangedText(ref revision, out var empty) && empty == "", "initial empty snapshot available");
Check(!buffer.TryGetChangedText(ref revision, out _), "unchanged revision skips snapshots");
for (var i = 0; i < 1000; i++) { var text = $"line {i}: retained on disk\n"; complete.Append(text); discarded.Append(buffer.Append(text)); Check(buffer.Length <= 1024, "live cache stays bounded"); }
Check(buffer.HasDiscarded && buffer.GetText().StartsWith(BoundedLogBuffer.TruncatedNotice), "truncation is visible");
Check(discarded + buffer.GetText(false) == complete.ToString(), "persisted prefixes plus retained tail reconstruct entire session in order");
Check(buffer.TryGetChangedText(ref revision, out var changed), "changed revision produces snapshot");
Check(ReferenceEquals(changed, buffer.GetText()), "unchanged consumers reuse immutable cached snapshot");
Check(!buffer.TryGetChangedText(ref revision, out _), "polling unchanged log performs no snapshot work");
var huge = new string('X', 4000); discarded.Append(buffer.Append(huge)); complete.Append(huge);
Check(buffer.Length <= 1024, "single oversized line cannot defeat bound");
Check(discarded + buffer.GetText(false) == complete.ToString(), "oversized line prefix can also be preserved on disk");
var unicodeBuffer = new BoundedLogBuffer(256, 128);
var unicodeLine = string.Concat(Enumerable.Repeat("😀", 201)) + "Z";
var unicodePrefix = unicodeBuffer.Append(unicodeLine);
Check(unicodePrefix + unicodeBuffer.GetText(false) == unicodeLine, "oversized Unicode lines reconstruct without splitting surrogate pairs");
Check(!char.IsHighSurrogate(unicodePrefix[^1]), "spooled prefix does not end with half of a Unicode scalar");
var folder = Path.Combine(Path.GetTempPath(), "XCL2-log-regression-" + Guid.NewGuid().ToString("N"));
XCL2.App.App.DataDir = folder;
try
{
    LauncherLogService.BeginSession(new ConfigService());
    long serviceRevision = -1;
    Check(LauncherLogService.TryGetBufferedText(ref serviceRevision, out _), "service initial version is readable");
    Check(!LauncherLogService.TryGetBufferedText(ref serviceRevision, out _), "service unchanged poll skips string snapshot");
    LauncherLogService.AppendLine("--accessToken spill-token-fixture password=spill-password-fixture\nAuthorization: Bearer spill-header-fixture\nmail spill-mail-fixture@example.org");
    Check(!LauncherLogService.GetBufferedText().Contains("spill-token-fixture"), "live log is sanitized before spilling");
    for (var i = 0; i < 16000; i++) LauncherLogService.AppendLine($"row-{i:D5}-" + new string('x', 90));
    Check(LauncherLogService.GetBufferedText().Contains(BoundedLogBuffer.TruncatedNotice), "real logger has bounded view with notice");
    var spool = (string?)typeof(LauncherLogService).GetField("_spoolPath", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null);
    Check(spool != null && File.Exists(spool), "large session actually spilled to disk");
    var spilledText = File.ReadAllText(spool!);
    foreach (var secret in new[] { "spill-token-fixture", "spill-password-fixture", "spill-header-fixture", "spill-mail-fixture@example.org" })
        Check(!spilledText.Contains(secret), "temporary log does not contain " + secret);
    LauncherLogService.AppendLine("refresh_token=tail-token-fixture");
    LauncherLogService.EndSessionAndFlush();
    var persisted = File.ReadAllText(LauncherLogService.SessionLogPath!);
    Check(!persisted.Contains("spill-token-fixture") && !persisted.Contains("tail-token-fixture"), "final log protects both spooled prefix and retained tail");
    Check(!persisted.Contains(BoundedLogBuffer.TruncatedNotice), "complete saved session does not pretend it is a truncated view");
    Check(persisted.Contains("row-00000-") && persisted.Contains("row-15999-"), "real service preserves both spilled prefix and final tail");
    Check(persisted.IndexOf("row-00000-", StringComparison.Ordinal) < persisted.IndexOf("row-15999-", StringComparison.Ordinal), "saved session preserves prefix ordering");
    var rows = persisted.Split('\n').Where(line => line.Contains("row-", StringComparison.Ordinal)).ToArray();
    Check(rows.Length == 16000, "session spill and flush neither lose nor duplicate log rows");
}
finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
Console.WriteLine($"PASS {count} bounded launcher log checks");
