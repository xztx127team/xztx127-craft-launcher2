using XCL2.App.Models;

namespace System.Windows
{
    // Only the OS preference surface is substituted; preference resolution and timing use production code.
    public static class SystemParameters
    {
        public static bool ClientAreaAnimation { get; set; } = true;
        public static event System.ComponentModel.PropertyChangedEventHandler? StaticPropertyChanged;
        public static void Notify() => StaticPropertyChanged?.Invoke(null,
            new System.ComponentModel.PropertyChangedEventArgs(nameof(ClientAreaAnimation)));
    }
    public sealed class Application
    {
        public static Application? Current => null;
        public TestDispatcher Dispatcher => new();
    }
    public sealed class TestDispatcher
    {
        public bool HasShutdownStarted => false;
        public bool CheckAccess() => true;
        public void BeginInvoke(Action action) => action();
    }
}
namespace XCL2.App
{
    public static class App
    {
        public sealed class Arguments { public bool DisableAnimations { get; set; } }
        public static Arguments StartupArgs { get; } = new();
    }
}
namespace XCL2.App.Models
{
    public sealed class AppConfig
    {
        public bool FollowSystemReducedMotion { get; set; } = true;
        public bool EnableUiAnimations { get; set; } = true;
        public bool EnablePageAnimations { get; set; } = true;
        public bool LowPerformanceMode { get; set; }
        public bool CompletelyDisableAnimations { get; set; }
        public double AnimationSpeedPercent { get; set; } = 75;
        public NoAnimationOptimization NoAnimationOptimization { get; set; }
        public Dictionary<string, AnimationOption> AnimationSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
namespace XCL2.App.Services
{
    public sealed class ConfigService
    {
        public static ConfigService? Active { get; set; } = new();
        public AppConfig Config { get; set; } = new();
    }
}
