namespace XCL2.App.Services;

internal enum InputModeNotificationAction { Open, Keyboard, Touch }

/// <summary>每次启动独立的通知凭据；旧启动、旧进程以及已完成的请求不能再做选择。</summary>
internal sealed class InputModeNotificationRequest
{
    private readonly string _token = Guid.NewGuid().ToString("N");
    private bool _closed;

    internal string Tag => _token[..16];
    internal bool IsPending => !_closed;

    internal string Argument(InputModeNotificationAction action) => $"xcl2-input:{_token}:{action}";

    internal bool TryGetAction(string? argument, out InputModeNotificationAction action)
    {
        action = default;
        if (_closed) return false;
        foreach (var candidate in Enum.GetValues<InputModeNotificationAction>())
        {
            if (!string.Equals(argument, Argument(candidate), StringComparison.Ordinal)) continue;
            action = candidate;
            return true;
        }
        return false;
    }

    internal void Close() => _closed = true;
}
