namespace XCL2.App.Services;

/// <summary>Samples Bedrock only while downloading; Java changes are supplied immediately.</summary>
internal sealed class DownloadGameActivityMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<bool> _probeBedrock;
    private readonly Action<bool> _report;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _loop;
    private Task<bool?>? _probe;
    private readonly TimeSpan _probeTimeout;
    private bool _javaRunning, _bedrockRunning, _disposed;

    internal DownloadGameActivityMonitor(Func<bool> probeBedrock, Action<bool> report, TimeSpan? interval = null, TimeSpan? probeTimeout = null)
    {
        _probeBedrock = probeBedrock; _report = report;
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(2);
    }

    internal void Update(bool enabled, bool downloading, bool javaRunning)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _javaRunning = javaRunning;
            if (!enabled || !downloading)
            {
                _loop?.Cancel(); _loop = null;
                _bedrockRunning = false;
            }
            else if (_loop == null)
            {
                var lifetime = _loop = new CancellationTokenSource();
                _ = Task.Run(() => RunAsync(lifetime));
            }
            _report(_javaRunning || _bedrockRunning);
        }
    }

    private async Task RunAsync(CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Task<bool?> probe;
                lock (_sync)
                {
                    if (_disposed || !ReferenceEquals(_loop, lifetime)) return;
                    // Process APIs are synchronous and cannot be canceled. Reuse an unfinished
                    // probe across stop/start cycles instead of accumulating blocked workers.
                    if (_probe == null || _probe.IsCompleted)
                        _probe = Task.Run<bool?>(() => { try { return _probeBedrock(); } catch { return null; } });
                    probe = _probe;
                }
                bool? running = null;
                try { running = await probe.WaitAsync(_probeTimeout, lifetime.Token).ConfigureAwait(false); }
                catch (TimeoutException) { /* Keep the last known state; the next tick reuses this probe. */ }
                lock (_sync)
                {
                    if (_disposed || !ReferenceEquals(_loop, lifetime) || lifetime.IsCancellationRequested) return;
                    if (running.HasValue) _bedrockRunning = running.Value;
                    _report(_javaRunning || _bedrockRunning);
                }
                await Task.Delay(_interval, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { lifetime.Dispose(); }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _loop?.Cancel(); _loop = null;
            _report(false);
        }
    }
}
