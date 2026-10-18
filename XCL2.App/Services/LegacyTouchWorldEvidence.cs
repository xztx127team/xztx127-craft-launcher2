namespace XCL2.App.Services;

internal enum LegacyTouchWorldPhase { Unknown, Loading, Joined, Outside }

/// <summary>
/// 每个游戏进程独立的被动世界证据。只消费已有输出，不轮询日志文件；连接请求和
/// 本地服务器的登录日志不是客户端已进入世界的证据。原生抓取仍是模式判断的主证据。
/// </summary>
internal sealed class LegacyTouchWorldEvidence
{
    private readonly object _gate = new();
    private LegacyTouchWorldPhase _phase;
    public LegacyTouchWorldPhase Phase { get { lock (_gate) return _phase; } }

    public void Observe(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        static bool Has(string value, string text) => value.Contains(text, StringComparison.OrdinalIgnoreCase);
        // 现代客户端为 Render thread，旧客户端为 Client thread。其它线程的聊天、
        // advancements、连接日志可能来自集成服务器或 Mod，不能给客户端放行。
        var client = Has(line, "[Render thread/") || Has(line, "[Client thread/");
        lock (_gate)
        {
            if ((client && (Has(line, "Connecting to ") || Has(line, "Joining world"))) ||
                Has(line, "Starting integrated minecraft server version") ||
                Has(line, "Preparing start region for level"))
            {
                _phase = LegacyTouchWorldPhase.Loading;
                return;
            }
            if (Has(line, "Stopping server") || Has(line, "Stopping!") ||
                (client && (Has(line, "Disconnected from server") || Has(line, "Disconnecting from") ||
                    Has(line, "Connection lost") || Has(line, "Failed to connect to") || Has(line, "Can't connect to server"))))
            {
                _phase = LegacyTouchWorldPhase.Outside;
                return;
            }
            // advancement 同步来自世界中的客户端数据包；[CHAT] 仅在当前连接阶段用作
            // 旧版补充证据。还需隐藏且回中的原生光标样本，日志本身从不直接切模式。
            if (_phase != LegacyTouchWorldPhase.Outside && client && ((Has(line, "Loaded ") && Has(line, " advancements")) ||
                (_phase == LegacyTouchWorldPhase.Loading && Has(line, "[CHAT]"))))
                _phase = LegacyTouchWorldPhase.Joined;
        }
    }
}
