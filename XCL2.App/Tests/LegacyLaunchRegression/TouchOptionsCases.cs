using System.Text;
using XCL2.App.Services;

internal static class TouchOptionsCases
{
    public static void Run(string root, Action<bool, string> check)
    {
        var dir = Path.Combine(root, "touch-options");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "options.txt");
        var bom = new UTF8Encoding(true);
        File.WriteAllText(path, "rawMouseInput:false\r\nfov:0.0\r\nrawMouseInput:true\r\n", bom);
        using (LegacyTouchOptions.Acquire(dir))
        {
            var active = File.ReadAllText(path);
            check(!active.Contains("rawMouseInput:true") && active.Split("rawMouseInput:false").Length == 3,
                "legacy launch temporarily disables every duplicate raw-input key");
            File.WriteAllText(path, active.Replace("fov:0.0", "fov:0.4") + "sensitivity:0.65\r\n", bom);
        }
        var restored = File.ReadAllText(path);
        check(restored.Split("rawMouseInput:").Length == 2 && restored.Contains("rawMouseInput:true\r\n") &&
            !restored.Contains("rawMouseInput:false"), "restore keeps original effective raw-input value without a later false override");
        check(restored.Contains("fov:0.4\r\n") && restored.Contains("sensitivity:0.65\r\n"),
            "raw-input restoration preserves settings edited during play");
        check(File.ReadAllBytes(path).AsSpan().StartsWith(bom.GetPreamble()), "raw-input restoration preserves UTF-8 BOM");

        File.WriteAllText(path, "fov:0.0\n");
        using (LegacyTouchOptions.Acquire(dir))
            File.AppendAllText(path, "rawMouseInput:false\n");
        check(File.ReadAllText(path) == "fov:0.0\n", "an originally absent raw-input option removes every temporary duplicate");

        File.WriteAllText(path, "rawMouseInput:true\nfov:0.0\n");
        using (LegacyTouchOptions.Acquire(dir))
            File.WriteAllText(path, "rawMouseInput:true\nfov:0.2\n");
        check(File.ReadAllText(path) == "rawMouseInput:true\nfov:0.2\n", "explicit raw-input changes during play are not overwritten");

        File.WriteAllText(path, "rawMouseInput:true\nrawMouseInput:false\nfov:0.0\n");
        using (LegacyTouchOptions.Acquire(dir)) { }
        check(File.ReadAllText(path) == "rawMouseInput:false\nfov:0.0\n",
            "last original duplicate false remains the effective value after restoration");
    }
}

namespace XCL2.App.Services
{
    internal static class LauncherLogService
    {
        public static void AppendLine(string message) { }
    }
}
