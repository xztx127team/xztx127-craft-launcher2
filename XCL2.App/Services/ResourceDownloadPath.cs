using System.IO;

namespace XCL2.App.Services;

/// <summary>Remote filenames and selected save names must stay within their selected directory.</summary>
internal static class ResourceDownloadPath
{
    internal static string ResolveChild(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("资源文件名或存档名包含路径分隔符，已停止下载。");
        return ArchiveImportSafety.Resolve(directory, name);
    }
}
