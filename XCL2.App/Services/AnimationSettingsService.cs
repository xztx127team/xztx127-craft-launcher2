using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Services;

public enum AnimationKind
{
    WindowOpen,
    WindowMinimize,
    WindowRestore,
    WindowFullscreen,
    WindowClose,
    Page,
    Popup,
    Sidebar,
    ControlFeedback,
    Toast,
    Loading,
    SmoothScroll,
    SkinMotion,
    SkinAttack,
    SkinHurt,
    Transparency
}

/// <summary>统一动画偏好入口。设置页的即时预览独立于配置，撤销时不会留下未保存值。</summary>
public static class AnimationSettingsService
{
    private static readonly string[] OptionKeys = Enum.GetNames<AnimationKind>();
    private static StartupAnimationPreferences? _startupPreferences;

    /// <summary>仅由主窗口创建前的独立提示调用；正式 ConfigService 存在后自动优先使用正式配置。</summary>
    public static void LoadStartupPreferences()
    {
        if (ConfigService.Active != null || _startupPreferences != null) return;
        _startupPreferences = StartupAnimationPreferences.Read(new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2", "config.json"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "XCL2", "Json", "config.json"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "json", "config.json"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "xcl2", "config.json")
        });
    }

    static AnimationSettingsService()
    {
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            if (dispatcher.CheckAccess()) NotifyChanged();
            else dispatcher.BeginInvoke(new Action(NotifyChanged));
        };
    }

    private static bool SystemRequestsReducedMotion
        => (ConfigService.Active?.Config.FollowSystemReducedMotion ?? _startupPreferences?.FollowSystemReducedMotion ?? true) && !SystemParameters.ClientAreaAnimation;

    public static readonly IReadOnlyList<(AnimationKind Kind, string Label)> Options =
        new (AnimationKind Kind, string Label)[]
        {
            (AnimationKind.WindowOpen, "窗口打开"),
            (AnimationKind.WindowMinimize, "窗口最小化 / 收到托盘"),
            (AnimationKind.WindowRestore, "窗口恢复 / 从托盘还原"),
            (AnimationKind.WindowFullscreen, "窗口最大化 / 全屏切换"),
            (AnimationKind.WindowClose, "窗口关闭"),
            (AnimationKind.Page, "页面切换"),
            (AnimationKind.Popup, "弹窗 / 抽屉"),
            (AnimationKind.Sidebar, "侧边栏展开 / 收起"),
            (AnimationKind.ControlFeedback, "按钮 / 文字点击反馈"),
            (AnimationKind.Toast, "通知提示"),
            (AnimationKind.Loading, "加载指示"),
            (AnimationKind.SmoothScroll, "平滑滚动"),
            (AnimationKind.SkinMotion, "3D 皮肤日常动作"),
            (AnimationKind.SkinAttack, "3D 皮肤攻击动作"),
            (AnimationKind.SkinHurt, "3D 皮肤受伤动作 / 红色闪光"),
            (AnimationKind.Transparency, "透明渐入（起始不透明度）")
        };

    private sealed record PreviewState(bool Enabled, bool LowPerformance, bool PageEnabled,
        double SpeedPercent, bool NoAnimations, NoAnimationOptimization Optimization,
        Dictionary<string, AnimationOption> Settings);

    private static PreviewState? _preview;

    private sealed record EffectiveState(double Speed, bool NoAnimations,
        NoAnimationOptimization Optimization, double[] Intensities, double[] Durations, bool TransparencyEnabled);
    private static EffectiveState? _lastNotifiedState;

    public static event Action? Changed;

    public static bool NoAnimationModeEnabled
        => App.StartupArgs.DisableAnimations || SystemRequestsReducedMotion ||
           (_preview?.NoAnimations ?? ConfigService.Active?.Config.CompletelyDisableAnimations ?? _startupPreferences?.CompletelyDisableAnimations ?? false);

    public static NoAnimationOptimization OptimizationPreference
        => _preview?.Optimization ?? ConfigService.Active?.Config.NoAnimationOptimization ?? _startupPreferences?.NoAnimationOptimization ?? NoAnimationOptimization.Balanced;

    public static double SpeedRatio
    {
        get
        {
            var percent = _preview?.SpeedPercent ?? ConfigService.Active?.Config.AnimationSpeedPercent ?? _startupPreferences?.AnimationSpeedPercent ?? 75;
            return double.IsFinite(percent) ? Math.Clamp(percent / 100, 0.25, 1) : 0.75;
        }
    }

    /// <summary>75% 为传入的参考时长；100% 时快 1/3。受伤闪光不拉长，80ms 仍为 80ms。</summary>
    public static TimeSpan Duration(int milliseconds, AnimationKind kind)
    {
        if (!IsEnabled(kind)) return TimeSpan.Zero;
        // “0.2 秒”是实际过渡时长，不再被全局速度再次缩放；入场与退场共用同一个值。
        if (kind is AnimationKind.Popup or AnimationKind.Toast)
            return TimeSpan.FromSeconds(GetEffectiveOption(kind).DurationSeconds);
        var reference = Math.Max(0, milliseconds);
        return TimeSpan.FromMilliseconds(kind == AnimationKind.SkinHurt ? reference : reference * 0.75 / SpeedRatio);
    }

    public static double Intensity(AnimationKind kind)
    {
        // 命令行临时禁用不改写用户偏好，本次进程所有分类均静态显示。
        if (App.StartupArgs.DisableAnimations || NoAnimationModeEnabled) return 0;
        var index = (int)kind;
        if ((uint)index >= (uint)OptionKeys.Length) return 0;
        var preview = _preview;
        var config = ConfigService.Active?.Config;
        if (!(preview?.Enabled ?? config?.EnableUiAnimations ?? _startupPreferences?.EnableUiAnimations ?? true)
            || (preview?.LowPerformance ?? config?.LowPerformanceMode ?? _startupPreferences?.LowPerformanceMode ?? false)
            || (kind == AnimationKind.Page && !(preview?.PageEnabled ?? config?.EnablePageAnimations ?? _startupPreferences?.EnablePageAnimations ?? true)))
            return 0;

        var option = GetEffectiveOption(kind);
        if (!option.Enabled) return 0;
        var percent = option.IntensityPercent;
        return double.IsFinite(percent) ? Math.Clamp(percent / 100, 0, kind == AnimationKind.Transparency ? .7 : 1) : kind == AnimationKind.Transparency ? .3 : 1;
    }

    public static bool IsEnabled(AnimationKind kind)
    {
        // 透明渐入允许从完全透明（0%）开始；0% 不代表关闭这一分类。
        if (kind != AnimationKind.Transparency) return Intensity(kind) > 0;
        return !App.StartupArgs.DisableAnimations && !NoAnimationModeEnabled
            && (_preview?.Enabled ?? ConfigService.Active?.Config.EnableUiAnimations ?? _startupPreferences?.EnableUiAnimations ?? true)
            && !(_preview?.LowPerformance ?? ConfigService.Active?.Config.LowPerformanceMode ?? _startupPreferences?.LowPerformanceMode ?? false)
            && GetEffectiveOption(kind).Enabled;
    }

    private static EffectiveState CaptureEffectiveState()
        => new(SpeedRatio, NoAnimationModeEnabled, OptimizationPreference,
            Options.Select(option => Intensity(option.Kind)).ToArray(),
            Options.Select(option => Duration(200, option.Kind).TotalMilliseconds).ToArray(), IsEnabled(AnimationKind.Transparency));

    public static void NotifyChanged()
    {
        var current = CaptureEffectiveState();
        var previous = _lastNotifiedState;
        _lastNotifiedState = current;
        // 即时预览已经应用了滑杆值。保存同一组值时无需再次结束动画，
        // 否则自动保存会把刚开始的预览/正在播放的过渡强行截断。
        if (previous != null && previous.Speed == current.Speed
            && previous.TransparencyEnabled == current.TransparencyEnabled
            && previous.NoAnimations == current.NoAnimations
            && previous.Optimization == current.Optimization
            && previous.Intensities.SequenceEqual(current.Intensities)
            && previous.Durations.SequenceEqual(current.Durations)) return;
        Changed?.Invoke();
    }

    private static AnimationOption DefaultOption(AnimationKind kind)
        => new() { Enabled = kind != AnimationKind.Transparency,
            IntensityPercent = kind is AnimationKind.Popup or AnimationKind.Toast or AnimationKind.Transparency ? 30 : 100 };

    private static AnimationOption GetEffectiveOption(AnimationKind kind)
    {
        var settings = _preview?.Settings ?? ConfigService.Active?.Config.AnimationSettings ?? _startupPreferences?.AnimationSettings;
        return settings != null && settings.TryGetValue(kind.ToString(), out var option) && option != null
            ? option : DefaultOption(kind);
    }

    public static AnimationOption GetOption(AppConfig config, AnimationKind kind)
    {
        var index = (int)kind;
        if ((uint)index >= (uint)OptionKeys.Length
            || !config.AnimationSettings.TryGetValue(OptionKeys[index], out var option) || option == null)
            return DefaultOption(kind);
        return new AnimationOption
        {
            Enabled = option.Enabled, IntensityPercent = kind == AnimationKind.Transparency ? Math.Clamp(option.IntensityPercent, 0, 70) : option.IntensityPercent,
            DurationSeconds = option.DurationSeconds
        };
    }

    public static void SetPreview(AppConfig config)
    {
        _lastNotifiedState ??= CaptureEffectiveState();
        _preview = new PreviewState(config.EnableUiAnimations, config.LowPerformanceMode,
            config.EnablePageAnimations, config.AnimationSpeedPercent, config.CompletelyDisableAnimations,
            config.NoAnimationOptimization, Options.ToDictionary(item => item.Kind.ToString(),
                item => GetOption(config, item.Kind), StringComparer.OrdinalIgnoreCase));
        NotifyChanged();
    }

    public static void ClearPreview()
    {
        if (_preview == null) return;
        _lastNotifiedState ??= CaptureEffectiveState();
        _preview = null;
        NotifyChanged();
    }
}
