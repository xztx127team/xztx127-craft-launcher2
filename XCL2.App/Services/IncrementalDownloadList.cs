namespace XCL2.App.Services;

public static class DownloadListBatchPolicy
{
    public static int Normalize(int value) => Math.Clamp(value, 10, 100);
    public static int Initial(int value) => Math.Min(20, Normalize(value));
}

public sealed record IncrementalSourcePage<T>(IReadOnlyList<T> Items, int? Total, bool Failed = false);

/// <summary>A bounded, user-driven search window. Each source owns its offset;
/// changing the batch size never changes the already-consumed API positions.</summary>
public sealed class IncrementalDownloadList<T>
{
    private sealed class SourceCursor
    {
        public string Name = "";
        public int Offset;
        public bool Exhausted;
        public bool Failed;
        public SourceCursor Copy() => (SourceCursor)MemberwiseClone();
    }

    private List<SourceCursor> _sources = new();
    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _generation;
    public bool IsLoading { get; private set; }
    public bool NeedsReset { get; private set; } = true;
    public bool HasMore => !NeedsReset && _sources.Any(source => !source.Exhausted && !source.Failed);
    public bool HasFailedSources => _sources.Any(source => source.Failed);
    public int Generation => _generation;

    public void Reset(IEnumerable<string> sources)
    {
        Invalidate();
        _sources = sources.Distinct(StringComparer.Ordinal).Select(name => new SourceCursor { Name = name }).ToList();
        _seen.Clear();
        NeedsReset = false;
    }

    public void Invalidate()
    {
        ++_generation;
        NeedsReset = true;
        IsLoading = false;
    }

    public void RetryFailedSources()
    {
        foreach (var source in _sources) source.Failed = false;
    }

    public async Task<IReadOnlyList<T>> LoadAsync(int limit,
        Func<string, int, int, CancellationToken, Task<IncrementalSourcePage<T>>> fetch,
        Func<T, string> key, CancellationToken cancellationToken)
    {
        if (IsLoading || !HasMore) return Array.Empty<T>();
        limit = Math.Clamp(limit, 1, 100);
        var generation = _generation;
        IsLoading = true;
        // Work on a snapshot. A canceled request may never advance a live cursor.
        var cursors = _sources.Select(source => source.Copy()).ToList();
        var seen = new HashSet<string>(_seen, StringComparer.Ordinal);
        var output = new List<T>(limit);
        var consumed = 0;
        try
        {
            // One extra round fills the other source's share if it is exhausted or
            // unavailable. This is bounded; it never walks all remote search pages.
            for (var round = 0; round < 2 && consumed < limit; ++round)
            {
                var active = cursors.Where(source => !source.Exhausted && !source.Failed).ToArray();
                if (active.Length == 0) break;
                var remaining = limit - consumed;
                var requests = active.Select((source, index) => new
                {
                    Source = source,
                    Limit = remaining / active.Length + (index < remaining % active.Length ? 1 : 0)
                }).Where(request => request.Limit > 0).ToArray();
                var responses = await Task.WhenAll(requests.Select(request =>
                    fetch(request.Source.Name, request.Source.Offset, request.Limit, cancellationToken)));
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _generation) return Array.Empty<T>();
                for (var index = 0; index < requests.Length; ++index)
                {
                    var cursor = requests[index].Source;
                    var response = responses[index];
                    if (response.Failed) { cursor.Failed = true; continue; }
                    // Offsets consume raw API records, never the deduplicated UI count.
                    // Refuse an over-sized response instead of silently skipping records.
                    if (response.Items.Count > requests[index].Limit)
                        throw new InvalidOperationException("下载来源返回的数量超过请求上限。");
                    cursor.Offset += response.Items.Count;
                    consumed += response.Items.Count;
                    cursor.Exhausted = response.Items.Count == 0 ||
                        (response.Total is int total ? cursor.Offset >= total : response.Items.Count < requests[index].Limit);
                    foreach (var item in response.Items)
                        if (seen.Add(key(item))) output.Add(item);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation) return Array.Empty<T>();
            _sources = cursors;
            _seen = seen;
            return output;
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }
}
