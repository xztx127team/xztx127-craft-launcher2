namespace XCL2.App.Services;

internal static class TouchRoutePolicy
{
    public const string RuntimeRawInputAdvice = "运行中开启 GLFW 触屏后，若移动键正常但手指不能转视角，请在游戏“设置 → 控制 → 鼠标设置”关闭“原始输入（Raw Input）”，再返回游戏。也可以先保存退出，再在启动前选择触屏模式重新启动；启动器会为旧版临时设置并在退出后恢复该选项。仅修改磁盘配置不会实时改变已运行的游戏。";
    public static bool NeedsRuntimeRawInputAdvice(TouchGameBackend backend, bool requestedAtLaunch, bool? versionModern)
        => backend == TouchGameBackend.Glfw && !(requestedAtLaunch && versionModern == false);

    // The actual window is authoritative for transport; metadata remains the fallback.
    public static bool UseModern(bool? versionModern, TouchGameBackend backend) => backend switch
    {
        TouchGameBackend.Sdl => true,
        TouchGameBackend.Glfw or TouchGameBackend.Lwjgl2 => false,
        _ => versionModern == true
    };
}
