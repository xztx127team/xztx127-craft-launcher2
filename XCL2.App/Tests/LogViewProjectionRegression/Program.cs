using XCL2.App.Services;

var count = 0;
void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); count++; }
var raw = "environment\r\n[INFO] ready\r\n[ERROR] Something failed\r\n   at Example.Run()\r\n[WARN] retry";
var all = LogViewTextService.Project(raw, "", "");
Check(all.Lines.Count == 5 && !all.IsFiltered, "complete view keeps every source line");
var errors = LogViewTextService.Project(raw, "", "ERROR");
Check(errors.Lines.Count == 3, "unknown leading context and error stack remain visible");
Check(errors.Lines.Select(x => x.SourceLine).SequenceEqual(new[] { 1, 3, 4 }), "filtered projection retains original line ordinals");
Check(!errors.Text.Contains("[WARN]") && !errors.Text.Contains("[INFO]"), "selected severity affects view only");
var keyword = LogViewTextService.Project(raw, "FAILED", "");
Check(keyword.Lines.Count == 1 && keyword.Lines[0].SourceLine == 3, "keyword uses case-insensitive matching");
Check(raw.Contains("[INFO]") && raw.Contains("[WARN]"), "projection leaves original source untouched");
var selectedStart = errors.Text.IndexOf("[ERROR]", StringComparison.Ordinal);
var selected = errors.Text[selectedStart..];
var copied = LogViewTextService.CopySelection(errors, selectedStart, selected, true);
Check(copied.StartsWith("3: [ERROR]") && copied.Contains("4:    at Example.Run()"), "copied selection numbers refer to original log, not filtered positions");
Check(LogViewTextService.CopySelection(errors, selectedStart, selected, false) == selected, "line numbering is opt-in");
var trailing = LogViewTextService.CopySelection(all, 0, "environment" + Environment.NewLine, true);
Check(!trailing.Contains("2: "), "terminal newline never creates a spurious numbered selection");
Check(LogViewTextService.Project(null, null, null).Text == "", "empty logs remain empty");

var processA = new object(); var processB = new object();
Check(LogViewTextService.AcceptLiveOutput(processB, processB, processB, 2, 2, true), "active process output accepted");
Check(!LogViewTextService.AcceptLiveOutput(processA, processB, processB, 1, 2, true), "queued output from old process rejected");
Check(!LogViewTextService.AcceptLiveOutput(processA, processA, processA, 1, 2, true), "queued output from previous subscription to same process rejected");
Check(!LogViewTextService.AcceptLiveOutput(processB, processB, processB, 2, 2, false), "unloaded view rejects queued output");
Console.WriteLine($"PASS {count} view-only log projection checks");
