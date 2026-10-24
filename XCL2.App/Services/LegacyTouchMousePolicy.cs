namespace XCL2.App.Services;

internal enum LegacyMouseMessageKind { Rejected, Physical, Injected, Look }

/// <summary>
/// 旧窗口只接收有明确来源的鼠标输入。WM_MOUSEMOVE 也会由触摸同步、窗口刷新和
/// SetCursorPos 回中生成；没有触摸签名并不意味着它来自物理鼠标。
/// </summary>
internal static class LegacyTouchMousePolicy
{
    public const ulong InjectedSignature = 0x58434C32;
    private const ulong LookSignaturePrefix = 0x584C0000;
    public static ulong LookSignature(int sequence) => LookSignaturePrefix | (uint)(sequence & 0xffff);
    public static bool IsLookInjection(ulong extraInfo) => (extraInfo & 0xffff0000UL) == LookSignaturePrefix && extraInfo <= uint.MaxValue;
    public static bool IsInjected(ulong extraInfo) => extraInfo == InjectedSignature || IsLookInjection(extraInfo);
    public static bool IsStaleLook(ulong extraInfo, uint messageTime, uint now)
        => IsLookInjection(extraInfo) && unchecked(now - messageTime) > LegacyMouseMessageReceipts.LifetimeMilliseconds;
    private const ulong TouchSignature = 0xFF515700;
    private const ulong TouchSignatureMask = 0xFFFFFF00;

    // INPUT_MESSAGE_DEVICE_TYPE / INPUT_MESSAGE_ORIGIN_ID (winuser.h).
    public const uint Mouse = 2, Pen = 8, Touch = 4, Touchpad = 16;
    public const uint Hardware = 1;

    public static bool IsTouch(ulong extraInfo, uint deviceType = 0)
        => !IsInjected(extraInfo) &&
           ((extraInfo & TouchSignatureMask) == TouchSignature || deviceType is Pen or Touch);

    public static bool IsPhysicalMouse(uint deviceType, uint originId)
        => deviceType is Mouse or Touchpad && originId == Hardware;

    public static bool CanForward(ulong extraInfo, uint deviceType, uint originId)
        => Classify(extraInfo, deviceType, originId) != LegacyMouseMessageKind.Rejected;

    public static LegacyMouseMessageKind Classify(ulong extraInfo, uint deviceType, uint originId,
        bool hasReceipt = false)
        => IsLookInjection(extraInfo) ? LegacyMouseMessageKind.Rejected // requires this session's issued packet
            : extraInfo == InjectedSignature ? LegacyMouseMessageKind.Injected
            : IsTouch(extraInfo) ? LegacyMouseMessageKind.Rejected
            : hasReceipt ? LegacyMouseMessageKind.Injected
            : !IsTouch(extraInfo, deviceType) && IsPhysicalMouse(deviceType, originId)
                ? LegacyMouseMessageKind.Physical : LegacyMouseMessageKind.Rejected;

    // Generic button/menu injections can produce a cursor synchronization message as a side effect.
    // Only an issued camera packet may move the view during a touch gesture. Real mouse motion resumes
    // when the gesture and its short promotion tail have ended; no global hardware input is blocked.
    public static bool CanForwardGameplayMove(LegacyMouseMessageKind kind, bool contactActive)
        => kind == LegacyMouseMessageKind.Look || kind == LegacyMouseMessageKind.Physical && !contactActive;

    public static bool ShouldSuppressPromotion(ulong extraInfo, bool gameForeground, bool overlayVisible,
        bool insideOverlay, bool protectedContact)
        => IsTouch(extraInfo) && gameForeground && overlayVisible && (insideOverlay || protectedContact);
}
