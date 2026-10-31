using System.IO;
using System.Text;

namespace XCL2.App.Services;

/// <summary>Default exports omit authenticated sessions; executable scripts safely quote every argument.</summary>
internal static class LaunchScriptExportPolicy
{
    internal const string AccountNotice = "为了保护账号，已生成不含登录凭据的提示脚本；请从 XCL2 启动。";
    internal static async Task<bool> ConfirmTokenExportAsync(Func<string, string, string, Task<bool?>> confirm)
    {
        if (await confirm(
                "含令牌启动脚本会将当前游戏登录令牌以明文写入 launch-with-token.bat。拿到文件的人可能在令牌有效期内使用你的游戏身份。是否继续？",
                "安全提醒（1/2）：令牌将写入文件", "了解风险，继续") != true)
            return false;
        return await confirm(
            "再次确认：请勿分享、上传或把含令牌脚本放进要分享的整合包、备份中。只有本次导出会包含令牌，普通导出仍不包含；令牌过期后需重新登录并导出。确定将当前令牌写入文件？",
            "安全提醒（2/2）：确认导出令牌", "确认导出令牌") == true;
    }

    internal static string ProtectedNotice() => string.Join("\r\n", new[]
    {
        "@echo off", "setlocal DisableDelayedExpansion",
        "\"%SystemRoot%\\System32\\chcp.com\" 65001 >nul",
        "echo " + AccountNotice,
        "echo 默认导出不包含登录令牌。如需包含令牌，请在实例设置中选择导出含令牌启动脚本。",
        "pause", "exit /b 1", ""
    });

    internal static string Offline(string java, string directory, IReadOnlyList<string> args, bool highPerformanceGpu)
        => Runnable(java, directory, args, highPerformanceGpu);

    internal static string Runnable(string java, string directory, IReadOnlyList<string> args, bool highPerformanceGpu)
    {
        // Validate everything before constructing a command. CMD and the Java runtime
        // parse quotes differently; embedded quotes/control characters fail closed.
        var command = Quote(java) + " " + string.Join(" ", args.Select(arg => Quote(arg, nativeArgument: true)));
        var lines = new List<string>
        {
            "@echo off", "setlocal DisableDelayedExpansion",
            "\"%SystemRoot%\\System32\\chcp.com\" 65001 >nul",
            "cd /d " + Quote(directory), "if errorlevel 1 exit /b 1",
            highPerformanceGpu ? "set \"SHIM_MCCOMPAT=0x800000001\"" : "set \"SHIM_MCCOMPAT=\""
        };
        foreach (var argument in args.Where(arg => arg.StartsWith("-Dlog4j.configurationFile=", StringComparison.OrdinalIgnoreCase)))
        {
            var path = argument[(argument.IndexOf('=') + 1)..];
            // LauncherLoggingPolicy stores each verified XML beneath its expected hash.
            // Re-check that pinned hash at execution time; a later deletion/tamper must
            // not make the old Log4j dependency fall back to an unsafe default config.
            var hash = Path.GetFileName(Path.GetDirectoryName(path));
            if (hash == null || hash.Length != 40 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("日志安全配置没有固定校验值，请从 XCL2 启动。");
            var literal = path.Replace("'", "''", StringComparison.Ordinal);
            var verify = "$ErrorActionPreference='Stop'; $s=$null; $h=$null; try { " +
                "$s=[IO.File]::OpenRead('" + literal + "'); $h=[Security.Cryptography.SHA1]::Create(); " +
                "$actual=[BitConverter]::ToString($h.ComputeHash($s)).Replace('-',''); " +
                "if ($actual -ne '" + hash + "') { exit 1 } } catch { exit 1 } finally { " +
                "if ($s) { $s.Dispose() }; if ($h) { $h.Dispose() } }; exit 0";
            lines.Add("\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo -NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(verify)));
            lines.Add("if errorlevel 1 (");
            lines.Add("  echo 日志安全配置缺失或校验失败，请从 XCL2 修复并启动。");
            lines.Add("  pause");
            lines.Add("  exit /b 1");
            lines.Add(")");
        }
        lines.Add(command);
        lines.Add("pause");
        lines.Add("");
        return string.Join("\r\n", lines);
    }

    internal static string Quote(string value, bool nativeArgument = false)
    {
        if (value == null || value.Any(char.IsControl) || value.Contains('"'))
            throw new InvalidDataException("参数含批处理无法安全表示的引号或控制字符，请从 XCL2 启动。");
        // No CALL/second parse is used. Double '%' for the batch parser and keep '!' as
        // literal data with delayed expansion disabled. Quote every argument, including
        // ones without spaces, so &, |, < and > cannot become CMD operators.
        var escaped = value.Replace("%", "%%", StringComparison.Ordinal);
        if (nativeArgument)
        {
            var trailingBackslashes = value.Length - value.TrimEnd('\\').Length;
            escaped += new string('\\', trailingBackslashes);
        }
        return "\"" + escaped + "\"";
    }

    internal static void Write(string path, string script) =>
        ArchiveImportSafety.ExportAtomically(path, temporary =>
            File.WriteAllText(temporary, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
}
