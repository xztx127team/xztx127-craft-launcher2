namespace XCL2.App.Services;

public sealed record ProgressInfo(string Stage, int Done, int Total, string CurrentFile)
{
    public string Unit { get; init; } = "项";
}

// Observe the production downloader's policy integration without loading launcher UI/configuration.
internal static class DownloadTransferPolicy
{
    private static readonly AsyncLocal<TransferProbe?> Current = new();
    internal static TransferProbe Observe()
    {
        var probe = new TransferProbe(Current.Value, value => Current.Value = value);
        Current.Value = probe;
        return probe;
    }
    public static void TrackTarget(string path) { if (Current.Value is { } probe) probe.Target = path; }
    public static void TrackTemporary(string path, bool resumable) => Current.Value?.Temporaries.Add((path, resumable));
    public static void TrackCompleted(string path) => Current.Value?.Completed.Add(path);
    public static void EnsureDiskSpace(string path, long bytes) { }
    public static IDisposable BeginPhase(string kind)
    {
        var probe = Current.Value;
        probe?.Phases.Add(kind);
        if (probe != null) Interlocked.Increment(ref probe.ActivePhases);
        return new Phase(() => { if (probe != null) Interlocked.Decrement(ref probe.ActivePhases); });
    }
    public static async Task ConsumeAsync(int bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var probe = Current.Value;
        if (probe == null) return;
        Interlocked.Add(ref probe.Received, bytes);
        if (probe.Consume != null) await probe.Consume(bytes, ct);
    }
    private sealed class Phase(Action close) : IDisposable { public void Dispose() => close(); }
    internal sealed class TransferProbe(TransferProbe? previous, Action<TransferProbe?> restore) : IDisposable
    {
        public string? Target;
        public long Received;
        public int ActivePhases;
        public Func<int, CancellationToken, Task>? Consume;
        public List<(string Path, bool Resumable)> Temporaries { get; } = new();
        public List<string> Completed { get; } = new();
        public List<string> Phases { get; } = new();
        public void Dispose() => restore(previous);
    }
}
