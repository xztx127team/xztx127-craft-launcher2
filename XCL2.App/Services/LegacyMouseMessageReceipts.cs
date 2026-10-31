namespace XCL2.App.Services;

/// <summary>
/// WH_MOUSE_LL 已确认的本启动器输入。只用于认证随后实际到达覆盖层的同一条消息，
/// 不额外投递输入，也不按“最近有注入”开放一个任意鼠标消息可通过的时间窗。
/// 每个游戏/覆盖层会话各持有一个实例；切换目标时整个实例作废。
/// </summary>
internal sealed class LegacyMouseMessageReceipts
{
    internal const int Capacity = 64;
    internal const long LifetimeMilliseconds = 500;
    private readonly object _gate = new();
    private readonly List<Receipt> _pending = new(Capacity);
    private readonly record struct Receipt(int Message, int X, int Y, uint MessageTime, long RecordedAt, ulong Signature);

    public void Record(int message, int x, int y, uint messageTime, long now,
        ulong signature = LegacyTouchMousePolicy.InjectedSignature)
    {
        lock (_gate)
        {
            Prune(now);
            // Windows timestamps have millisecond precision. A button synchronization and a look
            // sample may share a point/time; keep the latest packet's type, as mouse coalescing does.
            _pending.RemoveAll(r => r.Message == message && r.X == x && r.Y == y && r.MessageTime == messageTime);
            if (_pending.Count == Capacity) _pending.RemoveAt(0);
            _pending.Add(new(message, x, y, messageTime, now, signature));
        }
    }

    public bool TryConsume(int message, int x, int y, uint messageTime, long now)
        => TryConsume(message, x, y, messageTime, now, out _);

    public bool TryConsume(int message, int x, int y, uint messageTime, long now, out ulong signature)
    {
        signature = 0;
        lock (_gate)
        {
            Prune(now);
            var index = _pending.FindLastIndex(r => r.Message == message && r.X == x && r.Y == y &&
                r.MessageTime == messageTime);
            if (index < 0) return false;
            signature = _pending[index].Signature;
            // 原生 WM_MOUSEMOVE 合并后只保留最新位置和消息时间；旧移动不能稍后重播。
            if (message == 0x0200)
            {
                for (var i = index; i >= 0; i--)
                    if (_pending[i].Message == 0x0200) _pending.RemoveAt(i);
            }
            else _pending.RemoveAt(index);
            return true;
        }
    }

    public void Clear() { lock (_gate) _pending.Clear(); }

    private void Prune(long now)
        => _pending.RemoveAll(r => now < r.RecordedAt || now - r.RecordedAt > LifetimeMilliseconds);
}
