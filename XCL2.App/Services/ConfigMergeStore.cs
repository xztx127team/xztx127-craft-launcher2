using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XCL2.App.Services;

/// <summary>跨进程配置提交。只提交本实例真实修改的字段；冲突显式失败，旧文件保留。</summary>
internal static class ConfigMergeStore
{
    private readonly record struct Value(bool Exists, JsonNode? Node);
    private static bool Same(Value a, Value b) => a.Exists == b.Exists && JsonNode.DeepEquals(a.Node, b.Node);
    private static Value At(JsonObject? obj, string key) => obj != null && obj.TryGetPropertyValue(key, out var value)
        ? new(true, value) : new(false, null);

    internal static string Merge(string previousLocal, string previousDisk, string local, string disk)
    {
        static JsonObject ParseObject(string json) => JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("配置必须是 JSON 对象。");
        var result = MergeValue(new(true, ParseObject(previousLocal)), new(true, ParseObject(previousDisk)),
            new(true, ParseObject(local)), new(true, ParseObject(disk)), "设置");
        return result.Node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static Value MergeValue(Value beforeLocal, Value beforeDisk, Value local, Value disk, string path)
    {
        if (Same(local, beforeLocal)) return new(disk.Exists, disk.Node?.DeepClone());
        // Even an unchanged disk object may contain fields unknown to this version's serializer.
        // Merge its children before taking the stable-disk fast path, or an ordinary save would
        // delete every unknown extension property absent from both local snapshots.
        if (local.Node is JsonObject localObject && disk.Node is JsonObject diskObject &&
            beforeLocal.Node is JsonObject oldLocal && beforeDisk.Node is JsonObject oldDisk)
        {
            var result = new JsonObject();
            foreach (var key in localObject.Select(p => p.Key).Concat(diskObject.Select(p => p.Key))
                         .Concat(oldLocal.Select(p => p.Key)).Concat(oldDisk.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
            {
                var merged = MergeValue(At(oldLocal, key), At(oldDisk, key), At(localObject, key), At(diskObject, key), path + "." + key);
                if (merged.Exists) result[key] = merged.Node;
            }
            return new(true, result);
        }
        if (Same(disk, beforeDisk) || Same(local, disk)) return new(local.Exists, local.Node?.DeepClone());
        throw new IOException($"另一份启动器已修改“{path}”，本次未覆盖磁盘设置。请重启此启动器实例，重新读取设置后再修改。");
    }

    internal static string Commit(string path, string local, string previousLocal, string previousDisk, bool recoverInvalid = false,
        Action<string>? publishMirror = null, Action<Exception>? onMirrorError = null, Action<string>? validate = null)
    {
        var full = Path.GetFullPath(path);
        var identity = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        using var gate = new Mutex(false, "XCL2.Config." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
        var acquired = false;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一份启动器正在保存设置，请稍后重试。本次没有覆盖配置。");
            string disk;
            if (!File.Exists(full)) disk = previousDisk;
            else
            {
                disk = File.ReadAllText(full);
                try
                {
                    if (JsonNode.Parse(disk) is not JsonObject) throw new JsonException("配置必须是对象。");
                    // Load may have recovered a mirror because valid JSON contains a bad
                    // known field type. Preserve that unusable primary too, then restore
                    // the already loaded snapshot instead of making every save fail forever.
                    if (recoverInvalid) validate?.Invoke(disk);
                }
                catch (JsonException) when (recoverInvalid)
                {
                    File.Copy(full, full + ".corrupt-" + Guid.NewGuid().ToString("N"), false);
                    disk = previousDisk;
                }
            }
            var merged = Merge(previousLocal, previousDisk, local, disk);
            // Schema validation belongs before the durable commit. A JSON object with an invalid
            // known field type must not silently poison the primary configuration on an unrelated save.
            validate?.Invoke(merged);
            WriteAtomically(full, merged);
            // A primary commit remains successful if an optional mirror is unwritable or a
            // mirror observer fails. Callers can advance snapshots and notify Saved exactly once.
            try { publishMirror?.Invoke(merged); }
            catch (Exception ex)
            {
                try { onMirrorError?.Invoke(ex); } catch { /* A diagnostic observer cannot undo a commit. */ }
            }
            return merged;
        }
        finally { if (acquired) gate.ReleaseMutex(); }
    }

    internal static void WriteAtomically(string path, string content)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                file.Write(bytes); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }
}
