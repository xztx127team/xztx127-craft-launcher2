using XCL2.App.Services;

void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
KeyValuePair<string, double> Engine(int pid, int engine, double value, int physical = 0)
    => new($"pid_{pid}_luid_0x00000001_0x00000002_phys_{physical}_eng_{engine}_engtype_Compute", value);
var id = PerformanceHardwareMath.Luid(1, 2);
Check(id == "0000000100000002", "LUID high/low ordering");
Check(PerformanceHardwareMath.AdapterId("pid_9_luid_0x1_0x2_phys_0_eng_1_engtype_3D") == id, "LUID zero padding");
Check(PerformanceHardwareMath.AdapterId("unknown") == null, "Unknown counter must not be assigned an adapter");
Check(PerformanceHardwareMath.BusiestEngine(new[] { Engine(1, 0, 35), Engine(2, 0, 40), Engine(1, 1, 60) }, id) == 75,
    "Aggregate processes per engine before selecting busiest engine");
Check(PerformanceHardwareMath.BusiestEngine(new[] { Engine(1, 0, 35), Engine(2, 0, 40, 1) }, id) == 40,
    "Independent physical engines must not be summed");
Check(PerformanceHardwareMath.BusiestEngine(new[] { Engine(1, 0, 0) }, id) == 0, "Actual idle is zero");
Check(PerformanceHardwareMath.BusiestEngine(new[] { Engine(1, 0, double.NaN), Engine(2, 0, -1) }, id) == null, "Invalid data must remain unavailable");
Check(PerformanceHardwareMath.BusiestEngine(new[] { Engine(1, 0, 15) }, "another") == null, "Missing adapter is not zero");
Check(PerformanceHardwareMath.BusiestEngine(new[] { Engine(1, 0, 90), Engine(2, 0, 30) }, id) == 100, "Clamp engine to 100%");
Check(PerformanceHardwareMath.AdapterMemory(new[] { new KeyValuePair<string, double>("luid_0x1_0x2_phys_0", 4096) }, id) == 4096, "Adapter memory mapping");
Check(PerformanceHardwareMath.AdapterMemory(Array.Empty<KeyValuePair<string, double>>(), id) == null, "Missing memory must remain unavailable");
if (!OperatingSystem.IsWindows())
{
    using var hardware = new PerformanceHardwareService();
    var result = hardware.Sample();
    Check(result.Devices.Count == 0 && result.GpuUnavailableReason != null && result.DiskReadBytesPerSecond == null, "Unsupported platform should be honest, not fabricated 0%");
}
var endpoint = TerracottaProtocol.ReadEndpoint("{\"port\":35678}");
Check(endpoint.AbsoluteUri == "http://127.0.0.1:35678/", "Handshake must be loopback only");
foreach (var json in new[] { "[]", "null", "{", "{}", "{\"port\":0}", "{\"port\":65536}", "{\"port\":\"https://example.com\"}" })
{
    try { TerracottaProtocol.ReadEndpoint(json); throw new Exception("Accepted invalid handshake " + json); }
    catch (InvalidDataException) { }
}
Check(TerracottaProtocol.IsSameOrigin(endpoint, "http://127.0.0.1:35678/state"), "Embedded local navigation");
foreach (var url in new[] { "https://127.0.0.1:35678/", "http://localhost:35678/", "http://127.0.0.1:35679/", "https://example.com/", "http://example.com@127.0.0.1:35678/" })
    Check(!TerracottaProtocol.IsSameOrigin(endpoint, url), "Embedded navigation escaped loopback origin " + url);
var start = TerracottaProtocol.CreateEmbeddedStart("/tmp/陶瓦 app.exe", "/tmp/a b/port.json");
Check(!start.UseShellExecute && start.CreateNoWindow && start.ArgumentList.SequenceEqual(new[] { "--hmcl", "/tmp/a b/port.json" }), "Parameter protocol must preserve path as one argument and avoid shell");
await NativeProtocolCases.RunAsync();
Console.WriteLine("Hardware math, unavailable readings, Terracotta native API/state/command/handshake regression checks passed.");
