using System.Diagnostics;
using System.Reflection;
using XCL2.App.Services;

static ProcessStartInfo Self(params string[] values)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var value in values) start.ArgumentList.Add(value);
    return start;
}

if (args is ["--hold-pipe", var ready, var release])
{
    await File.WriteAllTextAsync(ready, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    var timer = Stopwatch.StartNew();
    while (!File.Exists(release) && timer.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20);
    return;
}
if (args is ["--spawn-pipe-child", var readyFile, var releaseFile])
{
    using var child = Process.Start(Self("--hold-pipe", readyFile, releaseFile))!;
    var timer = Stopwatch.StartNew();
    while (!File.Exists(readyFile) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    if (!File.Exists(readyFile)) throw new Exception("Pipe-holding descendant failed to start.");
    return;
}
if (args is ["--log-flood"])
{
    for (var i = 0; i < 10000; i++)
    {
        Console.Out.WriteLine("stdout-" + i);
        Console.Error.WriteLine("stderr-" + i);
    }
    return;
}

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}

var mixed = new InstallerOutputCapture();
await mixed.DrainAsync(new ChunkReader("first\r\nsecond\rthird\nfourth", 1), null, default);
Check(mixed.Snapshot().SequenceEqual(new[] { "first", "second", "third", "fourth" }),
    "CRLF split between reads does not create phantom blank lines");

var blank = new InstallerOutputCapture();
await blank.DrainAsync(new StringReader("a\n\nb\n"), null, default);
Check(blank.Snapshot().SequenceEqual(new[] { "a", "", "b" }), "preserve real blank lines, no synthetic EOF line");

var huge = new InstallerOutputCapture();
var hugeReader = new RepeatingReader(16 * 1024 * 1024);
await huge.DrainAsync(hugeReader, null, default);
Check(hugeReader.MaximumRequested <= 4096, "read a huge unterminated line using bounded chunks");
Check(huge.Snapshot().Single().Length < 4200 && huge.Truncated, "oversized line cannot grow retained memory");

var unicode = new InstallerOutputCapture();
await unicode.DrainAsync(new StringReader(new string('x', 4095) + "😀" + "rest"), null, default);
Check(!unicode.Snapshot()[0].Contains('\ud83d'), "truncation does not retain half a surrogate pair");

var concurrent = new InstallerOutputCapture();
var progress = new CounterProgress();
var lines = string.Join('\n', Enumerable.Range(0, 10000).Select(i => $"line-{i}-" + new string('x', 200)));
await Task.WhenAll(Task.Run(() => concurrent.DrainAsync(new StringReader(lines), progress, default)),
    Task.Run(() => concurrent.DrainAsync(new StringReader(lines), progress, default)));
var snapshot = concurrent.Snapshot();
Check(snapshot.Length <= InstallerOutputCapture.MaximumStoredLines, "concurrent streams have a combined line bound");
Check(snapshot.Sum(x => x.Length) <= InstallerOutputCapture.MaximumStoredChars, "combined character bound holds");
Check(snapshot.Any(x => x.StartsWith("line-9999-")), "latest failure context survives log flood");
Check(concurrent.Truncated && progress.Count < 20000, "notify truncation and throttle UI reports");

using var stop = new CancellationTokenSource();
stop.Cancel();
try
{
    await new InstallerOutputCapture().DrainAsync(new RepeatingReader(100000), null, stop.Token);
    throw new Exception("cancelled read continued");
}
catch (OperationCanceledException) { checks++; }

// Exercise the real process/pipe boundary in addition to in-memory reader cases.
var processRoot = Path.Combine(Path.GetTempPath(), "xcl-installer-output-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(processRoot);
try
{
    var floodStart = Self("--log-flood");
    floodStart.RedirectStandardOutput = true;
    floodStart.RedirectStandardError = true;
    using (var flood = Process.Start(floodStart)!)
    {
        var captured = await InstallerOutputCapture.WaitForExitAsync(flood, TimeSpan.FromSeconds(10),
            "output regression", processRoot, null, default).WaitAsync(TimeSpan.FromSeconds(15));
        Check(flood.ExitCode == 0 && captured.Truncated, "both real pipes drain without blocking a verbose installer");
        Check(captured.Snapshot().Any(x => x.EndsWith("9999")), "real process retains its final output");
    }

    foreach (var cancelAfterParentExit in new[] { true, false })
    {
        var readyPath = Path.Combine(processRoot, "descendant-" + cancelAfterParentExit + ".pid");
        var releasePath = Path.Combine(processRoot, "release-" + cancelAfterParentExit);
        var parentStart = Self("--spawn-pipe-child", readyPath, releasePath);
        parentStart.RedirectStandardOutput = true;
        parentStart.RedirectStandardError = true;
        using var parent = Process.Start(parentStart)!;
        using var cancelDrains = new CancellationTokenSource();
        var wait = InstallerOutputCapture.WaitForExitAsync(parent, TimeSpan.FromSeconds(10),
            "pipe cancellation regression", processRoot, null, cancelDrains.Token);
        try
        {
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Check(parent.ExitCode == 0 && File.Exists(readyPath), "parent exits while descendant retains the redirected pipe");
            // Give the capture's process waiter time to enter the bounded pipe-drain phase.
            await Task.Delay(150);
            if (cancelAfterParentExit) cancelDrains.Cancel();
            try
            {
                await wait.WaitAsync(TimeSpan.FromSeconds(5));
                throw new Exception("a descendant retaining an installer pipe was accepted as success");
            }
            catch (OperationCanceledException ex) when (cancelAfterParentExit)
            {
                Check(InstallerProcessLifetime.RequiresPreservedDirectory(ex),
                    "pipe-drain cancellation retains the working-directory preservation marker");
            }
            catch (InstallerProcessCleanupDeferredException ex) when (!cancelAfterParentExit)
            {
                Check(ex.ParentExited && InstallerProcessLifetime.RequiresPreservedDirectory(ex)
                    && ex.WorkingDirectory == Path.GetFullPath(processRoot),
                    "pipe-drain timeout preserves the staging directory instead of publishing success");
            }
        }
        finally
        {
            await File.WriteAllTextAsync(releasePath, "release");
            if (File.Exists(readyPath) && int.TryParse(await File.ReadAllTextAsync(readyPath), out var childPid))
            {
                try
                {
                    using var child = Process.GetProcessById(childPid);
                    if (!child.HasExited)
                    {
                        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                        catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    }
                }
                catch (ArgumentException) { /* The released child has already exited. */ }
            }
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        }
    }
}
finally { Directory.Delete(processRoot, recursive: true); }
Console.WriteLine($"PASS {checks} installer output checks");

sealed class CounterProgress : IProgress<string>
{
    internal int Count;
    public void Report(string value) => Interlocked.Increment(ref Count);
}

sealed class RepeatingReader(int remaining) : TextReader
{
    internal int MaximumRequested;
    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MaximumRequested = Math.Max(MaximumRequested, buffer.Length);
        var count = Math.Min(remaining, buffer.Length);
        buffer.Span[..count].Fill('x');
        remaining -= count;
        return ValueTask.FromResult(count);
    }
}

sealed class ChunkReader(string text, int chunk) : TextReader
{
    private int _position;
    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var count = Math.Min(Math.Min(buffer.Length, chunk), text.Length - _position);
        text.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return ValueTask.FromResult(count);
    }
}
