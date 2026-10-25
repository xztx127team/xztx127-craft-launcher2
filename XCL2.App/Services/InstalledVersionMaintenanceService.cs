using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed record InstalledVersionChange(string MinecraftVersion, ServerCoreType Loader,
    string? LoaderVersion = null, string? JavaPath = null, string? OptiFineIdentifier = null,
    bool ReplaceCompositeComponents = false);

/// <summary>Installs into a separate game root, validates it, then changes only the target profile/client.</summary>
public sealed class InstalledVersionMaintenanceService
{
    private readonly AppConfig _config;
    public InstalledVersionMaintenanceService(AppConfig config) => _config = config;

    public static async Task RecoverAsync(string minecraftRoot, string versionId, Func<Task> ensureIdle, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Viewing an intact instance is read-only, even while its game holds a launch
        // lease. Only an actual recovery is a maintenance operation.
        if (!ComponentProfileTransaction.HasRecoveryRecord(minecraftRoot, versionId)) return;
        using var instanceLock = AcquireInstanceLock(minecraftRoot, versionId);
        if (!ComponentProfileTransaction.HasRecoveryRecord(minecraftRoot, versionId)) return;
        await ensureIdle();
        await Task.Run(() => ComponentProfileTransaction.Recover(minecraftRoot, versionId), ct);
    }

    private static ComponentsOperationLock.Lease AcquireInstanceLock(string minecraftRoot, string versionId)
        => ComponentsOperationLock.AcquireForMaintenance(minecraftRoot, versionId);

    public async Task<string> ApplyAsync(string minecraftRoot, string versionId, InstalledVersionChange change,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default, Func<Task>? ensureIdle = null,
        Action? beforeCommit = null)
    {
        minecraftRoot = Path.GetFullPath(minecraftRoot);
        var targetDirectory = ComponentProfileTransaction.VersionDirectory(minecraftRoot, versionId);
        if (!Directory.Exists(targetDirectory)) throw new DirectoryNotFoundException("找不到要修改的已安装实例。");
        using var instanceLock = AcquireInstanceLock(minecraftRoot, versionId);
        if (ensureIdle != null) await ensureIdle();
        await Task.Run(() => ComponentProfileTransaction.Recover(minecraftRoot, versionId), ct);
        var originalProfiles = await Task.Run(() => InstalledComponentInventory.ReadProfiles(minecraftRoot, versionId), ct);
        var inventory = InstalledComponentInventory.FromProfiles(originalProfiles);
        if (InstalledComponentInventory.NeedsCompositeConfirmation(inventory) && !change.ReplaceCompositeComponents)
            throw new InvalidOperationException("当前实例包含组合或其他组件。请先在版本与组件窗口确认将全部现有启动组件替换为所选组件。");
        void CheckDependents()
        {
            var current = VersionInfoResolver.Resolve(targetDirectory, versionId, null);
            if (change.Loader == ServerCoreType.Vanilla && current.ModLoader == null && inventory.Count == 0 &&
                current.McVersion == change.MinecraftVersion) return;
            var dependents = InstalledComponentInventory.DirectDependents(minecraftRoot, versionId);
            if (dependents.Count > 0)
                throw new InvalidOperationException("这个版本仍被其他实例作为底座使用：" + string.Join("、", dependents) +
                    "。直接更换 Minecraft 或加载器会破坏这些实例；请先解除它们的继承依赖，或用“补全文件”修复当前底座。");
        }
        await Task.Run(CheckDependents, ct);
        var before = await Task.Run(() => Fingerprint(targetDirectory, versionId), ct);
        if (ensureIdle != null) await ensureIdle();

        var stageRoot = ArchiveImportSafety.Resolve(minecraftRoot, ".xcl-component-work/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageRoot);
        var preserveStage = false;
        try
        {
            using var downloader = DownloadService.CreateFromConfig(_config);
            using var loaders = new ClientLoaderInstallService(_config);
            progress?.Report(new ProgressInfo("校验 Minecraft 与组件版本", 0, 1, change.MinecraftVersion));
            var manifest = await downloader.GetVersionManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(item => item.Id == change.MinecraftVersion)
                ?? throw new InvalidOperationException("官方版本清单中没有所选 Minecraft 版本，请刷新列表后重试。");
            await ValidateChoiceAsync(loaders, change, ct);
            // Installers never see the user's actual game root; even their profile creation,
            // defaults, cleanup and parent version writes are confined to this staging root.
            await downloader.InstallVersionAsync(stageRoot, entry, progress, ct);
            var stageId = change.Loader switch
            {
                ServerCoreType.Vanilla => entry.Id,
                ServerCoreType.Fabric => await loaders.InstallFabricClientAsync(stageRoot, entry.Id, change.LoaderVersion!, progress, ct),
                ServerCoreType.LegacyFabric => await loaders.InstallLegacyFabricClientAsync(stageRoot, entry.Id, change.LoaderVersion!, progress, ct),
                ServerCoreType.Quilt => await loaders.InstallQuiltClientAsync(stageRoot, entry.Id, change.LoaderVersion!, progress, ct),
                ServerCoreType.Forge or ServerCoreType.NeoForge => await loaders.InstallForgeOrNeoForgeClientAsync(
                    stageRoot, change.Loader, change.LoaderVersion!, change.JavaPath!, progress, ct),
                ServerCoreType.OptiFine => await loaders.InstallOptiFineClientAsync(stageRoot, entry.Id,
                    change.OptiFineIdentifier!.Split('|')[0], change.OptiFineIdentifier.Split('|')[1], change.JavaPath!, progress, ct),
                _ => throw new InvalidOperationException("不支持这个客户端组件。")
            };
            ct.ThrowIfCancellationRequested();
            var installed = VersionInfoResolver.Resolve(Path.Combine(stageRoot, "versions", stageId), stageId, null);
            if (!string.IsNullOrWhiteSpace(installed.ModLoaderVersion) && change.Loader != ServerCoreType.Vanilla &&
                change.LoaderVersion != installed.ModLoaderVersion &&
                !(change.Loader == ServerCoreType.Forge && change.LoaderVersion == entry.Id + "-" + installed.ModLoaderVersion))
                throw new InvalidDataException("安装器返回的组件版本与所选版本不同，未替换原实例。请刷新列表后重试。");
            var prepared = ComponentProfileComposer.Compose(stageRoot, stageId, versionId, entry.Id,
                change.Loader.ToString(), change.LoaderVersion);
            ComponentProfileCustomization.Preserve(prepared.Json, originalProfiles);
            var preparedJson = Path.Combine(stageRoot, "prepared-profile.json");
            await File.WriteAllTextAsync(preparedJson, prepared.Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
            var complete = JsonSerializer.Deserialize<VersionDetail>(prepared.Json.ToJsonString())!;
            // Loader installers may tolerate library download failures; maintenance must not
            // publish such a partial profile over a currently installed instance.
            await downloader.DownloadLibrariesOnlyAsync(stageRoot, complete, progress, ct);
            progress?.Report(new ProgressInfo("检查新组件文件", 0, 1, versionId));
            await Task.Run(() => PublishSharedFiles(stageRoot, minecraftRoot, ct), ct);
            if (ensureIdle != null) await ensureIdle();
            if (before != await Task.Run(() => Fingerprint(targetDirectory, versionId), ct))
                throw new IOException("安装期间原实例的版本文件已变化，本次修改未应用。请重新打开维护窗口。");
            progress?.Report(new ProgressInfo("备份当前实例", 0, 1, versionId));
            var backup = await InstanceBackupService.CreateBackupAsync(minecraftRoot, versionId,
                "组件修改前_" + Guid.NewGuid().ToString("N")[..8], ct: ct);
            ct.ThrowIfCancellationRequested();
            if (ensureIdle != null) await ensureIdle();
            if (before != await Task.Run(() => Fingerprint(targetDirectory, versionId), ct))
                throw new IOException("备份期间原实例的版本文件已变化，本次修改未应用。");
            await Task.Run(CheckDependents, ct);
            progress?.Report(new ProgressInfo("应用版本与组件修改", 0, 1, versionId));
            beforeCommit?.Invoke();
            await Task.Run(() => ComponentProfileTransaction.Commit(minecraftRoot, versionId,
                preparedJson, prepared.ClientJar, ct, preparedNatives: Path.Combine(stageRoot, "versions", versionId, "natives")), ct);
            progress?.Report(new ProgressInfo("版本与组件已更新", 1, 1, versionId));
            return backup;
        }
        catch (Exception error) when (InstallerProcessLifetime.RequiresPreservedDirectory(error))
        {
            preserveStage = true;
            throw;
        }
        finally
        {
            if (!preserveStage) await Task.Run(() => ComponentProfileTransaction.TryDeleteDirectory(stageRoot));
        }
    }

    private static async Task ValidateChoiceAsync(ClientLoaderInstallService loaders, InstalledVersionChange change, CancellationToken ct)
    {
        if (change.Loader == ServerCoreType.Vanilla) return;
        if (string.IsNullOrWhiteSpace(change.LoaderVersion)) throw new InvalidOperationException("请选择组件版本。");
        if (change.Loader is ServerCoreType.Forge or ServerCoreType.NeoForge or ServerCoreType.OptiFine &&
            (string.IsNullOrWhiteSpace(change.JavaPath) || !File.Exists(change.JavaPath)))
            throw new FileNotFoundException("请选择有效的 Java 路径；OptiFine 需要包含 javac 的 JDK。", change.JavaPath);
        if (change.Loader == ServerCoreType.NeoForge)
        {
            if (ComponentProfileComposer.MinecraftForNeoForge(change.LoaderVersion) != change.MinecraftVersion ||
                !(await loaders.GetNeoForgeVersionsAsync(ct)).Contains(change.LoaderVersion))
                throw new InvalidOperationException("这个 NeoForge 构建不支持所选 Minecraft 版本。");
            return;
        }
        var builds = change.Loader switch
        {
            ServerCoreType.Fabric => await loaders.GetFabricLoaderVersionsAsync(change.MinecraftVersion, ct),
            ServerCoreType.LegacyFabric => await loaders.GetLegacyFabricLoaderVersionsAsync(change.MinecraftVersion, ct),
            ServerCoreType.Quilt => await loaders.GetQuiltLoaderVersionsAsync(change.MinecraftVersion, ct),
            ServerCoreType.Forge => await loaders.GetForgeInstallerVersionsAsync(change.MinecraftVersion, ct),
            ServerCoreType.OptiFine => await loaders.GetOptiFineVersionsAsync(change.MinecraftVersion, ct),
            _ => throw new InvalidOperationException("不支持这个客户端组件。")
        };
        if (!builds.Any(item => item.DisplayVersion == change.LoaderVersion &&
            (change.Loader != ServerCoreType.OptiFine || item.RawIdentifier == change.OptiFineIdentifier)))
            throw new InvalidOperationException("所选组件与 Minecraft 版本不兼容或已从列表移除，请刷新后重新选择。");
    }

    private static string Fingerprint(string directory, string id)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(directory))!;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string version)
        {
            if (!visited.Add(version) || visited.Count > 64) throw new InvalidDataException("版本继承出现循环或超过 64 层。");
            var versionDirectory = ComponentProfileTransaction.VersionDirectory(root, version);
            var profile = ComponentProfileTransaction.FindProfile(versionDirectory, version)
                ?? throw new InvalidDataException("找不到唯一的版本描述文件，请先补全文件。");
            names.Add(profile);
            names.Add(ArchiveImportSafety.Resolve(versionDirectory, version + ".jar"));
            var json = JsonNode.Parse(File.ReadAllText(profile)) as JsonObject ?? throw new InvalidDataException("版本信息无效。");
            if (json["id"]?.GetValue<string>() is { Length: > 0 } internalId)
            {
                _ = ComponentProfileTransaction.VersionDirectory(root, internalId);
                names.Add(ArchiveImportSafety.Resolve(versionDirectory, internalId + ".jar"));
            }
            if (json["jar"]?.GetValue<string>() is { Length: > 0 } jarId)
                names.Add(ArchiveImportSafety.Resolve(ComponentProfileTransaction.VersionDirectory(root, jarId), jarId + ".jar"));
            if (json["inheritsFrom"]?.GetValue<string>() is { Length: > 0 } parent) Read(parent);
        }
        Read(id);
        return string.Join("|", names.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            ArchiveImportSafety.CheckLinks(path);
            if (!File.Exists(path)) return path + ":missing";
            using var stream = File.OpenRead(path);
            return path + ":" + Convert.ToHexString(SHA256.HashData(stream));
        }));
    }

    internal static void PublishSharedFiles(string stageRoot, string root, CancellationToken ct)
    {
        // Shared caches are content-addressed/versioned. Add missing files only; overwriting a
        // different library at an existing path could break another instance, so fail explicitly.
        foreach (var tree in new[] { "libraries", "assets" })
        {
            var source = Path.Combine(stageRoot, tree);
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                ArchiveImportSafety.CheckLinks(file);
                var destination = ArchiveImportSafety.Resolve(root, tree + "/" + Path.GetRelativePath(source, file).Replace('\\', '/'));
                if (File.Exists(destination))
                {
                    if (!FilesEqual(file, destination)) throw new IOException("共享依赖文件与新组件不一致，未覆盖其他实例使用的文件：" + destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + ".xcl-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(file, temporary);
                    try { File.Move(temporary, destination, overwrite: false); }
                    catch (IOException) when (File.Exists(destination) && FilesEqual(file, destination)) { }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }

    private static bool FilesEqual(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        using var a = File.OpenRead(first); using var b = File.OpenRead(second);
        return SHA256.HashData(a).SequenceEqual(SHA256.HashData(b));
    }
}
