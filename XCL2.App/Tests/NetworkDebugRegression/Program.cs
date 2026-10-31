using System.Diagnostics;
using System.Net;
using XCL2.App.Services;

var entries = new List<NetworkDebugEntry>();
using var observer = new NetworkDebugService { Enabled = true };
observer.Entry += entries.Add;
using var listener = new DiagnosticListener("HttpHandlerDiagnosticListener");
using var request = new HttpRequestMessage(HttpMethod.Get, "https://user:secret@example.com:8443/private/token?key=secret#secret");
listener.Write("System.Net.Http.HttpRequestOut.Start", new { Request = request });
using var response = new HttpResponseMessage(HttpStatusCode.OK);
listener.Write("System.Net.Http.HttpRequestOut.Stop", new { Request = request, Response = response, RequestTaskStatus = TaskStatus.RanToCompletion });
Check(entries.Count == 2 && entries[1].Success == true && entries[0].Id == entries[1].Id, "request/result pairing");
Check(entries.All(e => e.Host == "https://example.com:8443" && !e.Summary.Contains("secret") && !e.Summary.Contains("private")), "credential/query/path redaction");
listener.Write("System.Net.Http.HttpRequestOut.Start", new { Request = request });
listener.Write("System.Net.Http.HttpRequestOut.Stop", new { Request = request, Response = (HttpResponseMessage?)null, RequestTaskStatus = TaskStatus.Canceled });
Check(entries[^1].State == "已取消", "cancel reported separately");
var before = entries.Count;
observer.Enabled = false;
listener.Write("System.Net.Http.HttpRequestOut.Start", new { Request = request });
Check(entries.Count == before, "disabled setting suppresses diagnostics");
Console.WriteLine("Network debug checks passed.");
static void Check(bool condition, string title) { if (!condition) throw new Exception(title); }

namespace XCL2.App.Services
{
    public static class LauncherLogService { public static void AppendLine(string line) { } }
}
