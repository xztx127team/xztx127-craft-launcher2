using System.IO;

namespace XCL2.App.Services;

internal static class JavaRuntimeCompatibility
{
    // JEP 400 (JDK 18+) defines COMPAT as the pre-JDK-18 native default charset.
    // LWJGL 2 native libraries can pass filenames using that native code page;
    // forcing UTF-8 breaks OpenAL/native loading in non-ASCII Windows paths.
    internal static string FileEncodingArgument(string javaPath, IEnumerable<string> libraries)
        => ReadMajorFromRelease(javaPath) is >= 18 && libraries.Any(name =>
            name.StartsWith("org.lwjgl.lwjgl:lwjgl:2.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("org.lwjgl:lwjgl:2.", StringComparison.OrdinalIgnoreCase))
            ? "-Dfile.encoding=COMPAT" : "-Dfile.encoding=UTF-8";

    internal static int? ReadMajorFromRelease(string javaPath)
    {
        try
        {
            var executable = new FileInfo(Path.GetFullPath(javaPath));
            // Oracle's javapath and similar shims can be file symlinks outside
            // the runtime home. Read release metadata beside the real target.
            var actualPath = executable.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? executable.FullName;
            var bin = Path.GetDirectoryName(actualPath);
            if (bin == null) return null;
            var binDirectory = new DirectoryInfo(bin);
            bin = binDirectory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? binDirectory.FullName;
            var home = Directory.GetParent(bin)?.FullName;
            if (home == null) return null;
            using var reader = new StreamReader(Path.Combine(home, "release"));
            // Runtime release metadata is small; never load an arbitrary file whole.
            var buffer = new char[16 * 1024];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            foreach (var line in new string(buffer, 0, count).Split('\n'))
            {
                var value = line.Trim();
                const string prefix = "JAVA_VERSION=\"";
                if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith('"'))
                    return JavaVersionOutputPolicy.ParseMajorValue(value[prefix.Length..^1]);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return null;
    }
}
