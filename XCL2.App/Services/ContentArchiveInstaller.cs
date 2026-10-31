using System.IO;
using System.IO.Compression;

namespace XCL2.App.Services;

internal static class ContentArchiveInstaller
{
    internal static void Merge(string zipPath, string destination, Func<ZipArchiveEntry, string, bool>? preserve = null,
        CancellationToken ct = default)
    {
        ArchiveImportSafety.CheckLinks(zipPath);
        using var archive = ZipFile.OpenRead(zipPath);
        ArchiveImportSafety.ValidateArchive(archive, destination);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var relative = entry.FullName.Replace('\\', '/');
            var target = ArchiveImportSafety.Resolve(destination, relative);
            if (preserve?.Invoke(entry, target) == true) continue;
            ArchiveImportSafety.Extract(entry, destination, relative, ct);
        }
    }

    internal static void ExtractNew(string zipPath, string destination)
    {
        ArchiveImportSafety.CheckLinks(zipPath);
        using (var archive = ZipFile.OpenRead(zipPath))
            ArchiveImportSafety.ValidateArchive(archive, destination);
        ArchiveImportSafety.CreateNewDirectory(destination);
        try { Merge(zipPath, destination); }
        catch
        {
            ArchiveImportSafety.CheckLinks(destination);
            try { Directory.Delete(destination, recursive: true); } catch (IOException) { }
            throw;
        }
    }
}
