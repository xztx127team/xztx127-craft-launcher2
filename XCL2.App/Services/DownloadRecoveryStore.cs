using System.IO;
using System.Text.Json;

namespace XCL2.App.Services;

public sealed record DownloadRecoveryRequest(string Folder, string VersionId, string VersionUrl, string VersionType = "release")
{
    public bool IsAllowed(IEnumerable<string> configuredFolders, bool requireDirectory = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(VersionId) || VersionId.Length > 200 || VersionId is "." or ".."
                || VersionId.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0
                || VersionId.EndsWith('.') || VersionId.EndsWith(' ')) return false;
            if (VersionUrl.Length > 4096 || !Uri.TryCreate(VersionUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
            var folder = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return (!requireDirectory || Directory.Exists(folder)) && configuredFolders.Any(known =>
                string.Equals(Path.GetFullPath(known).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), folder,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        catch { return false; }
    }
}
public sealed record DownloadRecoveryRecord(Guid Id, string Name, DownloadRecoveryRequest Request);

/// <summary>每个进程独立保存任务；活跃会话的独占锁阻止多开启动器抢走它的任务。</summary>
public sealed class DownloadRecoveryStore : IDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private readonly FileStream _lease;
    private readonly object _gate = new();
    private bool _disposed;
    private byte[]? _lastSaved;
    public DownloadRecoveryStore(string directory)
    {
        _directory = directory; Directory.CreateDirectory(directory);
        var session = Guid.NewGuid().ToString("N");
        _path = Path.Combine(directory, session + ".json");
        _lease = new FileStream(Path.ChangeExtension(_path, ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }
    public void Save(IReadOnlyList<DownloadRecoveryRecord> records)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var data = JsonSerializer.SerializeToUtf8Bytes(records.Take(100).ToArray());
            if (_lastSaved != null && data.AsSpan().SequenceEqual(_lastSaved) && File.Exists(_path)) return;
            var temporary = _path + ".tmp";
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { output.Write(data); output.Flush(true); }
            File.Move(temporary, _path, true);
            _lastSaved = data;
        }
    }
    /// <summary>accept 必须先成功保存到本会话，再移除旧任务记录；不会执行任何任务。</summary>
    public void ImportPending(Action<IReadOnlyList<DownloadRecoveryRecord>> accept)
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Take(200).ToArray())
        {
            if (path == _path) continue;
            try
            {
                using var lease = new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                if (new FileInfo(path).Length > 512 * 1024) continue;
                var records = JsonSerializer.Deserialize<List<DownloadRecoveryRecord>>(File.ReadAllBytes(path));
                accept(records?.Where(record => record?.Request != null && record.Name != null).Take(100).ToArray()
                    ?? Array.Empty<DownloadRecoveryRecord>());
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lease.Dispose();
            try { File.Delete(Path.ChangeExtension(_path, ".lock")); } catch { }
        }
    }
}
