using System.Runtime.InteropServices;
using XCL2.App.Services;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
var session = new ExperimentalPasswordSession();
var password = session.Issue("crash");
Check(!session.Consume("memory", password), "A password cannot unlock another feature.");
Check(session.Consume("crash", password.ToLowerInvariant()), "The current password is accepted.");
Check(!session.Consume("crash", password), "A consumed password cannot be replayed.");
var first = session.Issue("crash");
var second = session.Issue("crash");
Check(first == second, "Viewing again retains this launch session password and renews authorization.");
Check(session.Consume("crash", second), "The replacement ticket remains valid.");
var nextSession = new ExperimentalPasswordSession();
Check(!nextSession.Consume("crash", session.Issue("crash")), "Tickets do not survive a process session.");
var blocked = session.Issue("crash");
for (int i = 0; i < 5; i++) Check(!session.Consume("crash", "wrong"), "Wrong passwords are rejected.");
Check(!session.Consume("crash", blocked), "Five failed attempts revoke the ticket.");
var concurrent = session.Issue("memory");
int accepted = 0;
Parallel.For(0, 20, _ => { if (session.Consume("memory", concurrent)) Interlocked.Increment(ref accepted); });
Check(accepted == 1, "Concurrent requests consume a password once.");

var lengths = new HashSet<int>();
var sessionPasswords = new HashSet<string>(StringComparer.Ordinal);
for (var sample = 0; sample < 160; sample++)
{
    var run = new ExperimentalPasswordSession();
    foreach (var feature in ExperimentalFeatureCatalog.All)
    {
        var generated = run.Issue(feature.Id);
        Check(generated.Length is >= 10 and <= 25 && generated.All(char.IsAsciiLetterOrDigit), "Passwords are 10–25 alphanumeric characters without separators.");
        Check(sessionPasswords.Add(generated), "Independent startup sessions/features do not reuse passwords.");
        lengths.Add(generated.Length);
        Check(run.Issue(feature.Id) == generated, "Repeated viewing within the same launch preserves the password.");
    }
}
Check(lengths.Count == 16, "Random length generation exercises both endpoints and every intermediate length.");

var drag = new FeatureSearchPointerDrag(); drag.Begin(10, 20);
Check(drag.Move(12, 22) == null, "A click is not a drag.");
Check(drag.Move(40, 50) == (30d, 30d), "Movement starts without a hold timer.");
Check(drag.Move(50, 55) == (40d, 35d), "The press origin remains fixed during the gesture.");
drag.End(); Check(drag.Move(100, 100) == null, "Release ends the gesture.");
var area = new AprilFoolsMotionPolicy.Box(-1920, 0, 1920, 1080);
for (int i = 0; i < 100; i++)
{
    var cursor = new AprilFoolsMotionPolicy.Point(-1920 + i * 19, i * 10);
    var target = AprilFoolsMotionPolicy.Evade(new(-1000, 400, 180, 50), area, cursor, 240, new Random(i));
    Check(target.X >= area.X && target.Right <= area.Right && target.Y >= area.Y && target.Bottom <= area.Bottom, "Dodge stays on screen.");
    Check(AprilFoolsMotionPolicy.Distance(cursor, target) >= 240, "Dodge leaves the avoidance radius when space permits.");
}
Check(!TerracottaArchitecture.IsCompatible(TerracottaArchitecture.Machine.X64, Architecture.X86), "32-bit Windows rejects x64.");
Check(TerracottaArchitecture.IsCompatible(TerracottaArchitecture.Machine.X86, Architecture.X64), "x86 runs on x64 Windows.");
Check(TerracottaArchitecture.ChooseEmbedded(new[] { "XCL2.App.Resources.Terracotta.terracotta-0.4.2-windows-x86_64.exe" }, Architecture.X86) == null, "An x64 file is never mistaken for x86.");
Check(TerracottaArchitecture.ChooseEmbedded(new[] { "XCL2.App.Resources.Terracotta.terracotta-0.4.2-windows-x86.exe", "XCL2.App.Resources.Terracotta.terracotta-0.4.2-windows-x86_64.exe" }, Architecture.X64)!.EndsWith("windows-x86.exe"), "x86 has priority when available.");
var badFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
try
{
    File.WriteAllText(badFile, "not an executable");
    try { TerracottaArchitecture.ReadMachine(badFile); throw new Exception("Invalid PE accepted."); }
    catch (BadImageFormatException) { checks++; }
}
finally { File.Delete(badFile); }
var timeout = ErrorReport.Create("下载失败", "System.TimeoutException: 请求超时", "失败");
var forbidden = ErrorReport.Create("下载失败", "HTTP 403 Forbidden", "失败");
var unauthorized = ErrorReport.Create("下载失败", "HTTP 401 Unauthorized", "失败");
Check(new[] { timeout.Code, forbidden.Code, unauthorized.Code }.Distinct().Count() == 3, "Different causes with one summary have different codes.");
var other = ErrorReport.Create("下载失败", "System.InvalidOperationException: unexpected frame", "失败");
var another = ErrorReport.Create("下载失败", "System.InvalidOperationException: missing endpoint", "失败");
Check(other.Code != another.Code, "Unclassified causes retain separate fingerprints.");
Check(timeout.Code == ErrorReport.Create("下载失败", "System.TimeoutException: 请求超时", "失败").Code, "The same cause is stable.");
Check(timeout.IncidentId != ErrorReport.Create("下载失败", "System.TimeoutException: 请求超时", "失败").IncidentId, "Each occurrence has its own report id.");
Check(ErrorCatalog.All.Select(x => x.Code).Distinct().Count() == ErrorCatalog.All.Count, "Catalog identifiers are unique.");
Check(ExperimentalFeatureCatalog.All.Count >= 17, "At least fifteen additions are available.");
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
foreach (var encoding in new[] { System.Text.Encoding.UTF8, System.Text.Encoding.Unicode, System.Text.Encoding.GetEncoding(936) })
{
    var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("启动成功：世界已载入")).ToArray();
    using var stream = new MemoryStream(bytes);
    using var reader = new StreamReader(stream, LogTextEncoding.Detect(stream), true);
    Check(reader.ReadToEnd() == "启动成功：世界已载入", "Chinese log text survives UTF-8, UTF-16 and GBK input.");
}
Console.WriteLine($"PASS: {checks} launcher experience checks");
