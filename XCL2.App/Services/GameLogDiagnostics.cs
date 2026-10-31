using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public sealed record GameLogIssue(string Level, string Summary, string Repair, int Occurrences);

/// <summary>日志等级不是进程状态：运行中的 ERROR 只表示待排查警告；退出后才结合尾部证据判断。</summary>
public static class GameLogDiagnostics
{
    private static readonly Regex LevelPattern = new(@"(?:^|\[|/|\s)(?<level>WARN(?:ING)?|ERROR|FATAL)(?:\]|\s|:)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExceptionPattern = new(@"^\s*(?:Caused by:|Exception in thread|(?:[\w$]+\.)+[\w$]*(?:Exception|Error)(?::|$))", RegexOptions.Compiled);
    private static readonly Regex PrefixPattern = new(@"^\s*(?:\[[^\]\r\n]*\]\s*)+", RegexOptions.Compiled);
    private static readonly string[] ShutdownMarkers = { "Stopping!", "Stopping server", "Saving worlds", "Saving players", "Client shutdown", "Sound engine shut down" };

    public static bool IsIssueLine(string line) => LevelPattern.IsMatch(line) || ExceptionPattern.IsMatch(line);
    public static bool IsErrorLine(string line)
    {
        var match = LevelPattern.Match(line);
        return match.Success && !match.Groups["level"].Value.StartsWith("WARN", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>只看尾部100行，至少3条明确的错误等级，最后一条后最多8行；排除已正常收尾的情况。</summary>
    public static bool HasTerminalErrorBurst(string text)
    {
        var lines = text.Split('\n').TakeLast(100).ToArray();
        var count = 0;
        var lastError = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (ShutdownMarkers.Any(marker => lines[i].Contains(marker, StringComparison.OrdinalIgnoreCase))) { count = 0; lastError = -1; }
            if (IsErrorLine(lines[i])) { count++; lastError = i; }
        }
        return count >= 3 && lastError >= lines.Length - 9;
    }

    /// <summary>按消息去重，附上保守、可操作的排查方案。不把普通句子中的 error 单词认作等级。</summary>
    public static IReadOnlyList<GameLogIssue> Scan(string text, int maxIssues = 80)
    {
        var issues = new Dictionary<string, GameLogIssue>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var match = LevelPattern.Match(line);
            if (!match.Success && !ExceptionPattern.IsMatch(line)) continue;
            var summary = PrefixPattern.Replace(line.Trim(), "");
            if (string.IsNullOrWhiteSpace(summary)) summary = line.Trim();
            if (summary.Length > 420) summary = summary[..420] + "…";
            var level = match.Groups["level"].Value.StartsWith("WARN", StringComparison.OrdinalIgnoreCase) ? "WARN" : "ERROR";
            var key = level + ":" + summary;
            if (issues.TryGetValue(key, out var old)) issues[key] = old with { Occurrences = old.Occurrences + 1 };
            else if (issues.Count < maxIssues) issues.Add(key, new(level, summary, RepairFor(line), 1));
        }
        return issues.Values.ToArray();
    }

    public static string Format(IReadOnlyList<GameLogIssue> issues, bool running)
        => issues.Count == 0 ? "没有检测到明确的 WARN / ERROR 等级记录。仍可查看完整日志。" :
            string.Join("\n\n", issues.Select((issue, index) => $"{index + 1}. 【{(running || issue.Level == "WARN" ? "警告" : "错误线索")} · {issue.Level} · {issue.Occurrences}次】{issue.Summary}\n修复建议：{issue.Repair}"));

    private static string RepairFor(string line)
    {
        bool Has(string value) => line.Contains(value, StringComparison.OrdinalIgnoreCase);
        if (Has("OpenGL") || Has("GLFW") || Has("LWJGL")) return "从显卡厂商官网更新与本机型号匹配的驱动，确认游戏所需的 OpenGL 版本；再暂时停用光影或渲染模组测试。";
        if (Has("OutOfMemory") || Has("heap space") || Has("memory")) return "检查可用内存和实例内存分配；保留系统余量，减少资源包/模组后重试，不要把全部内存分给游戏。";
        if (Has("mixin") || Has("mod") || Has("dependency") || Has("NoClassDefFound") || Has("ClassNotFound")) return "核对模组、前置依赖、加载器与游戏版本；先备份实例，再停用最近增加的模组逐项排查。";
        if (Has("UnsupportedClassVersion") || Has("java version") || Has("JVM")) return "在实例设置中选择该游戏/加载器要求的 Java 版本，并检查自定义 JVM 参数。";
        if (Has("SSL") || Has("timeout") || Has("connection") || Has("UnknownHost")) return "检查网络、系统时间与代理设置；在线服务不可用时稍后重试，此类日志不一定影响单人游戏。";
        if (Has("AccessDenied") || Has("Permission denied") || Has("being used") || Has("IOException")) return "检查实例目录是否可写、磁盘是否有空间以及文件是否被其他进程占用，关闭占用后重试。";
        if (Has("texture") || Has("resource") || Has("sound")) return "尝试停用最近添加的资源包，检查对应资源是否完整，必要时通过实例修复补全游戏资源。";
        return "查看该条记录后面的异常栈和来源模组，先备份实例，再更新或停用相关组件；若游戏仍可正常运行，可先观察。";
    }
}
