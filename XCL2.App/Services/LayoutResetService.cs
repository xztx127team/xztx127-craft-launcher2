using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>仅供用户主动重置布局调用；不改变功能开关、权限、账号和实例。</summary>
public static class LayoutResetService
{
    public static void Reset(AppConfig config)
    {
        config.FeatureSearchWidth = 300;
        config.FeatureSearchHeight = 36;
        config.FeatureSearchOffsetX = 0;
        config.FeatureSearchOffsetY = 0;
        config.LauncherWindowPlacement = null;
    }
}
