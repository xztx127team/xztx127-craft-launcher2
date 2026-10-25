namespace XCL2.App.Services;

/// <summary>
/// Serializes positive update authorization with cancellation. Once the real close
/// has authorized the helper, later shutdown exceptions must not revoke its files
/// or terminate the helper process.
/// </summary>
internal sealed class LauncherUpdateHandoff
{
    private enum State { Pending, Authorizing, Committed, Canceled }
    private readonly object _gate = new();
    private State _state;

    internal bool IsCommitted
    {
        get { lock (_gate) return _state == State.Committed; }
    }

    internal void Commit(Action authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        lock (_gate)
        {
            if (_state == State.Committed) return;
            if (_state == State.Canceled)
                throw new OperationCanceledException("更新交接已取消，不能再授权替换。");
            if (_state == State.Authorizing)
                throw new InvalidOperationException("更新交接正在授权，不能重复进入。");

            // Keep the authorization write and the committed state in one critical
            // section. A competing cancellation cannot slip between these actions.
            _state = State.Authorizing;
            try
            {
                authorize();
                _state = State.Committed;
            }
            catch
            {
                _state = State.Pending;
                throw;
            }
        }
    }

    internal bool TryCancel()
    {
        lock (_gate)
        {
            // Monitor locks are reentrant: an authorization callback may enter a
            // shutdown handler on the same thread. It must not cancel its own write.
            if (_state is State.Committed or State.Authorizing) return false;
            _state = State.Canceled;
            return true;
        }
    }
}
