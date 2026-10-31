using System.Threading;

namespace XCL2.App.Services;

internal enum AiElevationDecision { Deny, AllowOnce, AllowSession }

internal sealed record AiElevationRequest(string Shell, string Command, string Directory,
    int TimeoutSeconds, string PreviousFailure);

/// <summary>Memory-only, exact chat-session grant. Ordinary command permissions are checked separately.</summary>
internal sealed class AiElevationSession
{
    private string? _permissionFingerprint;
    public bool Allows(string fingerprint) => _permissionFingerprint == fingerprint;
    public void Allow(string fingerprint) => _permissionFingerprint = fingerprint;
    public void Clear() => _permissionFingerprint = null;
}

/// <summary>Async-local UI route; concurrent conversations never share a pending approval or grant.</summary>
internal sealed class AiElevationInteraction
{
    private static readonly AsyncLocal<AiElevationInteraction?> Context = new();
    public static AiElevationInteraction? Current => Context.Value;
    public required AiElevationSession Session { get; init; }
    public required Func<AiElevationRequest, CancellationToken, Task<AiElevationDecision>> AskAsync { get; init; }
    public required Action Activate { get; init; }
    public required Func<bool> IsPresented { get; init; }

    public IDisposable EnterScope()
    {
        var previous = Context.Value;
        Context.Value = this;
        return new Scope(previous);
    }

    private sealed class Scope(AiElevationInteraction? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Context.Value = previous;
        }
    }
}
