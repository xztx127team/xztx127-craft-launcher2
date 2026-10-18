namespace XCL2.App { public static class App { public static string DataDir { get; set; } = ""; } }
namespace XCL2.App.Services {
 public sealed class LauncherConfigStub { public string LastLaunchCountDate { get; set; } = ""; public int TodayLauncherStartCount { get; set; } }
 public sealed class ConfigService { public LauncherConfigStub Config { get; } = new(); public void Save() { } }
 public static class ErrorPresenter { public static void LogTechnicalDetail(string message) { throw new System.InvalidOperationException(message); } }
}
