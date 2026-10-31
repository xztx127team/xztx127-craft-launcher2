using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XCL2.App.Services;

/// <summary>
/// Replaces only the selected instance's launch profile and client. User files and parent
/// versions are never moved or deleted. A durable journal permits recovery after a process exit.
/// </summary>
internal static class ComponentProfileTransaction
{
    private sealed class Journal
    {
        public Journal() { }
        public string VersionId { get; set; } = "";
        public string OriginalProfile { get; set; } = "";
        public bool Committed { get; set; }
        public bool IncludesNatives { get; set; }
        public bool NativesExisted { get; set; }
        public List<Entry> Files { get; set; } = new();
    }
    private sealed class Entry
    {
        public Entry() { }
        public string Name { get; set; } = "";
        public bool Existed { get; set; }
    }

    internal static string VersionDirectory(string minecraftRoot, string versionId)
    {
        ValidateName(versionId);
        return ArchiveImportSafety.Resolve(minecraftRoot, "versions/" + versionId);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("实例名称无效，无法修改版本文件。");
        // Resolve validates Windows device names, trailing periods, colons and control characters.
        _ = ArchiveImportSafety.Resolve(Path.GetTempPath(), name);
    }

    private static string JournalDirectory(string root, string versionId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionId.ToUpperInvariant())));
        return ArchiveImportSafety.Resolve(root, ".xcl-component-transactions/" + key);
    }

    internal static bool HasRecoveryRecord(string root, string versionId)
    {
        _ = VersionDirectory(root, versionId);
        var path = ArchiveImportSafety.Resolve(JournalDirectory(root, versionId), "journal.json");
        try
        {
            // Unlike File.Exists, do not mistake access errors or a directory in place of
            // the record for the absence of a recovery transaction.
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new InvalidDataException("组件修改的恢复记录不是文件，请先检查恢复目录。");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static string? FindProfile(string directory, string id)
    {
        var exact = ArchiveImportSafety.Resolve(directory, id + ".json");
        if (File.Exists(exact)) return exact;
        var matches = Directory.EnumerateFiles(directory, "*.json").Where(path =>
        {
            ArchiveImportSafety.CheckLinks(path);
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                return json?["id"] is JsonValue && (json["mainClass"] is JsonValue || json["inheritsFrom"] is JsonValue);
            }
            catch (JsonException) { return false; }
        }).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static void Recover(string root, string versionId)
    {
        var target = VersionDirectory(root, versionId);
        var journalDirectory = JournalDirectory(root, versionId);
        if (!HasRecoveryRecord(root, versionId)) return;
        var path = Path.Combine(journalDirectory, "journal.json");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path))
            ?? throw new InvalidDataException("组件修改的恢复记录损坏，未继续写入实例。");
        if (!string.Equals(journal.VersionId, versionId, StringComparison.OrdinalIgnoreCase) || journal.Files.Count is < 2 or > 4)
            throw new InvalidDataException("组件修改的恢复记录与当前实例不匹配。");
        if (!journal.Committed)
        {
            ValidateRecoveryJournal(journalDirectory, journal);
            Rollback(target, journalDirectory, journal);
            journal.Committed = true; // A completed rollback is also a stable, launchable terminal state.
            WriteJournal(journalDirectory, journal);
        }
        TryDeleteDirectory(journalDirectory);
    }

    internal static void Commit(string root, string versionId, string preparedJson, string preparedJar,
        CancellationToken ct = default, Action<int>? afterMutation = null, string? preparedNatives = null)
    {
        var target = VersionDirectory(root, versionId);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("原实例已被移走，未应用组件修改。");
        Recover(root, versionId);
        ArchiveImportSafety.CheckLinks(preparedJson);
        ArchiveImportSafety.CheckLinks(preparedJar);
        if (!File.Exists(preparedJar) || new FileInfo(preparedJar).Length == 0)
            throw new InvalidDataException("新版本没有完整的客户端文件，原实例未修改。");
        var profile = JsonNode.Parse(File.ReadAllText(preparedJson)) as JsonObject
            ?? throw new InvalidDataException("新版本信息无效。");
        if (profile["id"]?.GetValue<string>() != versionId || string.IsNullOrWhiteSpace(profile["mainClass"]?.GetValue<string>()) ||
            profile["inheritsFrom"] != null || profile["jar"] != null)
            throw new InvalidDataException("新版本尚未转为独立实例，原实例未修改。");

        var oldProfile = FindProfile(target, versionId)
            ?? throw new InvalidDataException("找不到唯一的原版本描述文件，请先补全文件。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { versionId + ".json", versionId + ".jar", Path.GetFileName(oldProfile) };
        // Only an explicitly named old client belongs to this operation. Other JSON/JAR files
        // may be modpack manifests, custom configuration or installers and must remain intact.
        var old = JsonNode.Parse(File.ReadAllText(oldProfile)) as JsonObject;
        if (old?["id"]?.GetValue<string>() is { Length: > 0 } oldId)
        {
            ValidateName(oldId);
            if (File.Exists(ArchiveImportSafety.Resolve(target, oldId + ".jar"))) names.Add(oldId + ".jar");
        }
        var journalDirectory = JournalDirectory(root, versionId);
        if (Directory.Exists(journalDirectory)) Directory.Delete(journalDirectory, recursive: true);
        Directory.CreateDirectory(journalDirectory);
        Directory.CreateDirectory(Path.Combine(journalDirectory, "original"));
        var journal = new Journal { VersionId = versionId, OriginalProfile = Path.GetFileName(oldProfile),
            IncludesNatives = preparedNatives != null,
            NativesExisted = Directory.Exists(ArchiveImportSafety.Resolve(target, "natives")) };
        try
        {
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                var file = ArchiveImportSafety.Resolve(target, name);
                var exists = File.Exists(file);
                if (exists)
                {
                    var snapshot = ArchiveImportSafety.Resolve(journalDirectory, "original/" + name);
                    File.Copy(file, snapshot, overwrite: false);
                    using var stream = new FileStream(snapshot, FileMode.Open, FileAccess.Write, FileShare.None);
                    stream.Flush(true);
                }
                journal.Files.Add(new Entry { Name = name, Existed = exists });
            }
            if (journal.IncludesNatives)
            {
                if (!Directory.Exists(preparedNatives)) throw new DirectoryNotFoundException("新版本的本机库暂存目录缺失。");
                if (journal.NativesExisted) CopyTree(Path.Combine(target, "natives"), Path.Combine(journalDirectory, "original-natives"), ct);
                CopyTree(preparedNatives!, Path.Combine(journalDirectory, "prepared-natives"), ct);
            }
            WriteJournal(journalDirectory, journal);
            int mutations = 0;
            ct.ThrowIfCancellationRequested();
            if (journal.IncludesNatives)
            {
                var nativeTarget = ArchiveImportSafety.Resolve(target, "natives");
                if (Directory.Exists(nativeTarget)) Directory.Delete(nativeTarget, recursive: true);
                Directory.Move(Path.Combine(journalDirectory, "prepared-natives"), nativeTarget);
                afterMutation?.Invoke(++mutations);
            }
            ct.ThrowIfCancellationRequested();
            ReplaceFile(preparedJar, ArchiveImportSafety.Resolve(target, versionId + ".jar"));
            afterMutation?.Invoke(++mutations);
            ct.ThrowIfCancellationRequested();
            ReplaceFile(preparedJson, ArchiveImportSafety.Resolve(target, versionId + ".json"));
            afterMutation?.Invoke(++mutations);
            foreach (var name in names.Where(name => !name.Equals(versionId + ".json", StringComparison.OrdinalIgnoreCase) &&
                                                     !name.Equals(versionId + ".jar", StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();
                File.Delete(ArchiveImportSafety.Resolve(target, name));
                afterMutation?.Invoke(++mutations);
            }
            ct.ThrowIfCancellationRequested();
            journal.Committed = true;
            WriteJournal(journalDirectory, journal);
        }
        catch (Exception original)
        {
            if (File.Exists(Path.Combine(journalDirectory, "journal.json")))
            {
                try
                {
                    Rollback(target, journalDirectory, journal);
                    journal.Committed = true;
                    WriteJournal(journalDirectory, journal);
                }
                catch (Exception rollback)
                {
                    throw new IOException("组件更新未完成，自动恢复暂时失败。恢复文件保留在：" + journalDirectory +
                        "。请释放被占用文件后重新打开“版本与组件”以继续恢复。", new AggregateException(original, rollback));
                }
            }
            TryDeleteDirectory(journalDirectory);
            throw;
        }
        TryDeleteDirectory(journalDirectory);
    }

    private static void Rollback(string target, string journalDirectory, Journal journal)
    {
        if (journal.IncludesNatives)
        {
            var natives = ArchiveImportSafety.Resolve(target, "natives");
            if (Directory.Exists(natives)) Directory.Delete(natives, recursive: true);
            if (journal.NativesExisted) CopyTree(Path.Combine(journalDirectory, "original-natives"), natives, CancellationToken.None);
        }
        // Restore the client before publishing its matching profile. Cancellation never interrupts rollback.
        foreach (var entry in journal.Files.OrderBy(entry => entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var destination = ArchiveImportSafety.Resolve(target, entry.Name);
            if (entry.Existed) ReplaceFile(ArchiveImportSafety.Resolve(journalDirectory, "original/" + entry.Name), destination);
            else File.Delete(destination);
        }
    }

    private static void ValidateRecoveryJournal(string directory, Journal journal)
    {
        ValidateName(journal.OriginalProfile);
        if (!journal.OriginalProfile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("组件恢复记录中的原版本文件无效。");
        var original = JsonNode.Parse(File.ReadAllText(ArchiveImportSafety.Resolve(directory, "original/" + journal.OriginalProfile))) as JsonObject;
        if (original?["id"]?.GetValue<string>() is not { Length: > 0 } originalId ||
            (original["mainClass"] is not JsonValue && original["inheritsFrom"] is not JsonValue))
            throw new InvalidDataException("组件恢复快照不是有效版本描述文件，未覆盖实例文件。");
        ValidateName(originalId);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { journal.VersionId + ".json", journal.VersionId + ".jar", journal.OriginalProfile, originalId + ".jar" };
        if (journal.Files.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count ||
            !journal.Files.Any(entry => entry.Name == journal.OriginalProfile && entry.Existed) ||
            journal.Files.Any(entry => !allowed.Contains(entry.Name)))
            throw new InvalidDataException("组件恢复记录包含非版本文件，未执行恢复。");
        foreach (var entry in journal.Files)
        {
            ValidateName(entry.Name);
            if (entry.Existed && !File.Exists(ArchiveImportSafety.Resolve(directory, "original/" + entry.Name)))
                throw new InvalidDataException("组件恢复快照不完整，未执行恢复。");
        }
        if (journal.IncludesNatives && journal.NativesExisted && !Directory.Exists(Path.Combine(directory, "original-natives")))
            throw new InvalidDataException("组件恢复快照缺少本机库，未执行恢复。");
    }

    private static void CopyTree(string source, string destination, CancellationToken ct)
    {
        ArchiveImportSafety.CheckLinks(source);
        ArchiveImportSafety.CheckLinks(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var copy = ArchiveImportSafety.Resolve(destination, Path.GetFileName(file));
            ArchiveImportSafety.CheckLinks(file);
            File.Copy(file, copy, overwrite: false);
            using var stream = new FileStream(copy, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Flush(true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyTree(directory, ArchiveImportSafety.Resolve(destination, Path.GetFileName(directory)), ct);
    }

    private static void WriteJournal(string directory, Journal journal)
    {
        var temporary = Path.Combine(directory, "journal.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal));
        using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None)) stream.Flush(true);
        File.Move(temporary, Path.Combine(directory, "journal.json"), overwrite: true);
    }

    internal static void ReplaceFile(string source, string destination)
    {
        ArchiveImportSafety.CheckLinks(source);
        ArchiveImportSafety.CheckLinks(destination);
        var temporary = destination + ".xcl-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None)) stream.Flush(true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void TryDeleteDirectory(string directory)
    {
        try { ArchiveImportSafety.CheckLinks(directory); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
