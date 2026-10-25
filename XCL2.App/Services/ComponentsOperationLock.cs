using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Cross-process shared launch leases and exclusive maintenance leases for each physical instance.</summary>
public static class ComponentsOperationLock
{
    public sealed class Lease : IDisposable
    {
        private List<FileStream>? _handles;
        internal Lease(List<FileStream> handles) => _handles = handles;

        /// <summary>Transfers ownership to a running process; the surrounding using block can now safely end.</summary>
        public void TransferTo(Process process)
        {
            ArgumentNullException.ThrowIfNull(process);
            var handles = Interlocked.Exchange(ref _handles, null) ?? throw new ObjectDisposedException(nameof(Lease));
            int released = 0;
            EventHandler? onExit = null;
            void Release()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                try { if (onExit != null) process.Exited -= onExit; } catch (InvalidOperationException) { }
                CloseHandles(handles);
            }
            onExit = (_, _) => Release();
            try
            {
                process.Exited += onExit;
                process.EnableRaisingEvents = true;
                if (process.HasExited) Release();
            }
            catch { Release(); throw; }
        }
        public void Dispose()
        {
            var handles = Interlocked.Exchange(ref _handles, null);
            if (handles != null) CloseHandles(handles);
        }
        private static void CloseHandles(IEnumerable<FileStream> handles)
        {
            foreach (var handle in handles)
                try { handle.Dispose(); } catch (IOException) { }
        }
    }

    public static Lease AcquireForLaunch(string minecraftRoot, string versionId)
        => AcquireForLaunch(minecraftRoot, versionId, DefaultLockDirectory());

    internal static Lease AcquireForLaunch(string minecraftRoot, string versionId, string lockDirectory)
    {
        var handles = new List<FileStream>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            void Acquire(string id)
            {
                ValidateId(id);
                if (!visited.Add(id)) return;
                if (visited.Count > 64) throw new InvalidDataException("版本继承超过 64 层。");
                handles.Add(OpenLock(minecraftRoot, id, maintenance: false, lockDirectory));
                DemandNoPendingTransaction(minecraftRoot, id);
                var directory = Path.Combine(minecraftRoot, "versions", id);
                var profile = FolderService.ResolveVersionJson(directory, id);
                if (profile == null) return; // Startup repair may still be preparing this instance.
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(profile));
                    foreach (var property in new[] { "inheritsFrom", "jar" })
                        if (json.RootElement.TryGetProperty(property, out var reference) && reference.ValueKind == JsonValueKind.String &&
                            reference.GetString() is { Length: > 0 } parent) Acquire(parent);
                }
                catch (JsonException) { /* Launch validation will report malformed metadata after repair has had its chance. */ }
            }
            Acquire(versionId);
            return new Lease(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    public static Lease AcquireForMaintenance(string minecraftRoot, string versionId)
        => AcquireForMaintenance(minecraftRoot, versionId, DefaultLockDirectory());

    internal static Lease AcquireForMaintenance(string minecraftRoot, string versionId, string lockDirectory)
    {
        ValidateId(versionId);
        return new Lease(new List<FileStream> { OpenLock(minecraftRoot, versionId, maintenance: true, lockDirectory) });
    }

    private static string DefaultLockDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("无法找到本机应用数据目录，暂时不能锁定实例。");
        return Path.Combine(local, "XCL2", "InstanceLocks");
    }

    private static FileStream OpenLock(string root, string id, bool maintenance, string lockDirectory)
    {
        var canonical = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "\n" + id.ToUpperInvariant())));
        Directory.CreateDirectory(lockDirectory);
        try
        {
            return new FileStream(Path.Combine(lockDirectory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                maintenance ? FileShare.None : FileShare.ReadWrite);
        }
        catch (IOException ex)
        {
            throw new IOException(maintenance ? "这个实例正在启动、运行或由另一维护任务使用，请结束后再修改组件。" :
                "这个实例或它依赖的父版本正在修改组件，请等待维护完成后再启动。", ex);
        }
    }

    private static void DemandNoPendingTransaction(string root, string id)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToUpperInvariant())));
        var journal = Path.Combine(root, ".xcl-component-transactions", key, "journal.json");
        if (!File.Exists(journal)) return;
        bool committed;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(journal));
            committed = document.RootElement.TryGetProperty("Committed", out var state) && state.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { throw new IOException("实例留有未完成的组件恢复记录。请先打开“版本与组件”恢复后再启动。", ex); }
        if (!committed) throw new IOException("实例的上次组件修改尚未安全结束。请先打开“版本与组件”完成自动恢复，再启动游戏。");
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(new[] { '/', '\\', ':', '<', '>', '|', '"', '?', '*' }) >= 0 ||
            id.Any(character => character < 32)) throw new InvalidDataException("无法锁定无效的实例名称。");
    }
}
