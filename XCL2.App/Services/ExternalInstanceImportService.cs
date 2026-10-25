using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Imports an independent copy; external launcher roots are never changed or registered as XCL roots.</summary>
public sealed class ExternalInstanceImportService
{
    private readonly AppConfig _config;
    public ExternalInstanceImportService(AppConfig config) => _config = config;

    public async Task<string> ImportAsync(ExternalInstanceCandidate source, string minecraftRoot, string name,
        string? javaPath, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default, Action<string>? targetPrepared = null)
    {
        source = ExternalInstanceDiscovery.Validate(source);
        if (!source.CanImport) throw new InvalidDataException(source.Problem ?? "实例版本信息不完整。");
        minecraftRoot = Path.GetFullPath(minecraftRoot);
        var sourceRoot = Path.GetFullPath(source.InstanceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(minecraftRoot, sourceRoot, StringComparison.OrdinalIgnoreCase) ||
            minecraftRoot.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("目标游戏目录不能位于源实例内部。");
        ArchiveImportSafety.CheckLinks(source.GameDirectory); ArchiveImportSafety.CheckLinks(minecraftRoot);
        var id = ModpackInstallService.MakeUniqueInstanceName(minecraftRoot, name);
        var destination = ArchiveImportSafety.Resolve(Path.Combine(minecraftRoot, "versions"), id);
        if ((source.Loader is "Forge" or "NeoForge") && (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath)))
            throw new InvalidOperationException("此实例需要本地 Java 运行加载器安装器。请先在设置中配置 Java，再导入。");
        using var operation = ComponentsOperationLock.AcquireForMaintenance(minecraftRoot, id);
        targetPrepared?.Invoke(id);
        await Plugins.PluginInterceptionRegistry.ValidateInstanceAsync(minecraftRoot, id, "external-instance-copy", ct);
        var stage = ArchiveImportSafety.Resolve(minecraftRoot, ".xcl-external-import/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var preserveStage = false;
        try
        {
            // Install only in a private staging root. Existing instances and external metadata are never edited.
            using var downloader = DownloadService.CreateFromConfig(_config);
            using var installers = new ClientLoaderInstallService(_config);
            progress?.Report(new ProgressInfo("核对外部实例版本", 0, 1, source.MinecraftVersion!));
            var manifest = await downloader.GetVersionManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == source.MinecraftVersion)
                ?? throw new InvalidDataException("官方版本清单没有 Minecraft " + source.MinecraftVersion + "，未创建实例。");
            await downloader.InstallVersionAsync(stage, entry, progress, ct);
            string installedId;
            switch (source.Loader)
            {
                case null: installedId = entry.Id; break;
                case "Fabric": installedId = await installers.InstallFabricClientAsync(stage, entry.Id, source.LoaderVersion!, progress, ct); break;
                case "Quilt": installedId = await installers.InstallQuiltClientAsync(stage, entry.Id, source.LoaderVersion!, progress, ct); break;
                case "Forge":
                    var forgeVersion = source.LoaderVersion!.StartsWith(entry.Id + "-", StringComparison.Ordinal) ? source.LoaderVersion : entry.Id + "-" + source.LoaderVersion;
                    installedId = await installers.InstallForgeOrNeoForgeClientAsync(stage, ServerCoreType.Forge, forgeVersion!, javaPath!, progress, ct); break;
                case "NeoForge": installedId = await installers.InstallForgeOrNeoForgeClientAsync(stage, ServerCoreType.NeoForge, source.LoaderVersion!, javaPath!, progress, ct); break;
                default: throw new InvalidDataException("不支持该实例的加载器。");
            }
            var actual = VersionInfoResolver.Resolve(Path.Combine(stage, "versions", installedId), installedId, null);
            var expectedLoaderVersion = source.LoaderVersion;
            if (source.Loader == "Forge" && expectedLoaderVersion?.StartsWith(entry.Id + "-", StringComparison.Ordinal) == true)
                expectedLoaderVersion = expectedLoaderVersion[(entry.Id.Length + 1)..];
            if (!string.Equals(actual.McVersion, entry.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.ModLoader, source.Loader, StringComparison.OrdinalIgnoreCase) ||
                (source.Loader != null && !string.Equals(actual.ModLoaderVersion, expectedLoaderVersion, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("安装器返回的游戏或加载器版本与源实例不一致，未发布副本。");
            var composed = ComponentProfileComposer.Compose(stage, installedId, id, entry.Id, source.Loader ?? "Vanilla", source.LoaderVersion);
            var detail = JsonSerializer.Deserialize<VersionDetail>(composed.Json.ToJsonString())!;
            if (string.IsNullOrWhiteSpace(detail.MainClass) || !File.Exists(composed.ClientJar) || new FileInfo(composed.ClientJar).Length == 0)
                throw new InvalidDataException("生成的实例缺少主类或客户端程序，未发布副本。");
            await downloader.DownloadLibrariesOnlyAsync(stage, detail, progress, ct);
            var prepared = Path.Combine(stage, "prepared-instance");
            await Task.Run(() => ExternalInstanceContent.Copy(source.GameDirectory, prepared, progress, ct), ct);
            var natives = Path.Combine(stage, "versions", id, "natives");
            if (Directory.Exists(natives)) await Task.Run(() => ExternalInstanceContent.Copy(natives, Path.Combine(prepared, "natives"), progress, ct), ct);
            // Content is copied before authoritative launch metadata, so it cannot replace the generated profile.
            await File.WriteAllTextAsync(Path.Combine(prepared, id + ".json"), composed.Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
            File.Copy(composed.ClientJar, Path.Combine(prepared, id + ".jar"), overwrite: false);
            await File.WriteAllTextAsync(Path.Combine(prepared, "xcl-import-origin.json"), JsonSerializer.Serialize(new
            { source.SourceKind, source.Name, source.MinecraftVersion, source.Loader, source.LoaderVersion, ImportedAt = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }), ct);
            // This setting is committed with the copy, not later in a global ID-only dictionary.
            InstanceConfigService.Save(prepared, new InstanceSettings { IsolateVersion = true });
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => InstalledVersionMaintenanceService.PublishSharedFiles(stage, minecraftRoot, ct), ct);
            ct.ThrowIfCancellationRequested(); ArchiveImportSafety.CheckLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Atomic publication; a competing import can never be merged into or deleted by this operation.
            Directory.Move(prepared, destination);
            Plugins.PluginHostBridge.Current?.NotifyInstanceCreated(minecraftRoot, id);
            return id;
        }
        catch (Exception ex) when (InstallerProcessLifetime.RequiresPreservedDirectory(ex))
        {
            // The installer may still hold files. Preserve its isolated work directory until it has really stopped.
            preserveStage = true;
            throw;
        }
        finally { try { if (!preserveStage && Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { } }
    }
}
