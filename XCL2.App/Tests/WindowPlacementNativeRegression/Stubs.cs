using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Models
{
    public partial class AppConfig { public bool GuestModeEnabled { get; set; } }
}

namespace XCL2.App.Services
{
    public sealed class ConfigService
    {
        public AppConfig Config { get; } = new();
        public int SaveCalls { get; private set; }
        public void Save() => SaveCalls++;
    }
    public static class AprilFoolsUi
    {
        public static bool IsWindowGeometryTemporary(Window window) => false;
        public static WindowState? OriginalWindowState(Window window) => null;
    }
    public static class ErrorPresenter
    {
        public static void LogFallback(string message, Exception exception)
            => throw new InvalidOperationException(message, exception);
    }
    public enum AnimationKind { WindowOpen, WindowClose, WindowMinimize, WindowRestore, WindowFullscreen }
    public static class AnimationSettingsService
    {
        public static bool IsEnabled(AnimationKind kind) => false;
    }
}
