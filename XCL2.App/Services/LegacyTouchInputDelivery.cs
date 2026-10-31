namespace XCL2.App.Services;

/// <summary>
/// One legacy game/overlay session. The sender registers camera packets before SendInput;
/// a low-level mouse hook is optional evidence, never the authority for issuing input.
/// </summary>
internal sealed class LegacyTouchInputDelivery
{
    private readonly LegacyMouseMessageReceipts _receipts = new();
    private readonly object _gate = new();
    private readonly List<(ulong Signature, long At, bool Consumed)> _looks = new();
    internal const int Capacity = 128;

    public void BeginLook(ulong signature, long now)
    {
        if (!LegacyTouchMousePolicy.IsLookInjection(signature))
            throw new ArgumentException("A camera packet needs a dedicated look signature.", nameof(signature));
        lock (_gate)
        {
            Prune(now);
            if (_looks.Count == Capacity) _looks.RemoveAt(0);
            _looks.Add((signature, now, false));
        }
    }

    public void CancelLook(ulong signature)
    {
        lock (_gate) _looks.RemoveAll(item => item.Signature == signature);
    }

    public void ObserveHook(int message, int x, int y, uint time, ulong signature, long now)
    {
        if (LegacyTouchMousePolicy.IsInjected(signature))
            _receipts.Record(message, x, y, time, now, signature);
    }

    public LegacyMouseMessageKind Classify(int message, int x, int y, uint time,
        ulong extra, uint device, uint origin, long now, out bool recovered)
    {
        recovered = false;
        // A real touch signature must never borrow a matching synthetic mouse receipt.
        if (LegacyTouchMousePolicy.IsTouch(extra)) return LegacyMouseMessageKind.Rejected;
        var hasReceipt = _receipts.TryConsume(message, x, y, time, now, out var receiptSignature);
        recovered = hasReceipt && !LegacyTouchMousePolicy.IsInjected(extra);
        var signature = recovered ? receiptSignature : extra;
        if (LegacyTouchMousePolicy.IsLookInjection(signature))
        {
            if (message != 0x0200) return LegacyMouseMessageKind.Rejected;
            lock (_gate)
            {
                Prune(now);
                var index = _looks.FindLastIndex(item => item.Signature == signature);
                if (index < 0 || _looks[index].Consumed) return LegacyMouseMessageKind.Rejected;
                var packet = _looks[index];
                // Keep a short-lived tombstone, so a second native/window echo cannot replay it.
                _looks[index] = (packet.Signature, packet.At, true);
                return LegacyMouseMessageKind.Look;
            }
        }
        return LegacyTouchMousePolicy.Classify(signature, device, origin);
    }

    public void Clear()
    {
        _receipts.Clear();
        lock (_gate) _looks.Clear();
    }

    private void Prune(long now)
        => _looks.RemoveAll(item => now < item.At || now - item.At > LegacyMouseMessageReceipts.LifetimeMilliseconds);
}
