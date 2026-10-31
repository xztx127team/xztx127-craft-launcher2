using XCL2.App.Models;
using XCL2.App.Services;

int assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
const string errors = "[12:00:00] [Render thread/ERROR]: mod alpha failed\n[12:00:01] [Render thread/ERROR]: mod beta failed\n[12:00:02] [Render thread/ERROR]: mod gamma failed\n";
Check(GameLogDiagnostics.HasTerminalErrorBurst(errors), "Terminal ERROR burst missed");
Check(!GameLogDiagnostics.HasTerminalErrorBurst("[Render thread/ERROR]: one mod problem\n"), "Single mod error became crash evidence");
Check(!GameLogDiagnostics.HasTerminalErrorBurst(errors + "[Render thread/INFO]: Stopping!\n"), "Normal close was ignored");
Check(!GameLogDiagnostics.HasTerminalErrorBurst(errors + string.Concat(Enumerable.Repeat("[Render thread/INFO]: game continues\n", 15))), "Old errors became terminal evidence");
Check(!GameLogDiagnostics.HasTerminalErrorBurst("no error was detected\nno error was detected\nno error was detected\n"), "Prose was treated as error level");
var issues = GameLogDiagnostics.Scan(errors + "[12:10:01] [Render thread/ERROR]: mod alpha failed\n[Render thread/WARN]: texture missing\n");
Check(issues.Count == 4 && issues[0].Occurrences == 2, "Timestamped duplicates were not merged");
Check(issues.All(i => !string.IsNullOrWhiteSpace(i.Repair)), "An issue has no repair suggestion");
Check(!GameLogDiagnostics.Format(issues, running: true).Contains("错误线索"), "Running game was reported as crashed");
Check(GameLogDiagnostics.Format(issues, running: false).Contains("错误线索"), "Exited issue did not get contextual severity");
var root = Path.Combine(Path.GetTempPath(), "xcl-log-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "logs"));
try
{
    var info = new GameProcessInfo { GameDir = root, Output = errors };
    var assessment = GameExitClassifier.Assess(info, 0);
    Check(assessment.Kind == GameExitKind.Crash && assessment.SuspectedLogFailure, "Zero exit code masked terminal ERROR sequence");
    info.Output = errors + "[Render thread/INFO]: Stopping!\n";
    Check(GameExitClassifier.Assess(info, 0).Kind == GameExitKind.Normal, "Normal zero close was misreported");
    Check(GameExitClassifier.Assess(info, 1).Kind == GameExitKind.Normal, "Normal wrapper close was misreported");
    info.Output = "[Render thread/ERROR]: caught java.lang.OutOfMemoryError\n[Render thread/INFO]: Stopping!\n";
    Check(GameExitClassifier.Assess(info, 0).Kind == GameExitKind.Normal, "Handled error before normal zero close was misreported");
    info.Output = "[Render thread/ERROR]: a single optional mod failed\n";
    Check(GameExitClassifier.Assess(info, 0).Kind == GameExitKind.Normal, "Isolated error caused zero-code crash");
    info.Output = errors; info.UserRequestedClose = true;
    Check(GameExitClassifier.Assess(info, 1).Kind == GameExitKind.UserRequested, "User close was overridden");
    info.UserRequestedClose = false; info.Output = "";
    var latest = Path.Combine(root, "logs", "latest.log"); File.WriteAllText(latest, errors);
    File.SetLastWriteTime(latest, info.StartedAt.AddHours(-1));
    Check(GameExitClassifier.Assess(info, 0).Kind == GameExitKind.Normal, "Stale logs were included");
    File.SetLastWriteTime(latest, DateTime.Now);
    Check(GameExitClassifier.Assess(info, 0).SuspectedLogFailure, "Fresh file error burst was missed");
    // Create a large sparse file: the diagnostic reader must read a bounded tail
    // instead of allocating the whole log before truncation.
    using (var large = new FileStream(latest, FileMode.Create, FileAccess.Write))
    {
        var header = System.Text.Encoding.UTF8.GetBytes("[INFO] first diagnostic line\n"); large.Write(header);
        large.SetLength(64L * 1024 * 1024); large.Position = large.Length;
        large.Write(System.Text.Encoding.UTF8.GetBytes("\n" + errors + "[Render thread/INFO]: Stopping!\n"));
    }
    info.Output = "";
    var bounded = DiagnosticLogReader.Read(latest);
    Check(bounded.Length <= DiagnosticLogReader.MaximumBytes + 200 && bounded.Contains(DiagnosticLogReader.TruncationNotice), "large logs are bounded before decoding");
    Check(bounded.EndsWith("Stopping!\n") && GameExitClassifier.Assess(info, 0).Kind == GameExitKind.Normal, "bounded tail retains the actual shutdown evidence");
    var headAndTail = DiagnosticLogReader.Read(latest, includeHead: true);
    Check(headAndTail.StartsWith("[INFO] first diagnostic line") && headAndTail.EndsWith("Stopping!\n"), "report view retains both header and shutdown tail");
    File.WriteAllText(latest, new string('a', DiagnosticLogReader.MaximumBytes) + "\n[INFO] 中文收尾\n", System.Text.Encoding.Unicode);
    Check(DiagnosticLogReader.Read(latest).EndsWith("[INFO] 中文收尾\n"), "UTF16 BOM and aligned tail remain readable");
    File.WriteAllText(latest, "password=" + new string('S', DiagnosticLogReader.MaximumBytes * 2));
    var singleLine = DiagnosticLogReader.Read(latest, includeHead: true);
    Check(!singleLine.Contains(new string('S', 10)), "Truncated single-line credential fragments are omitted");
    File.WriteAllText(latest, "[INFO] no final newline");
    Check(DiagnosticLogReader.Read(latest) == "[INFO] no final newline", "small files and truncated final-line signal are unchanged");
    // 两个通道的同一条错误不能累加成三条。
    File.WriteAllText(latest, "[Render thread/ERROR]: duplicate\n");
    info.Output = "[Render thread/ERROR]: duplicate\n";
    Check(GameExitClassifier.Assess(info, 0).Kind == GameExitKind.Normal, "Duplicate channels manufactured an error burst");
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Game log diagnostics: {assertions} assertions passed.");
