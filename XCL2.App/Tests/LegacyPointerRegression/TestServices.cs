namespace XCL2.App.Services;

// The production logger uses launcher UI/config services; the isolated native-input test writes stderr.
internal static class ErrorPresenter
{
    public static void LogTechnicalDetail(string message) => Console.Error.WriteLine(message);
}

internal static class LauncherLogService
{
    public static void AppendLine(string message) => Console.Error.WriteLine(message);
}
