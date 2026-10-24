using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;

namespace XCL2.App.Services;

public sealed record NetworkDebugEntry(long Id, string Method, string Host, string State, bool? Success, long Milliseconds)
{
    public string Summary => $"#{Id} {Method} {Host} · {State}" + (Milliseconds > 0 ? $" · {Milliseconds} ms" : "");
}

/// <summary>Observes the shared .NET HTTP diagnostics pipeline without changing clients or retry behavior.</summary>
public sealed class NetworkDebugService : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ConcurrentDictionary<HttpRequestMessage, (long Id, long Started)> _pending = new();
    private readonly IDisposable _allListeners;
    private volatile bool _enabled;
    private bool _disposed;
    private long _nextId;
    public event Action<NetworkDebugEntry>? Entry;

    public NetworkDebugService() => _allListeners = DiagnosticListener.AllListeners.Subscribe(this);

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) _pending.Clear(); }
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name != "HttpHandlerDiagnosticListener") return;
        lock (_subscriptions)
        {
            if (_disposed) return;
            _subscriptions.Add(listener.Subscribe(this, name => _enabled &&
                (name == "System.Net.Http.HttpRequestOut" || name == "System.Net.Http.HttpRequestOut.Start" ||
                 name == "System.Net.Http.HttpRequestOut.Stop")));
        }
    }

    public void OnNext(KeyValuePair<string, object?> diagnostic)
    {
        if (!_enabled || diagnostic.Value == null) return;
        try
        {
            var payload = diagnostic.Value;
            var request = payload.GetType().GetProperty("Request")?.GetValue(payload) as HttpRequestMessage;
            if (request == null) return;
            if (diagnostic.Key.EndsWith(".Start", StringComparison.Ordinal))
            {
                // Bound work even for clients whose pending requests never complete.
                if (_pending.Count >= 1024) _pending.Clear();
                var id = Interlocked.Increment(ref _nextId);
                _pending[request] = (id, Stopwatch.GetTimestamp());
                Publish(new(id, request.Method.Method, SafeHost(request.RequestUri), "请求中", null, 0));
            }
            else if (diagnostic.Key.EndsWith(".Stop", StringComparison.Ordinal))
            {
                if (!_pending.TryRemove(request, out var started)) return;
                var response = payload.GetType().GetProperty("Response")?.GetValue(payload) as HttpResponseMessage;
                var status = payload.GetType().GetProperty("RequestTaskStatus")?.GetValue(payload);
                var state = response != null ? $"HTTP {(int)response.StatusCode}" : status is TaskStatus.Canceled ? "已取消" : "请求失败";
                Publish(new(started.Id, request.Method.Method, SafeHost(request.RequestUri), state,
                    response?.IsSuccessStatusCode ?? false, (long)Stopwatch.GetElapsedTime(started.Started).TotalMilliseconds));
            }
        }
        catch { /* Diagnostics must never change the outcome of a network operation. */ }
    }

    public static string SafeHost(Uri? uri) => uri is { IsAbsoluteUri: true }
        ? uri.Scheme + "://" + uri.IdnHost + (uri.IsDefaultPort ? "" : ":" + uri.Port) : "未知主机";

    private void Publish(NetworkDebugEntry entry)
    {
        LauncherLogService.AppendLine("[网络调试] " + entry.Summary);
        Entry?.Invoke(entry);
    }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose()
    {
        Enabled = false;
        _allListeners.Dispose();
        lock (_subscriptions)
        {
            _disposed = true;
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
        _pending.Clear();
    }
}
