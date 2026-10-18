using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XCL2.App.Services.Worlds;

namespace XCL2.App.Services.Plugins;

public sealed record PluginUpgradeResult(string Package, string Report, string Fingerprint) : IDisposable
{
    internal string Workspace { get; init; } = "";
    internal string PayloadDirectory { get; init; } = "";
    internal string? SourceDirectory { get; init; }
    internal string Entry { get; init; } = "";
    internal string PermissionWarning { get; init; } = "";
    public void Dispose()
    {
        if (string.IsNullOrEmpty(Workspace)) return;
        try { if (Directory.Exists(Workspace)) Directory.Delete(Workspace, true); } catch { }
    }
}

/// <summary>Builds in a copy and exports a reviewable v2 package. It never sends source to a remote AI.</summary>
internal static class PluginUpgradeService
{
    internal static async Task<PluginUpgradeResult> PrepareAsync(LoadedPluginInfo plugin, string? source,
        string destination, IProgress<string>? progress, CancellationToken ct)
    {
        if (!PluginUpgradeExport.IsPackage(destination)) throw new IOException("升级包请使用 .xcl 或 .xclplugin 扩展名。");
        var workspace = Path.Combine(Path.GetTempPath(), "XCL2-upgrade-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
        var keepWorkspace = false;
        try
        {
            string? working = null, project = null;
            var payload = Path.Combine(workspace, "payload"); Directory.CreateDirectory(payload);
            var original = Path.GetDirectoryName(plugin.FilePath)!;
            var manifestPath = Path.Combine(original, "plugin.json");
            JsonObject manifest = File.Exists(manifestPath) ? await ReadManifestAsync(manifestPath, ct) : new JsonObject();
            string entry = Path.GetFileName(plugin.FilePath);
            var previousManifest = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (source != null)
            {
                progress?.Report("复制源码并准备本地编译…"); working = Path.Combine(workspace, "source");
                if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                { ExtractSource(source, working); var projects = Directory.GetFiles(working, "*.csproj", SearchOption.AllDirectories); if (projects.Length != 1) throw new IOException("源码 ZIP 必须包含一个 .csproj；多项目请直接选择入口 .csproj。"); project = projects[0]; }
                else
                { CopySource(Path.GetDirectoryName(source)!, working, ct); project = Path.Combine(working, Path.GetFileName(source)); }
                // A source upgrade may change its requested capabilities. Prefer its own manifest over the installed one.
                var sourceManifest = Path.Combine(Path.GetDirectoryName(project)!, PluginManifestReader.FileName);
                if (File.Exists(sourceManifest)) manifest = await ReadManifestAsync(sourceManifest, ct);
                else manifest.Remove("permissions"); // Old installed grants/defaults cannot describe new source.
                // Project evaluation/MSBuild targets may execute code; the dialog explicitly discloses this before entry.
                var output = Path.Combine(workspace, "build");
                var hostAssemblyPath = ResolveHostAssemblyPath();
                var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(project)!, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "build", project, "-c", "Release", "-o", output, "-p:Xcl2HostPath=" + hostAssemblyPath, "-p:NuGetAudit=false" }) psi.ArgumentList.Add(arg);
                using var process = Process.Start(psi) ?? throw new IOException("无法运行 dotnet SDK。");
                using var compilation = CancellationTokenSource.CreateLinkedTokenSource(ct); compilation.CancelAfter(TimeSpan.FromMinutes(5));
                var log = new StringBuilder(); var gate = new object();
                async Task Drain(StreamReader reader)
                { while (await reader.ReadLineAsync(compilation.Token) is { } line) { lock (gate) { if (log.Length < 200000) log.AppendLine(line); } progress?.Report(line.Length > 1000 ? line[..1000] : line); } }
                var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
                try { await process.WaitForExitAsync(compilation.Token); compilation.CancelAfter(TimeSpan.FromSeconds(5)); await Task.WhenAll(stdout, stderr); }
                catch { compilation.Cancel(); try { if (!process.HasExited) process.Kill(true); } catch { } try { await Task.WhenAll(stdout, stderr); } catch { } throw; }
                if (process.ExitCode != 0) throw new IOException("源码编译失败，原插件未修改：\n" + log.ToString()[..Math.Min(log.Length, 8000)]);
                if (!File.Exists(Path.Combine(output, entry))) throw new IOException($"编译输出没有 {entry}。请在项目中保留入口 AssemblyName，或使用正确的插件入口项目。原插件未修改。");
                CopySource(output, payload, ct, skipBuild: false);
                var builtManifest = Path.Combine(output, PluginManifestReader.FileName);
                if (File.Exists(builtManifest)) manifest = await ReadManifestAsync(builtManifest, ct);
                // The host contract is shared with XCL2 and must not be shipped as a private duplicate.
                var hostName = Path.GetFileName(ResolveHostAssemblyPath()); var copiedHost = Path.Combine(payload, hostName);
                if (!hostName.Equals(entry, StringComparison.OrdinalIgnoreCase) && File.Exists(copiedHost)) File.Delete(copiedHost);
            }
            else
            {
                progress?.Report("复制已安装插件，迁移清单…");
                if (File.Exists(Path.Combine(original, ".xcl-plugin.json")) || File.Exists(manifestPath)) CopySource(original, payload, ct, skipBuild: false);
                else
                {
                    File.Copy(plugin.FilePath, Path.Combine(payload, entry));
                    // Loose legacy entries may still use an apphost or adjacent managed dependencies.
                    CopyLegacyDependencies(plugin.FilePath, payload, ct);
                }
            }
            foreach (var name in new[] { ".xcl-plugin.json", PluginPackageSignature.FileName }) { var file = Path.Combine(payload, name); if (File.Exists(file)) File.Delete(file); }
            var id = plugin.Metadata?.DeclaredId ?? plugin.Plugin?.Id ?? throw new IOException("无法确定插件 ID，不能安全迁移。"); PluginManifestReader.ValidateId(id);
            if (manifest["id"]?.GetValue<string>() is { Length: > 0 } declaredId && declaredId != id)
                throw new IOException("源码清单的插件 ID 与已安装插件不一致，不能替换。请通过导入安装其他插件。");
            manifest["schemaVersion"] = 2; manifest["id"] = id; manifest["name"] = manifest["name"]?.GetValue<string>() ?? plugin.Metadata?.Name ?? plugin.Plugin?.DisplayName ?? id;
            var version = manifest["version"]?.GetValue<string>() ?? plugin.Metadata?.Version ?? plugin.Plugin?.Version ?? "1.0.0";
            try { _ = PluginSemanticVersion.Parse(version); } catch { version = "1.0.0"; }
            manifest["version"] = version; manifest["entry"] = entry; manifest["kind"] = Path.GetExtension(entry).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? "exe" : "dll";
            manifest["minApiVersion"] = "2.0"; manifest.Remove("maxApiVersion");
            // Never insert blanket UI/default permissions. Preserve explicit declarations (including dynamic/pipe calls)
            // and add only host capabilities actually imported by the built entry and its packaged dependencies.
            var analysis = PluginUpgradePermissions.Analyze(Path.Combine(payload, entry), ct);
            var required = analysis.Required.ToHashSet(StringComparer.Ordinal);
            var declaredPermissions = new List<string>();
            if (manifest["permissions"] is JsonArray declared)
                foreach (var permission in declared) declaredPermissions.Add(permission?.GetValue<string>() ?? throw new IOException("权限声明不能为空。"));
            else if (manifest["permissions"] != null) throw new IOException("权限声明必须是字符串数组。");
            required.UnionWith(declaredPermissions);
            foreach (var permission in required)
                if (!PluginPermissionNames.All.Contains(permission, StringComparer.Ordinal)) throw new IOException("未知插件权限：" + permission);
            manifest["permissions"] = new JsonArray(required.OrderBy(p => p, StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            var warnings = analysis.Warnings.Where(w => !w.StartsWith("UnknownSettingsPanel：", StringComparison.Ordinal) || !required.Contains("ui.basic")).ToList();
            if (declaredPermissions.Count == 0) warnings.Add("插件没有明确声明权限；目前只列出能够静态识别的宿主调用，动态行为需要作者补充清单。");
            var permissionWarning = warnings.Count == 0 ? "" : "可能有权限不准问题：\n" + string.Join("\n", warnings.Select(w => "• " + w));
            if (manifest["dependencies"] == null) manifest["dependencies"] = new JsonArray();
            if (manifest["conflicts"] == null) manifest["conflicts"] = new JsonArray();
            var report = $"XCL2 插件迁移报告\n插件：{id}\n操作：{(source == null ? "复制现有二进制；迁移清单和签名" : "在源码副本中本地编译；迁移清单和签名")}\n清单：schemaVersion 2；API 最低版本 2.0\n最终申请权限：{manifest["permissions"]}\n作者明确声明：{string.Join(", ", declaredPermissions)}\n宿主调用检测：{string.Join(", ", analysis.Required)}\n没有补入默认权限，也没有自动授权。\n{permissionWarning}\n签名：本机临时 ECDSA 密钥，仅校验包完整性，不代表原作者或可信发布者身份。\n状态：未执行插件功能测试，接口或运行时依赖可能仍需开发者修改。\n源码没有上传到远程服务；dotnet restore 可能访问项目指定的软件源。\n安装后保持禁用；点击右上角“插火”审核新增权限并注册、启用插件。\n\n迁移前清单：\n{previousManifest}\n\n迁移后清单：\n{manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}\n";
            await File.WriteAllTextAsync(Path.Combine(payload, "migration-report.txt"), report, ct);
            await File.WriteAllTextAsync(Path.Combine(payload, "plugin.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested(); var fingerprint = PluginPackageSignature.SignLocal(payload); _ = PluginPackageSignature.Verify(payload);
            _ = PluginManifestReader.ReadEntry(payload);
            if (working != null && project != null)
            {
                var projectDirectory = Path.GetDirectoryName(project)!;
                foreach (var generated in new[] { PluginPackageSignature.FileName, ".xcl-plugin.json" })
                {
                    var stale = Path.Combine(projectDirectory, generated);
                    if (File.Exists(stale)) File.Delete(stale);
                }
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, PluginManifestReader.FileName), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
                await File.WriteAllTextAsync(Path.Combine(working, "migration-report.txt"), report, ct);
                await File.WriteAllTextAsync(Path.Combine(working, "UPGRADE-README.txt"),
                    "这是此次升级所使用的源码副本，入口项目旁的 plugin.json 已更新为最终权限清单。\n迁移未自动改写业务代码，也没有从二进制反编译源码。\n重新编译时请传入 -p:Xcl2HostPath=<启动器目录中的 XCL2.dll>。\n编译改变文件后必须重新签名；可将本源码重新导入“升级插件”生成签名包，不能沿用旧签名。\nDLL 与 EXE 管道插件使用不同运行契约，不能仅修改扩展名转换。\n", ct);
            }
            PluginUpgradeExport.ExportZip(payload, destination, ct);
            var result = new PluginUpgradeResult(destination, report, fingerprint)
            { Workspace = workspace, PayloadDirectory = payload, SourceDirectory = working, Entry = entry, PermissionWarning = permissionWarning };
            keepWorkspace = true;
            progress?.Report("已生成并校验升级包，可以安装或导出。"); return result;
        }
        finally { if (!keepWorkspace) try { Directory.Delete(workspace, true); } catch { } }
    }
    private static async Task<JsonObject> ReadManifestAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("插件清单超过 1 MiB。");
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject() ?? throw new IOException("插件清单为空。");
    }
    private static string ResolveHostAssemblyPath()
    {
        var assembly = typeof(IPlugin).Assembly;
        // Assembly.Location is empty for assemblies bundled into a single-file application.
        if (!string.IsNullOrWhiteSpace(assembly.Location))
            return assembly.Location;

        var assemblyName = assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
            throw new IOException("无法确定 XCL2 插件 API 程序集名称。");

        // Source-plugin compilation needs a real file path to reference the host contract.
        var bundledHostPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (File.Exists(bundledHostPath))
            return bundledHostPath;

        throw new IOException(
            $"当前启动器以单文件方式运行，且目录中找不到插件 API 程序集：{bundledHostPath}。无法编译插件源码；请在应用目录提供该 API DLL。");
    }

    private static void CopySource(string source, string destination, CancellationToken ct, bool skipBuild = true) =>
        PluginUpgradeExport.CopyTree(source, destination, ct, skipBuild);

    private static void CopyLegacyDependencies(string entry, string destination, CancellationToken ct)
    {
        var root = Path.GetDirectoryName(entry)!;
        var pending = new Queue<string>(); pending.Enqueue(entry);
        if (Path.GetExtension(entry).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(entry, ".dll")))
            pending.Enqueue(Path.ChangeExtension(entry, ".dll"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        while (pending.TryDequeue(out var path))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(path)) continue;
            WorldArchiveService.RejectLinks(path);
            bytes += new FileInfo(path).Length;
            if (bytes > 512L * 1024 * 1024 || seen.Count > 10000) throw new IOException("插件依赖超过复制上限。");
            File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), true);
            using var input = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(input);
            if (!pe.HasMetadata) continue;
            var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var handle in reader.AssemblyReferences)
            {
                var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                if (name == typeof(IPlugin).Assembly.GetName().Name || Path.GetFileName(name) != name || name.Contains(':')) continue;
                var dependency = Path.Combine(root, name + ".dll");
                if (File.Exists(dependency)) pending.Enqueue(dependency);
            }
        }
        foreach (var extension in new[] { ".deps.json", ".runtimeconfig.json" })
        {
            var path = Path.Combine(root, Path.GetFileNameWithoutExtension(entry) + extension);
            if (File.Exists(path)) { WorldArchiveService.RejectLinks(path); File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), false); }
        }
    }

    private static void ExtractSource(string file, string destination)
    {
        Directory.CreateDirectory(destination); string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(file); long total = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count > 10000) throw new IOException("源码包文件过多。");
        foreach (var entry in zip.Entries)
        {
            total += entry.Length; var name = entry.FullName.Replace('\\', '/');
            if (total > 256L * 1024 * 1024 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "." or "..")) throw new IOException("源码包过大或包含不安全路径。");
            var target = Path.GetFullPath(Path.Combine(destination, name)); if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !seen.Add(target)) throw new IOException("源码包路径重复或越界。");
            if (name.EndsWith('/')) Directory.CreateDirectory(target); else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, false); }
        }
    }
}
