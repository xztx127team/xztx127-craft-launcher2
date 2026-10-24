using XCL2.App.Services;

internal static class LegacyInputDeliveryCases
{
    public static void Run(Action<bool, string> check)
    {
        var delivery = new LegacyTouchInputDelivery();
        var first = LegacyTouchMousePolicy.LookSignature(101);
        var second = LegacyTouchMousePolicy.LookSignature(102);
        const ulong touch = 0xff515780;
        LegacyMouseMessageKind Move(ulong signature, long at, uint time = 50, int x = 510,
            uint device = 0, uint origin = 0)
            => delivery.Classify(0x0200, x, 300, time, signature, device, origin, at, out _);

        check(Move(first, 999) == LegacyMouseMessageKind.Rejected, "an unissued camera marker is never trusted");
        delivery.BeginLook(first, 1000);
        // No ObserveHook call: installation failure, a silent Windows hook timeout, or delayed hook work.
        check(Move(first, 1001) == LegacyMouseMessageKind.Look, "issued look works with no low-level hook receipt");
        check(Move(first, 1002) == LegacyMouseMessageKind.Rejected, "one signed packet produces at most one view movement");
        check(Move(first, 1003, time: 777, x: 800) == LegacyMouseMessageKind.Rejected,
            "a replay with rewritten coordinates and timestamp is still rejected");

        delivery.BeginLook(second, 1100);
        delivery.ObserveHook(0x0200, 520, 300, 60, second, 1100);
        check(Move(0, 1101, 60, 521) == LegacyMouseMessageKind.Rejected, "a neighboring point cannot borrow a receipt");
        check(Move(0, 1101, 61, 520) == LegacyMouseMessageKind.Rejected, "a different native time cannot borrow a receipt");
        check(Move(touch, 1101, 60, 520) == LegacyMouseMessageKind.Rejected, "explicit touch promotion stays blocked");
        check(Move(0, 1102, 60, 520, 4, 1) == LegacyMouseMessageKind.Look,
            "an exact hook receipt recovers the camera type when Windows strips its signature");
        check(Move(second, 1103, 60, 520) == LegacyMouseMessageKind.Rejected, "fallback then signed echo cannot duplicate look");
        check(Move(0, 1103, 60, 520) == LegacyMouseMessageKind.Rejected, "an unsigned receipt cannot be replayed");

        delivery.BeginLook(first, 1200);
        delivery.ObserveHook(0x0200, 510, 300, 70, LegacyTouchMousePolicy.InjectedSignature, 1200);
        delivery.ObserveHook(0x0200, 510, 300, 70, first, 1200);
        check(Move(0, 1201, 70) == LegacyMouseMessageKind.Look,
            "a same-millisecond button echo cannot replace the newer camera packet's identity");
        delivery.BeginLook(second, 1300);
        delivery.ObserveHook(0x0200, 510, 300, 80, second, 1300);
        delivery.ObserveHook(0x0200, 510, 300, 80, LegacyTouchMousePolicy.InjectedSignature, 1300);
        check(Move(0, 1301, 80) == LegacyMouseMessageKind.Injected,
            "a newer button synchronization cannot borrow an older same-millisecond camera identity");

        delivery.BeginLook(first, 2000);
        delivery.CancelLook(first);
        check(Move(first, 2001) == LegacyMouseMessageKind.Rejected, "failed SendInput revokes the pending camera packet");
        delivery.BeginLook(first, 2100);
        delivery.ObserveHook(0x0200, 510, 300, 50, first, 2100);
        delivery.Clear();
        check(Move(first, 2101) == LegacyMouseMessageKind.Rejected && Move(0, 2101) == LegacyMouseMessageKind.Rejected,
            "focus, mode, or session reset revokes both native markers and fallback receipts");
        delivery.BeginLook(first, 3000);
        check(Move(first, 3501) == LegacyMouseMessageKind.Rejected, "an input older than 500 ms is not replayed");
        delivery.BeginLook(first, 4000);
        check(Move(first, 3999) == LegacyMouseMessageKind.Rejected, "a clock rollback cannot prolong a packet");

        delivery.BeginLook(first, 5000);
        check(delivery.Classify(0x0201, 510, 300, 50, first, 0, 0, 5001, out _) == LegacyMouseMessageKind.Rejected,
            "a camera token cannot authorize a mouse button");
        check(Move(first, 5002) == LegacyMouseMessageKind.Look, "a rejected button does not consume the real movement");
        delivery.BeginLook(first, 5100);
        delivery.BeginLook(second, 5101);
        check(Move(second, 5102) == LegacyMouseMessageKind.Look && Move(first, 5103) == LegacyMouseMessageKind.Look,
            "distinct issued packets are independently delivered even if arrival order differs");

        delivery.Clear();
        for (var i = 0; i <= LegacyTouchInputDelivery.Capacity; i++)
            delivery.BeginLook(LegacyTouchMousePolicy.LookSignature(i), 6000);
        check(Move(LegacyTouchMousePolicy.LookSignature(0), 6001) == LegacyMouseMessageKind.Rejected,
            "issued-packet memory is bounded when a target stops consuming input");
        check(Move(LegacyTouchMousePolicy.LookSignature(LegacyTouchInputDelivery.Capacity), 6001) == LegacyMouseMessageKind.Look,
            "a bounded queue retains the latest camera packet");

        delivery.Clear();
        delivery.BeginLook(first, 7000);
        var replacement = new LegacyTouchInputDelivery();
        check(replacement.Classify(0x0200, 510, 300, 50, first, 0, 0, 7001, out _) == LegacyMouseMessageKind.Rejected,
            "a new game or replacement window cannot consume an old session's packet");
        delivery.ObserveHook(0x0200, -200, 300, uint.MaxValue, first, 7000);
        check(Move(0, 7001, uint.MaxValue, -200) == LegacyMouseMessageKind.Look,
            "negative screen coordinates and native timestamp wrap retain exact identity");

        check(!LegacyTouchMousePolicy.CanForwardGameplayMove(LegacyMouseMessageKind.Injected, false),
            "mouse synchronization from a button or menu injection cannot rotate the view");
        check(!LegacyTouchMousePolicy.CanForwardGameplayMove(LegacyMouseMessageKind.Physical, true),
            "mouse-looking touch echoes cannot rotate the view while a control owns the pointer");
        check(LegacyTouchMousePolicy.CanForwardGameplayMove(LegacyMouseMessageKind.Physical, false),
            "ordinary hardware mouse movement resumes after the touch gesture");
        check(LegacyTouchMousePolicy.CanForwardGameplayMove(LegacyMouseMessageKind.Look, true),
            "second-finger camera movement works while a movement control remains held");
        check(!LegacyTouchMousePolicy.CanForwardGameplayMove(LegacyMouseMessageKind.Rejected, false),
            "unowned movement is never a camera input");

        var falseMoves = 0;
        for (var frame = 0; frame < 600; frame++)
        {
            var kind = Move(LegacyTouchMousePolicy.InjectedSignature, 8000 + frame);
            if (LegacyTouchMousePolicy.CanForwardGameplayMove(kind, true)) falseMoves++;
        }
        check(falseMoves == 0, "600 stationary button synchronization frames cause zero camera movement");
    }
}
