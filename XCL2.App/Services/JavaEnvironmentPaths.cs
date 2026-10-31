using System.IO;

namespace XCL2.App.Services;

/// <summary>Windows PATH/JAVA_HOME values may contain quotes and expandable variables.</summary>
internal static class JavaEnvironmentPaths
{
    internal static string? SelectRuntime(IEnumerable<(string Path, int? Major, string? Architecture)> candidates,
        int requiredMajor, string requiredArchitecture)
        => candidates.Where(candidate => candidate.Major == requiredMajor && string.Equals(candidate.Architecture, requiredArchitecture, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Path).FirstOrDefault();

    internal static string ReadSearchPath()
    {
        var values = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        // A launcher already running does not inherit PATH changes made by a new
        // Java installer. Refresh also reads the current persisted Windows values.
        if (OperatingSystem.IsWindows())
        {
            foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                try { values.Add(Environment.GetEnvironmentVariable("PATH", scope)); }
                catch (System.Security.SecurityException) { }
            }
        }
        return string.Join(";", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    internal static string? NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        path = Environment.ExpandEnvironmentVariables(path).Trim();
        // Empty PATH entries mean the current directory to Windows. Do not execute
        // an arbitrary javaw.exe found there as a consequence of a doubled semicolon.
        return path.Length == 0 || path.Contains('"') || !Path.IsPathFullyQualified(path) ? null : path;
    }

    internal static IEnumerable<string> FindExecutables(string? pathValue)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (pathValue ?? "").Split(';'))
        {
            var directory = NormalizeDirectory(entry);
            if (directory == null) continue;
            var executable = Path.Combine(directory, "javaw.exe");
            if (seen.Add(executable) && File.Exists(executable)) yield return executable;
        }
    }
}
