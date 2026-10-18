namespace System.Windows
{
    public class Application
    {
        public static Application? Current => null;
        public object? MainWindow => null;
        public Dispatcher? Dispatcher => null;
    }
    public class Dispatcher
    {
        public bool CheckAccess() => true;
        public void Invoke(Action action) => action();
        public void BeginInvoke(Action action) => action();
    }
}
namespace XCL2.App.Models
{
    public class AppConfig
    {
        public bool Ux50DownloadStageDetails { get; set; } = true;
        public bool Ux50DownloadRetryCountdown { get; set; } = true;
        public bool Ux50DownloadQuietHours { get; set; }
        public string Ux50DownloadQuietStart { get; set; } = "22:00";
        public string Ux50DownloadQuietEnd { get; set; } = "08:00";
        public bool RememberInterruptedDownloads { get; set; } = true;
        public int DownloadPopupSizeDisplayMode { get; set; }
        public int DownloadTaskConcurrency { get; set; }
        public int DownloadSpeedLimitKBps { get; set; }
        public bool ReduceDownloadSpeedWhileGaming { get; set; }
        public int GamingDownloadSpeedLimitKBps { get; set; } = 1024;
        public bool GameVersionNoPopup { get; set; }
        public bool CommunityResourceNoPopup { get; set; }
        public bool ModpackNoPopup { get; set; }
        public int DownloadNotifyMode { get; set; }
    }
}
namespace XCL2.App.Services
{
    public static class ReminderCenterService
    { public static void Publish(string key, string title, string description) { } }
    public static class LauncherLogService { public static void AppendLine(string value) { } }
    public enum JavaInstallMode { Portable, System }
    public record JavaDownloadRequest(int MajorVersion, string Arch, JavaInstallMode InstallMode);
    public sealed class JavaService
    {
        public Func<JavaDownloadRequest, IProgress<ProgressInfo>?, CancellationToken, Task<string>> Download { get; set; }
            = (_, _, _) => Task.FromResult("fixture-javaw.exe");
        public Task<string> DownloadJavaAsync(JavaDownloadRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
            => Download(request, progress, ct);
    }
    public record ProgressInfo(string Stage, int Done, int Total, string CurrentFile) { public string Unit { get; init; } = "项"; }
    public class ConfigService { public static ConfigService? Active { get; set; } = new(); public Models.AppConfig Config { get; } = new(); }
    public static class ToastService
    {
        public static void ShowInfo(string text) { }
        public static void ShowSuccess(string text) { }
        public static void ShowSystemNotification(string text, string title) { }
    }
}
namespace XCL2.App.Views
{
    public class MainWindow { public Services.ConfigService ConfigService { get; } = new(); }
}
