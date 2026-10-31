using System.IO;
using System.Security.Cryptography;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Apply Mojang's per-version Log4j configuration, including cached pre-patch profiles.</summary>
internal static class LauncherLoggingPolicy
{
    internal const string ArgumentTemplate = "-Dlog4j.configurationFile=${path}";
    private const string CorePrefix = "org.apache.logging.log4j:log4j-core:";

    internal static LoggingClientConfiguration? Resolve(VersionDetail detail)
    {
        var declared = detail.Logging?.Client;
        if (declared != null) Validate(declared);
        // Mojang's current client manifests retain old Log4j libraries, but ship
        // different mitigations: a RegexFilter for beta9 and %msg{nolookups} for 2.8+.
        // Never rely solely on formatMsgNoLookups; beta9 does not implement it.
        var cores = (detail.Libraries ?? new()).Select(library => library.Name ?? "")
            .Where(name => name.StartsWith(CorePrefix, StringComparison.Ordinal))
            .Select(name => name[CorePrefix.Length..].Split(':')[0]);
        Version? oldest = null;
        foreach (var core in cores)
        {
            var numeric = core.Split('-', 2)[0];
            if (!Version.TryParse(numeric, out var version))
                throw new InvalidDataException("无法确认 Log4j 版本安全性，请更新实例的依赖元数据。");
            if (version.Major == 2 && version < new Version(2, 17, 1) && (oldest == null || version < oldest))
                oldest = version;
        }
        if (oldest != null) return Official(oldest < new Version(2, 7) ? "client-1.7.xml" : "client-1.12.xml");
        if (declared?.File?.Id is "client-1.7.xml" or "client-1.12.xml")
            return Official(declared.File.Id);
        return declared;
    }

    private static LoggingClientConfiguration Official(string id)
    {
        var legacy = id == "client-1.7.xml";
        var sha1 = legacy ? "50c9cc4af6d853d9fc137c84bcd153e2bd3a9a82" : "bd65e7d2e3c237be76cfbef4c2405033d7f91521";
        return new LoggingClientConfiguration
        {
            Argument = ArgumentTemplate, Type = "log4j2-xml",
            File = new LoggingFile
            {
                Id = id, Sha1 = sha1, Size = legacy ? 966 : 888,
                Url = "https://" + (legacy ? "launcher.mojang.com" : "piston-data.mojang.com") + "/v1/objects/" + sha1 + "/" + id
            }
        };
    }

    internal static void Validate(LoggingClientConfiguration config)
    {
        var file = config.File;
        if (config.Type != "log4j2-xml" || config.Argument != ArgumentTemplate || file == null ||
            string.IsNullOrWhiteSpace(file.Id) || !file.Id.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            file.Sha1 == null || file.Sha1.Length != 40 || !file.Sha1.All(Uri.IsHexDigit) || file.Size is <= 0 or > 4 * 1024 * 1024 ||
            !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort ||
            uri.Host is not ("launcher.mojang.com" or "launchermeta.mojang.com" or "piston-data.mojang.com" or "piston-meta.mojang.com"))
            throw new InvalidDataException("日志安全配置清单无效，已停止启动或下载。");
    }

    internal static string PathFor(string minecraftDir, LoggingClientConfiguration config)
    {
        Validate(config);
        // Hash-specific paths avoid one profile replacing another profile's verified configuration.
        return ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "assets", "log_configs", config.File!.Sha1.ToLowerInvariant()), config.File.Id);
    }

    internal static bool IsVerified(string path, LoggingClientConfiguration config)
    {
        ArchiveImportSafety.CheckLinks(path);
        if (!File.Exists(path)) return false;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return input.Length == config.File!.Size &&
            Convert.ToHexString(SHA1.HashData(input)).Equals(config.File.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ApplyArguments(List<string> jvmArguments, string path)
    {
        jvmArguments.RemoveAll(IsProtectedArgument);
        jvmArguments.Add("-Dlog4j.configurationFile=" + Path.GetFullPath(path));
        jvmArguments.Add("-Dlog4j2.formatMsgNoLookups=true");
    }

    internal static bool IsProtectedArgument(string argument) =>
        new[] { "-Dlog4j.configurationFile", "-Dlog4j2.configurationFile", "-Dlog4j2.formatMsgNoLookups" }
            .Any(key => argument.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
}
