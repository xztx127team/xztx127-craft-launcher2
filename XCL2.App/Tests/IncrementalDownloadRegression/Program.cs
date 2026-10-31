using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name) { ++checks; if (!condition) throw new Exception(name); }
var config = new AppConfig();
Check(config.DownloadGamesContinuousLoading && !config.DownloadResourcesContinuousLoading, "Game and resource defaults");
Check(config.DownloadListBatchSize == 20, "Default batch");
Check(DownloadListBatchPolicy.Normalize(-1) == 10 && DownloadListBatchPolicy.Normalize(999) == 100, "Corrupt config bounded");
Check(DownloadListBatchPolicy.Initial(100) == 20 && DownloadListBatchPolicy.Initial(10) == 10, "Initial page respects lower maximum");

var calls = new List<(string Source, int Offset, int Limit)>();
Task<IncrementalSourcePage<string>> Fetch(string source, int offset, int limit, CancellationToken ct)
{
    calls.Add((source, offset, limit));
    return Task.FromResult(new IncrementalSourcePage<string>(Enumerable.Range(offset, limit).Select(i => source + i).ToArray(), 1000));
}
var list = new IncrementalDownloadList<string>();
list.Reset(new[] { "mr", "cf" });
var initial = await list.LoadAsync(20, Fetch, item => item, default);
Check(initial.Count == 20 && calls.SequenceEqual(new[] { ("mr", 0, 10), ("cf", 0, 10) }), "Combined sources share one total batch budget");
Check(list.HasMore && !list.IsLoading, "No eager all-pages loop after initial display");
calls.Clear();
var next = await list.LoadAsync(35, Fetch, item => item, default);
Check(next.Count == 35 && calls.SequenceEqual(new[] { ("mr", 10, 18), ("cf", 10, 17) }), "Changed batch size continues each source cursor");
Check(!initial.Intersect(next).Any(), "No duplicate records across varying batch sizes");

calls.Clear();
list.Reset(new[] { "mr", "cf" });
var oneSource = await list.LoadAsync(20, (source, offset, limit, ct) =>
{
    calls.Add((source, offset, limit));
    return Task.FromResult(new IncrementalSourcePage<string>(source == "cf" ? Array.Empty<string>() :
        Enumerable.Range(offset, limit).Select(i => source + i).ToArray(), source == "cf" ? 0 : 1000));
}, item => item, default);
Check(oneSource.Count == 20 && calls.SequenceEqual(new[] { ("mr", 0, 10), ("cf", 0, 10), ("mr", 10, 10) }),
    "Exhausted or unconfigured source share is filled once by the remaining source");

calls.Clear();
list.Reset(new[] { "mr" });
var duplicatePage = await list.LoadAsync(10, (source, offset, limit, ct) =>
{
    calls.Add((source, offset, limit));
    return Task.FromResult(new IncrementalSourcePage<string>(Enumerable.Repeat("duplicate", limit).ToArray(), 100));
}, item => item, default);
await list.LoadAsync(10, Fetch, item => item, default);
Check(duplicatePage.Count == 1 && calls.Last().Offset == 10, "API cursor advances by raw count, not unique UI count");

list.Reset(new[] { "mr" });
var exhaustedCalls = 0;
Task<IncrementalSourcePage<string>> Empty(string source, int offset, int limit, CancellationToken ct)
{
    ++exhaustedCalls;
    return Task.FromResult(new IncrementalSourcePage<string>(Array.Empty<string>(), 0));
}
await list.LoadAsync(20, Empty, item => item, default);
await list.LoadAsync(20, Empty, item => item, default);
Check(!list.HasMore && exhaustedCalls == 1, "Exhausted list never refetches an empty page");

var pending = new TaskCompletionSource<IncrementalSourcePage<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
list.Reset(new[] { "old" });
var slow = list.LoadAsync(20, (_, _, _, _) => pending.Task, item => item, default);
Check(list.IsLoading, "Busy flag raised before first await");
var overlapCalls = 0;
var overlapping = await list.LoadAsync(20, (source, offset, limit, ct) => { ++overlapCalls; return Fetch(source, offset, limit, ct); }, item => item, default);
Check(overlapCalls == 0 && overlapping.Count == 0, "Fast repeated scrolling cannot create overlapping requests");
list.Reset(new[] { "new" });
pending.SetResult(new IncrementalSourcePage<string>(new[] { "stale" }, 1));
Check((await slow).Count == 0 && !list.IsLoading, "Reset discards old response and does not touch new busy state");
calls.Clear();
var fresh = await list.LoadAsync(20, Fetch, item => item, default);
Check(fresh.Count == 20 && calls.Single() == ("new", 0, 20), "New query starts from zero after stale response");

list.Reset(new[] { "mr" });
using (var cancellation = new CancellationTokenSource())
{
    try
    {
        await list.LoadAsync(20, (_, _, limit, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new IncrementalSourcePage<string>(Enumerable.Range(0, limit).Select(i => "cancel" + i).ToArray(), 100));
        }, item => item, cancellation.Token);
        throw new Exception("Cancellation must propagate");
    }
    catch (OperationCanceledException) { }
}
calls.Clear();
await list.LoadAsync(20, Fetch, item => item, default);
Check(calls.Single().Offset == 0 && !list.IsLoading, "Canceled response cannot consume a page");

calls.Clear();
list.Reset(new[] { "mr", "cf" });
var partial = await list.LoadAsync(20, (source, offset, limit, ct) => source == "cf"
    ? Task.FromResult(new IncrementalSourcePage<string>(Array.Empty<string>(), null, true))
    : Fetch(source, offset, limit, ct), item => item, default);
Check(partial.Count == 20 && list.HasFailedSources, "Partial source failure keeps healthy results and remains retryable");
list.RetryFailedSources();
calls.Clear();
await list.LoadAsync(20, Fetch, item => item, default);
Check(calls.SequenceEqual(new[] { ("mr", 20, 10), ("cf", 0, 10) }), "Retry preserves healthy cursor and retries failed cursor without gaps");

list.Reset(new[] { "mr" });
try
{
    await list.LoadAsync(10, (_, _, _, _) => Task.FromResult(new IncrementalSourcePage<string>(
        Enumerable.Range(0, 50).Select(i => i.ToString()).ToArray(), 100)), item => item, default);
    throw new Exception("Over-sized remote response must be rejected");
}
catch (InvalidOperationException) { }
calls.Clear();
await list.LoadAsync(10, Fetch, item => item, default);
Check(calls.Single().Offset == 0, "Invalid remote response neither overflows memory window nor consumes records");

list.Invalidate();
calls.Clear();
await list.LoadAsync(20, Fetch, item => item, default);
Check(calls.Count == 0 && list.NeedsReset, "Typing/filter invalidation prevents old-query append during debounce");
Console.WriteLine($"Incremental download regression checks passed: {checks}");
