using System.Collections.Specialized;
using System.ComponentModel;

namespace XCL2.App.Services;

public enum TaskResultKind { DownloadOrInstall, ModUpdate, Backup, Restore }
public enum TaskResultOutcome { Completed, Failed, Canceled }
public sealed record TaskResultRecord(DateTime OccurredAt, TaskResultKind Kind, TaskResultOutcome Outcome);

/// <summary>Session-only ring buffer; by construction has no raw names, paths, shell args or chat.</summary>
public static class TaskResultHistoryService
{
    public const int MaxRecords = 100;
    private static readonly object Sync = new();
    private static readonly Queue<TaskResultRecord> Records = new();
    private static Func<bool>? _enabled;
    public static event Action? Changed;
    public static IReadOnlyList<TaskResultRecord> Snapshot()
    {
        lock (Sync) return Records.Reverse().ToArray();
    }
    public static void Clear()
    {
        lock (Sync) Records.Clear();
        Changed?.Invoke();
    }
    public static void Record(TaskResultKind kind, TaskResultOutcome outcome)
    {
        if (_enabled?.Invoke() != true || !Enum.IsDefined(kind) || !Enum.IsDefined(outcome)) return;
        lock (Sync)
        {
            Records.Enqueue(new(DateTime.Now, kind, outcome));
            while (Records.Count > MaxRecords) Records.Dequeue();
        }
        Changed?.Invoke();
    }
    public static IDisposable Initialize(Func<bool> enabled)
    {
        _enabled = enabled;
        return new QueueSubscription(DownloadQueueService.Instance);
    }

    private sealed class QueueSubscription : IDisposable
    {
        private readonly DownloadQueueService _queue;
        private readonly HashSet<DownloadQueueItem> _subscribed = new();
        private readonly Dictionary<DownloadQueueItem, DownloadQueueItemStatus> _lastRecorded = new();
        private bool _disposed;
        public QueueSubscription(DownloadQueueService queue)
        {
            _queue = queue;
            foreach (var item in queue.Items) Subscribe(item);
            queue.Items.CollectionChanged += CollectionChanged;
        }
        private void Subscribe(DownloadQueueItem item)
        {
            if (_subscribed.Add(item)) item.PropertyChanged += ItemChanged;
        }
        private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (DownloadQueueItem item in e.NewItems) Subscribe(item);
            if (e.OldItems != null) foreach (DownloadQueueItem item in e.OldItems) Unsubscribe(item);
            if (e.Action == NotifyCollectionChangedAction.Reset)
                foreach (var item in _subscribed.Where(i => !_queue.Items.Contains(i)).ToArray()) Unsubscribe(item);
        }
        private void Unsubscribe(DownloadQueueItem item)
        {
            item.PropertyChanged -= ItemChanged;
            _subscribed.Remove(item); _lastRecorded.Remove(item);
        }
        private void ItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed || sender is not DownloadQueueItem item) return;
            if (item.Status == DownloadQueueItemStatus.Downloading) { _lastRecorded.Remove(item); return; }
            if ((e.PropertyName != nameof(DownloadQueueItem.IsRunning) && e.PropertyName != nameof(DownloadQueueItem.Status)) || item.IsRunning) return;
            if (_lastRecorded.TryGetValue(item, out var prior) && prior == item.Status) return;
            var outcome = item.Status switch
            {
                DownloadQueueItemStatus.Completed => TaskResultOutcome.Completed,
                DownloadQueueItemStatus.Failed => TaskResultOutcome.Failed,
                DownloadQueueItemStatus.Canceled => TaskResultOutcome.Canceled,
                _ => (TaskResultOutcome?)null
            };
            if (outcome == null) return;
            _lastRecorded[item] = item.Status;
            Record(TaskResultKind.DownloadOrInstall, outcome.Value);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Items.CollectionChanged -= CollectionChanged;
            foreach (var item in _subscribed.ToArray()) Unsubscribe(item);
            _enabled = null;
        }
    }
}
