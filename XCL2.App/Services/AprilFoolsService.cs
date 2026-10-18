using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>日期、偏好和当天退出状态；这里不引用 WPF，也不改变真实启动权限。</summary>
public static class AprilFoolsService
{
    private static string UserStateDir
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Path.GetTempPath(), "XCL2-" + Environment.UserName);
            return Path.Combine(root, "XCL2", "cdjs");
        }
    }
    private static readonly AprilFoolsStateStore Store = new(UserStateDir, Path.Combine(App.DataDir, "cdjs"));
    private static string StateDir => Store.DirectoryPath;
    private static readonly object LegacySwitchSync = new();
    private static long _legacySwitchCheckedAt;
    private static bool _legacySwitchLoaded, _legacySwitchDisabled;
    public static string? PersistenceWarning { get; private set; }
    public static event Action<string>? PersistenceWarningRaised;
    private static AprilFoolsState? _state;
    private static Effect _sessionEffects;
    private static bool _forceAprilForSession, _sessionEffectsDismissed;
    private static readonly Random Rng = new();
    public static event Action? StateChanged;

    private static void NotifyStateChanged()
    {
        if (StateChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception ex) { System.Diagnostics.Trace.TraceError(ex.ToString()); }
    }

    [Flags]
    public enum Effect
    {
        None = 0, RickRoll1 = 1 << 0, FakeWin32Error = 1 << 1,
        DodgeLaunchButton = 1 << 2, RickRoll2 = 1 << 3, WindowChaos = 1 << 4,
        LauncherRefuses = 1 << 5, TileChaos = 1 << 6
    }
    public const Effect MotionEffects = Effect.DodgeLaunchButton | Effect.WindowChaos | Effect.TileChaos;
    private static readonly Dictionary<int, (Effect Effect, string Description)> Catalog = new()
    {
        [1] = (Effect.RickRoll1, "愚人节彩蛋 1：已自行允许的视频彩蛋。可在趣味与彩蛋设置中关闭。"),
        [2] = (Effect.FakeWin32Error, "愚人节彩蛋 2：已自行允许的模拟报错，不代表真实故障。"),
        [3] = (Effect.DodgeLaunchButton, "愚人节彩蛋 3：启动按钮快速避让鼠标。真正触发启动就认输；F1 可立即恢复。"),
        [4] = (Effect.RickRoll2, "愚人节彩蛋 4：已自行允许的另一段视频彩蛋。"),
        [5] = (Effect.WindowChaos, "愚人节彩蛋 5：窗口在当前显示器工作区快速移动。F1 可立即恢复。"),
        [7] = (Effect.LauncherRefuses, "愚人节彩蛋 7：已自行允许的拒绝启动台词，可在趣味与彩蛋设置中关闭。"),
        [8] = (Effect.TileChaos, "愚人节彩蛋 8：首页磁贴快速跳动。真正触发启动就结束当天整蛊。")
    };
    private sealed class AprilFoolsState
    {
        public DateTime SelectedDate { get; set; }
        public Effect Effects { get; set; }
        public bool FlagClicked { get; set; }
    }
    private static AppConfig? Config => ConfigService.Active?.Config;
    private static bool Enabled => Config?.EnableAprilFoolsEasterEggs == true && !IsDisabledByRegistry();
    private static DateTime SelectedDate => _forceAprilForSession ? new DateTime(2000, 4, 1) : DateTime.Now.Date;
    private static bool DateEffectsActive => Enabled && (IsAprilFoolsDate() || _forceAprilForSession)
        && _state is { FlagClicked: false } && _state.SelectedDate == SelectedDate;
    public static bool IsActive => ActiveEffects != Effect.None;
    public static bool HasMotionEffects => (ActiveEffects & MotionEffects) != Effect.None;
    public static Effect ActiveEffects => !Enabled ? Effect.None : AllowedEffects(
        (DateEffectsActive ? _state!.Effects : Effect.None) |
        (!_sessionEffectsDismissed ? _sessionEffects : Effect.None));
    public static bool Has(Effect effect) => effect != Effect.None && (ActiveEffects & effect) == effect;
    public static Effect AllowedEffects(Effect requested)
    {
        var cfg = Config;
        if (cfg == null || !cfg.EnableAprilFoolsEasterEggs) return Effect.None;
        var allowed = Effect.None;
        if (cfg.AprilFoolsWindowMovement) allowed |= Effect.WindowChaos;
        if (cfg.AprilFoolsButtonDodge) allowed |= Effect.DodgeLaunchButton;
        if (cfg.AprilFoolsTileMovement) allowed |= Effect.TileChaos;
        if (cfg.AprilFoolsExternalVideos) allowed |= Effect.RickRoll1 | Effect.RickRoll2;
        if (cfg.AprilFoolsFakeErrors) allowed |= Effect.FakeWin32Error;
        if (cfg.AprilFoolsLaunchRefusal) allowed |= Effect.LauncherRefuses;
        return requested & allowed;
    }
    public static void EnableDebugAprilFools() { if (Enabled) _forceAprilForSession = true; }
    private static bool IsAprilFoolsDate() => DateTime.Now is { Month: 4, Day: 1 };
    private static bool IsDisabledByRegistry()
    {
        lock (LegacySwitchSync)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_legacySwitchLoaded || System.Diagnostics.Stopwatch.GetElapsedTime(_legacySwitchCheckedAt, now) >= TimeSpan.FromSeconds(1))
            {
                try { _legacySwitchDisabled = RegistryConfigService.GetInt("noyrj", 0) == 1; }
                catch { _legacySwitchDisabled = false; }
                _legacySwitchLoaded = true; _legacySwitchCheckedAt = now;
            }
            return _legacySwitchDisabled;
        }
    }
    /// <summary>保存偏好时立即重读旧开关；动画帧只读取缓存，外部注册表修改最多延迟一秒。</summary>
    private static void InvalidateLegacySwitch() { lock (LegacySwitchSync) _legacySwitchLoaded = false; }
    private static void WarnStorage(string? warning)
    {
        if (warning is null || PersistenceWarning == warning) return;
        PersistenceWarning = warning;
        if (PersistenceWarningRaised is not { } handlers) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(warning); } catch (Exception ex) { System.Diagnostics.Trace.TraceError(ex.ToString()); }
    }
    /// <summary>在配置 Load 后初始化；同一天刷新不会重新抽取或复活已经结束的彩蛋。</summary>
    public static void EnsureTodaysStateLoaded()
    {
        if (!Enabled || (!IsAprilFoolsDate() && !_forceAprilForSession))
        {
            if (_state != null) { _state = null; NotifyStateChanged(); }
            return;
        }
        var today = SelectedDate;
        if (_state?.SelectedDate == today) return;
        if (!_forceAprilForSession) LoadState();
        if (_state?.SelectedDate != today)
        {
            _state = new AprilFoolsState { SelectedDate = today, Effects = RollRandomEffects() };
            if (!_forceAprilForSession) { SaveState(); PersistTriggeredDescriptions(AllowedEffects(_state.Effects)); }
        }
        NotifyStateChanged();
    }
    public static void RefreshPreferences() { InvalidateLegacySwitch(); EnsureTodaysStateLoaded(); NotifyStateChanged(); }
    private static Effect RollRandomEffects()
    {
        var pool = Catalog.Values.Select(value => value.Effect).Where(effect => AllowedEffects(effect) != Effect.None).ToArray();
        if (pool.Length == 0) return Effect.None;
        var result = pool[Rng.Next(pool.Length)];
        if (Rng.NextDouble() < .30) result |= pool[Rng.Next(pool.Length)];
        if (Rng.NextDouble() < .15) result |= pool[Rng.Next(pool.Length)];
        return result;
    }
    public static Effect ActivateRandomSessionEffect()
    {
        if (!Enabled) return Effect.None;
        _sessionEffects = RollRandomEffects();
        _sessionEffectsDismissed = false;
        NotifyStateChanged();
        return _sessionEffects;
    }
    private static void PersistTriggeredDescriptions(Effect triggered)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            foreach (var (number, (effect, description)) in Catalog)
            {
                var path = Path.Combine(StateDir, $"{number}.txt");
                if ((triggered & effect) == effect && !File.Exists(path)) File.WriteAllText(path, description);
            }
        }
        catch { }
    }
    public static List<string> ClickWhiteFlag()
    {
        var descriptions = Catalog.Values.Where(value => Has(value.Effect)).Select(value => value.Description).ToList();
        DismissForToday();
        return descriptions;
    }
    /// <summary>白旗、F1 和成功启动共用；真实日期状态持久保存。</summary>
    public static void DismissForToday()
    {
        if (_state != null && _state.SelectedDate == SelectedDate)
        {
            _state.FlagClicked = true;
            if (!_forceAprilForSession) SaveState();
        }
        _sessionEffectsDismissed = true;
        _sessionEffects = Effect.None;
        NotifyStateChanged();
    }
    private static void LoadState()
    {
        Store.TryLoad<AprilFoolsState>(out _state, out var warning);
        WarnStorage(warning);
    }
    private static void SaveState()
    {
        if (!Store.TrySave(_state, out var warning)) WarnStorage(warning);
    }
    /// <summary>仅用于下载等非启动入口；移动类效果绝不拦截真实操作。</summary>
    public static bool TryGetLaunchOrDownloadInterception(out Effect which)
    {
        which = Effect.None;
        if (!IsActive) return false;
        var candidates = Catalog.Values.Select(value => value.Effect)
            .Where(effect => (effect & MotionEffects) == Effect.None && Has(effect)).ToArray();
        if (candidates.Length == 0) return false;
        which = candidates[Rng.Next(candidates.Length)];
        return true;
    }
    public static string RandomRefusalText()
    {
        string[] texts = ["游戏今天不想启动。", "启动器今天心情不好，改天再来吧。", "游戏说它还没睡醒。"];
        return texts[Rng.Next(texts.Length)];
    }
}
