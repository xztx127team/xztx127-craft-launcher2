using System.IO;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>Bounded, local diagnostic text sanitization. Never reads config/account stores.</summary>
public static class DiagnosticRedactionService
{
    public const int MaxEntryChars = 384 * 1024;
    private static string Replace(string text, string pattern, string replacement) =>
        Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    public static string Sanitize(string? raw, int limit = MaxEntryChars)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        limit = Math.Clamp(limit, 256, MaxEntryChars);
        // Limit work before regex, retaining useful environment/header and final failure context.
        var text = raw.Replace("\0", "");
        if (text.Length > limit)
        {
            var head = Math.Min(6000, limit / 4);
            var headEnd = text.LastIndexOf('\n', head - 1);
            var tailStart = text.IndexOf('\n', text.Length - (limit - head));
            // Do not expose a sliced token whose key/prefix was outside the selected region.
            text = (headEnd >= 0 ? text[..headEnd] : "") + "\n\n[中间内容因体积限制已省略]\n\n" +
                (tailStart >= 0 ? text[(tailStart + 1)..] : "");
        }
        try
        {
            text = Replace(text, "(https?://[^\\s?#\"'<>]+)[?#][^\\s\"'<>]*", "$1?<查询参数已隐藏>");
            text = Replace(text, "(?m)(^|\\s)(--?(?:access[-_]?token|refresh[-_]?token|client[-_]?token|token|password|passwd|api[-_]?key|client[-_]?secret)(?:\\s+|=))(?:\"[^\"]*\"|'[^']*'|[^\\s]+)", "$1$2<已隐藏>");
            text = Replace(text, "((?:\"|')?(?:access[-_]?token|refresh[-_]?token|client[-_]?token|id[-_]?token|token|auth|secret|api[-_]?key|authorization|password|passwd|client[-_]?secret|session[-_]?id|username|user_name|accountName|cookie|set-cookie)(?:\"|')?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;}]+)", "$1<已隐藏>");
            text = Replace(text, "(?m)(^\\s*(?:Set-Cookie|Cookie|Authorization)\\s*:\\s*).*$", "$1<已隐藏>");
            text = Replace(text, "\\b(Bearer|Basic)\\s+[A-Za-z0-9+/=_\\-.]+", "$1 <已隐藏>");
            text = Replace(text, "\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+", "<令牌已隐藏>");
            text = Replace(text, "\\b(?:sk-|gh[pousr]_)[A-Za-z0-9_-]{12,}", "<密钥已隐藏>");
            text = Replace(text, "(https?://)[^/\\s:@]+:[^/\\s@]+@", "$1<身份信息已隐藏>@");
            text = Replace(text, "\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b", "<邮箱已隐藏>");
            text = Replace(text, "((?:--username|--user|userName|user_name|accountName)\\s*(?:[:=]\\s*|\\s+))(?:\"[^\"]*\"|'[^']*'|[^\\s,;}]+)", "$1<用户名已隐藏>");
            text = Replace(text, "((?:\"|')?(?:账户|账号|用户名|玩家名|玩家)(?:\"|')?\\s*[:：=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\r\\n]*)", "$1<身份信息已隐藏>");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                text = text.Replace(profile, "<用户目录>", StringComparison.OrdinalIgnoreCase);
                text = text.Replace(profile.Replace('\\', '/'), "<用户目录>", StringComparison.OrdinalIgnoreCase);
            }
            text = Replace(text, @"[A-Z]:[\\/](?:Users|Documents and Settings)[\\/][^\\/\r\n\t\""<>|]+", "<用户目录>");
            text = Replace(text, @"/(?:home|Users)/[^/\s\""'<>]+", "<用户目录>");
            return text.Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: never expose the unprocessed original on adversarial log content.
            return "[此段日志过于复杂，无法安全脱敏，已从诊断内容中省略。]";
        }
    }

    public static string TrimPartialFirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline >= 0 ? text[(newline + 1)..] : "[超长单行日志已省略]";
    }

    public static string ReadTextTail(string path, int maxBytes = 512 * 1024)
    {
        maxBytes = Math.Clamp(maxBytes, 1024, 2 * 1024 * 1024);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var partialFirstLine = stream.Length > maxBytes;
        if (partialFirstLine) stream.Seek(-maxBytes, SeekOrigin.End);
        var bytes = new byte[maxBytes];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0) break;
            read += count;
        }
        var text = System.Text.Encoding.UTF8.GetString(bytes, 0, read);
        if (partialFirstLine) text = TrimPartialFirstLine(text);
        return Sanitize(text);
    }
}
