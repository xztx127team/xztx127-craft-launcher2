using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XCL2.App.Services;

/// <summary>All file download paths share ownership through verification and final commit.</summary>
internal static class DownloadTargetLock
{
    private sealed class Entry { internal readonly SemaphoreSlim Gate = new(1, 1); internal int Users; }
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    internal static async Task<IDisposable> AcquireAsync(string targetPath, CancellationToken ct)
    {
        var key = Path.GetFullPath(targetPath);
        ArchiveImportSafety.CheckLinks(key);
        Entry entry;
        lock (Sync)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new Entry());
            entry.Users++;
        }
        try { await entry.Gate.WaitAsync(ct).ConfigureAwait(false); }
        catch { ReleaseReference(key, entry); throw; }
        try
        {
            var directory = Path.GetDirectoryName(key)!;
            ArchiveImportSafety.CheckLinks(key);
            Directory.CreateDirectory(directory);
            ArchiveImportSafety.CheckLinks(directory);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())));
            var lockPath = Path.Combine(directory, ".xcl-download-" + digest + ".lock");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Keep the inode/name: deleting a lock after closing can split ownership
                    // between two processes on platforms that allow unlinking an open file.
                    ArchiveImportSafety.CheckLinks(lockPath);
                    var file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new Lease(key, entry, file);
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
                { await Task.Delay(100, ct).ConfigureAwait(false); }
            }
        }
        catch { entry.Gate.Release(); ReleaseReference(key, entry); throw; }
    }

    internal static async Task WriteTextAsync(string targetPath, string contents, CancellationToken ct)
    {
        using var ownership = await AcquireAsync(targetPath, ct).ConfigureAwait(false);
        var temporary = targetPath + ".metadata-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, contents, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            ArchiveImportSafety.CheckLinks(targetPath);
            File.Move(temporary, targetPath, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static void ReleaseReference(string key, Entry entry)
    {
        lock (Sync)
        {
            if (--entry.Users != 0) return;
            Entries.Remove(key);
            entry.Gate.Dispose();
        }
    }
    private sealed class Lease(string key, Entry entry, FileStream file) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { file.Dispose(); }
            finally { entry.Gate.Release(); ReleaseReference(key, entry); }
        }
    }
}
