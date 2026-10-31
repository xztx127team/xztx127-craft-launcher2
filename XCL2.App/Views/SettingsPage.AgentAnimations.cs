using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private void SubscribeAgentAnimationUpdates()
    {
        AiAgentAnimationService.ProfileApplied -= RefreshAgentAnimationSliders;
        AiAgentAnimationService.ProfileApplied += RefreshAgentAnimationSliders;
    }

    private void UnsubscribeAgentAnimationUpdates()
        => AiAgentAnimationService.ProfileApplied -= RefreshAgentAnimationSliders;

    private void RefreshAgentAnimationSliders(AppConfig config)
    {
        if (!ReferenceEquals(config, _owner.ConfigService.Config)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RefreshAgentAnimationSliders(config));
            return;
        }
        var wasSuppressed = _suppressDirtyTracking;
        _suppressDirtyTracking = true;
        try
        {
            foreach (var (kind, controls) in _animationControls)
                controls.Intensity.Value = AnimationSettingsService.GetOption(config, kind).IntensityPercent;
        }
        finally { _suppressDirtyTracking = wasSuppressed; }
        // 保留设置页中其它尚未保存的编辑，重新应用当前控件预览。
        PreviewAnimationSettings();
    }
}
