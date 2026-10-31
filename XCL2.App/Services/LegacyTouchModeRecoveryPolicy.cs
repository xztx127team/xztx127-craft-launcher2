using XCL2.App.Models;

namespace XCL2.App.Services;

internal enum LegacyTouchModeSelection { Automatic, Look, Menu }

/// <summary>
/// 旧触控的显式模式与一次性入世界恢复。Joined 是一次状态过渡，不是永久的抓取证据。
/// 本类不改变光标或游戏状态；未知原生状态始终允许用户明确选择视角/菜单。
/// </summary>
internal sealed class LegacyTouchModeRecoveryPolicy
{
    private LegacyTouchWorldPhase _phase;
    private bool _phaseInitialized;
    private long? _joinedAt;
    private long? _eligibleSince;
    private const int JoinRecoveryLifetimeMilliseconds = 5000;
    private const int HiddenStabilityMilliseconds = 300;

    public LegacyTouchModeSelection Selection { get; private set; }
    public bool? MenuOverride => Selection switch
    {
        LegacyTouchModeSelection.Look => false,
        LegacyTouchModeSelection.Menu => true,
        _ => null
    };

    /// <returns>进入新加载阶段或退出世界时，调用方应释放输入并回到自动菜单。</returns>
    public bool ObservePhase(LegacyTouchWorldPhase phase, long now)
    {
        if (!_phaseInitialized)
        {
            _phaseInitialized = true;
            _phase = phase;
            return false; // 中途挂载到已加入的游戏不是新的 Joined 过渡。
        }
        if (_phase == phase) return false;
        _phase = phase;
        CancelJoinedRecovery();
        if (phase is LegacyTouchWorldPhase.Loading or LegacyTouchWorldPhase.Outside)
        {
            Selection = LegacyTouchModeSelection.Automatic;
            return true;
        }
        if (phase == LegacyTouchWorldPhase.Joined && Selection == LegacyTouchModeSelection.Automatic)
            _joinedAt = now;
        return false;
    }

    /// <summary>
    /// 仅在真正 Joined 边沿后的短窗口内，持续无可见光标且窗口稳定时提供候选。
    /// 候选可跨两次原生采样保留，确认后必须消费，不能在之后的菜单中再次使用。
    /// </summary>
    public bool ShouldRecoverLook(bool cursorKnown, bool cursorVisible, bool windowStable,
        bool menuRequested, long now)
    {
        if (Selection != LegacyTouchModeSelection.Automatic || _joinedAt is not { } joinedAt) return false;
        if (now < joinedAt || now - joinedAt > JoinRecoveryLifetimeMilliseconds || menuRequested ||
            (cursorKnown && cursorVisible))
        {
            CancelJoinedRecovery();
            return false;
        }
        if (!cursorKnown || !windowStable)
        {
            _eligibleSince = null;
            return false;
        }
        if (!_eligibleSince.HasValue || now < _eligibleSince.Value)
        {
            _eligibleSince = now;
            return false;
        }
        return now - _eligibleSince.Value >= HiddenStabilityMilliseconds;
    }

    public void CancelJoinedRecovery() { _joinedAt = null; _eligibleSince = null; }

    public void Select(LegacyTouchModeSelection selection)
    {
        Selection = selection;
        CancelJoinedRecovery();
    }

    public void Cycle() => Select(Selection switch
    {
        LegacyTouchModeSelection.Automatic => LegacyTouchModeSelection.Look,
        LegacyTouchModeSelection.Look => LegacyTouchModeSelection.Menu,
        _ => LegacyTouchModeSelection.Automatic
    });

    public bool? ApplyMenuAction(TouchMenuAction action, bool currentMenu)
    {
        CancelJoinedRecovery();
        if (Selection == LegacyTouchModeSelection.Automatic || action == TouchMenuAction.None) return null;
        var menu = action switch
        {
            TouchMenuAction.Open => true,
            TouchMenuAction.Close => false,
            _ => !currentMenu
        };
        Select(menu ? LegacyTouchModeSelection.Menu : LegacyTouchModeSelection.Look);
        return menu;
    }

    /// <summary>失焦/窗口替换后不继承手动抓取，也不把当前 Joined 状态伪装成新过渡。</summary>
    public void ResetContext(LegacyTouchWorldPhase phase)
    {
        Select(LegacyTouchModeSelection.Automatic);
        _phase = phase;
        _phaseInitialized = true;
    }
}
