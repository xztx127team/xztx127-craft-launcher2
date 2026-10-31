using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using XCL2.App.Models;

namespace XCL2.App.Services;

public static class UiZoomService
{
    public const int MinPercent = 40;
    public const int MaxPercent = 300;
    public const int StepPercent = 10;
    public const int DefaultPercent = 100;

    private static ScaleTransform? _transform;
    private static ConfigService? _configService;

    /// <summary>MainWindow 构造/加载完成时调用一次，接上要缩放的 ScaleTransform 和配置服务，
    /// 并把上次保存的缩放比例立即应用一遍（不等用户第一次操作才生效）。</summary>
    public static void Initialize(ScaleTransform transform, ConfigService configService)
    {
        _transform = transform;
        _configService = configService;
        ApplyPercent(ClampPercent(configService.Config.UiZoomPercent), persist: false);
    }

    public static int CurrentPercent { get; private set; } = DefaultPercent;

    private static int ClampPercent(int percent)
    {
        if (percent < MinPercent) return MinPercent;
        if (percent > MaxPercent) return MaxPercent;
        // 统一吸附到 StepPercent 的整数倍，避免累积出 103%、97% 这种不对齐步进的中间值
        // （比如从设置页手动输入了一个奇怪的数字）。
        return (int)System.Math.Round(percent / (double)StepPercent) * StepPercent;
    }

    private static void ApplyPercent(int percent, bool persist)
    {
        CurrentPercent = percent;
        if (_transform != null)
        {
            double scale = percent / 100.0;
            _transform.ScaleX = scale;
            _transform.ScaleY = scale;
        }
        if (persist && _configService != null)
        {
            _configService.Config.UiZoomPercent = percent;
            _configService.Save();
        }
    }

    /// <summary>按一步放大/缩小。delta 为正表示放大一步，为负表示缩小一步——调用方
    /// （滚轮/方向键处理器）不需要关心具体百分比，只需要知道"往哪个方向走一步"。</summary>
    public static void StepZoom(int direction)
    {
        if (direction == 0) return;
        int next = ClampPercent(CurrentPercent + (direction > 0 ? StepPercent : -StepPercent));
        if (next != CurrentPercent) ApplyPercent(next, persist: true);
    }

    public static void ResetZoom() => ApplyPercent(DefaultPercent, persist: true);

    /// <summary>供设置页预览用：实时改变当前窗口缩放，但不写入配置文件。
    /// 设置页是否持久化由其统一的“保存设置 / 自动保存”流程决定。</summary>
    public static void PreviewPercent(int percent) => ApplyPercent(ClampPercent(percent), persist: false);

    /// <summary>需要明确立即持久化的调用方仍可使用此方法。</summary>
    public static void SetPercent(int percent) => ApplyPercent(ClampPercent(percent), persist: true);

    /// <summary>判断当前这次滚轮事件是否应该被当成"缩放"处理：需要功能总开关开启、
    /// 用户在设置里勾选了"滚轮"这种绑定方式、且当前确实按住了 Ctrl。</summary>
    public static bool ShouldHandleWheel(AppConfig cfg)
        => cfg.EnableUiZoomShortcut
           && cfg.UiZoomShortcutBindings.Contains(UiZoomShortcutMode.CtrlWheel)
           && Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

    /// <summary>判断当前这次键盘事件是否应该被当成"缩放"处理：功能总开关开启、
    /// 用户勾选了"方向键"这种绑定方式、按住 Ctrl，且按下的确实是上/下方向键。</summary>
    public static bool ShouldHandleKey(AppConfig cfg, Key key)
        => cfg.EnableUiZoomShortcut
           && cfg.UiZoomShortcutBindings.Contains(UiZoomShortcutMode.CtrlArrow)
           && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
           && (key == Key.Up || key == Key.Down);
}
