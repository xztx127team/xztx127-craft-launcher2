using System.ComponentModel;

namespace XCL2.App.Services;

/// <summary>Runtime failure detection, never an authorization decision or a policy-denial bypass.</summary>
internal static class AiElevationPolicy
{
    public static bool ShouldOffer(int? exitCode, string? output, Exception? error = null)
    {
        if (error is Win32Exception native && native.NativeErrorCode is 5 or 740) return true;
        if (error is UnauthorizedAccessException) return true;
        if (!exitCode.HasValue || exitCode == 0) return false;
        var text = output ?? "";
        // Neither a script policy, missing executable, remote login nor a syntax error is fixed by UAC.
        if (ContainsAny(text, "ExecutionPolicy", "PSSecurityException", "running scripts is disabled", "此系统上禁止运行脚本",
            "CommandNotFoundException", "is not recognized", "不是内部或外部命令", "SyntaxError", "ParserError",
            "authentication failed", "Permission denied (publickey", "HTTP 401", "HTTP 403", "Unauthorized (401)",
            "HTTP/1.1 403", "HTTP/2 403", "(403) Forbidden", "remote server", "远程服务器返回",
            "logon failure", "登录失败", "身份验证失败")) return false;
        return ContainsAny(text, "UnauthorizedAccessException", "0x80070005", "0x800702e4",
            "Access is denied", "Access denied.", "requires elevation", "requested operation requires elevation",
            "must be run as administrator", "requires administrator privileges", "administrative privileges", "需要管理员权限", "需要管理员特权", "请求的操作需要提升",
            "拒绝访问", "访问被拒绝", "PermissionError: [Errno 13]");
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
