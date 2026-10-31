using System.Diagnostics;
using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed record MiniWorldLocalCandidate(string ExecutablePath, string SourceId, string SourceName, string Reason);
public sealed record MiniWorldLaunchResult(int ProcessId, bool ExitedEarly, int? ExitCode);

/// <summary>本地客户端发现和启动与在线下载目录分开；只主动启动用户保存的游戏程序。</summary>
public static class MiniWorldLaunchService
{
    private static readonly string[] GameNames = { "CBLministart.exe", "MicroMiniNew.exe", "MicroMini.exe" };
    public static bool IsInstallerOrUtility(string? path)
    {
        var name = Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant();
        return new[] { "setup", "install", "unins", "unwise", "updater", "crashreport", "crashhandler", "repair" }.Any(name.Contains) ||
            name is "miniworldoffice" or "miniworldbeta_pc";
    }

    public static string ValidateGameExecutable(string? path)
    {
        var executable = MiniWorldCatalog.ValidateExecutable(path);
        if (IsInstallerOrUtility(executable)) throw new InvalidOperationException("选中的程序是安装器、卸载器或维护工具。请安装/解压后选择 MicroMiniNew.exe、MicroMini.exe 或社区发布者指定的游戏启动程序。");
        return executable;
    }

    public static ProcessStartInfo CreateStartInfo(MiniWorldLocalInstallation installation)
    {
        var executable = ValidateGameExecutable(installation.ExecutablePath);
        var directory = string.IsNullOrWhiteSpace(installation.WorkingDirectory) ? Path.GetDirectoryName(executable)! : Path.GetFullPath(installation.WorkingDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("游戏工作目录不存在，请编辑此客户端的工作目录或重新选择 EXE。");
        if (Path.GetFileName(executable).Equals("CBLministart.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(executable)!.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CBL 客户端的工作目录必须是 CBLministart.exe 所在版本文件夹，请恢复该目录后启动。");
        var arguments = (installation.Arguments ?? new List<string>()).ToList();
        if (Path.GetFileName(executable).Equals("CBLministart.exe", StringComparison.OrdinalIgnoreCase) && !arguments.Any(argument => argument?.StartsWith("CBL_", StringComparison.Ordinal) == true))
            arguments.InsertRange(0, SuggestedArguments(executable));
        if (arguments.Count > 128 || arguments.Sum(argument => (argument ?? "").Length) > 28000 || arguments.Any(argument => argument == null || argument.Any(ch => ch == '\0' || ch == '\r' || ch == '\n')))
            throw new ArgumentException("启动参数无效或过长。每行一个参数，不能包含换行或空字符。");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static IReadOnlyList<string> SuggestedArguments(string executable)
    {
        if (!Path.GetFileName(executable).Equals("CBLministart.exe", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
        var metadata = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "cbl_game_info.json");
        if (!File.Exists(metadata)) throw new FileNotFoundException("CBL 客户端缺少 cbl_game_info.json，请把全部归档内容解压到同一文件夹，或选择正确的游戏目录。", metadata);
        if (new FileInfo(metadata).Length > 65536) throw new InvalidDataException("CBL 客户端信息文件过大，请检查归档是否完整。");
        using var document = JsonDocument.Parse(File.ReadAllText(metadata), new JsonDocumentOptions { MaxDepth = 12 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("CBL 客户端信息格式无效，请检查 cbl_game_info.json 或重新解压归档。");
        var channel = "1";
        if (document.RootElement.TryGetProperty("Channel", out var value)) channel = value.ToString().Trim();
        if (channel.Length is < 1 or > 8 || channel.Any(character => character is < '0' or > '9')) throw new InvalidDataException("CBL 客户端渠道信息无效，请检查 cbl_game_info.json 或重新解压原归档。");
        return new[] { "CBL_" + channel };
    }

    public static async Task<MiniWorldLaunchResult> LaunchAsync(MiniWorldLocalInstallation installation, Action<Process>? onStarted = null)
    {
        var process = Process.Start(CreateStartInfo(installation)) ?? throw new InvalidOperationException("系统没有返回游戏进程，请检查文件权限或重新选择游戏 EXE。");
        var tracked = false;
        try
        {
            var pid = process.Id;
            if (onStarted != null) { onStarted(process); tracked = true; }
            await Task.Delay(850);
            return process.HasExited ? new(pid, true, process.ExitCode) : new(pid, false, null);
        }
        finally { if (!tracked) process.Dispose(); }
    }

    public static MiniWorldLocalInstallation CreateInstallation(string executable, string sourceId = "local", string sourceName = "本地导入",
        string? bindingKey = null, string? versionId = null, string? versionName = null, string? name = null)
    {
        var path = ValidateGameExecutable(executable); var directory = Path.GetDirectoryName(path)!;
        var title = name; var version = versionName;
        var metadata = Path.Combine(directory, "cbl_game_info.json");
        if (File.Exists(metadata) && new FileInfo(metadata).Length <= 65536)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadata), new JsonDocumentOptions { MaxDepth = 12 });
                string Field(string key) => document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(key, out var value) ? value.ToString() : "";
                if (string.IsNullOrWhiteSpace(title)) title = Field("Name");
                if (string.IsNullOrWhiteSpace(version))
                {
                    version = Field("Version"); var revision = Field("ModifiedVersion");
                    if (revision.Length > 0 && revision != "0") version += " · 社区修订 " + revision;
                }
            }
            catch (JsonException) when (!Path.GetFileName(path).Equals("CBLministart.exe", StringComparison.OrdinalIgnoreCase)) { }
        }
        return new MiniWorldLocalInstallation
        {
            SourceId = sourceId, SourceName = sourceName, VersionId = versionId ?? "local:" + path,
            BindingKey = bindingKey ?? MiniWorldCatalog.ClientKey(sourceId, "local:" + path.ToUpperInvariant()),
            Name = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(directory) : title[..Math.Min(title.Length, 160)],
            VersionName = string.IsNullOrWhiteSpace(version) ? "本地安装（版本未识别）" : version[..Math.Min(version.Length, 160)],
            ExecutablePath = path, WorkingDirectory = directory, Arguments = SuggestedArguments(path).ToList()
        };
    }

    public static IReadOnlyList<MiniWorldLocalCandidate> ScanDirectory(string directory, string sourceId = "local", string sourceName = "本地导入", CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("所选目录不存在。请先完成安装或手动解压压缩包。");
        var found = new List<MiniWorldLocalCandidate>(); var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((Path.GetFullPath(directory), 0));
        var visited = 0;
        while (queue.Count > 0 && visited++ < 1200 && found.Count < 50)
        {
            ct.ThrowIfCancellationRequested(); var current = queue.Dequeue();
            try
            {
                if ((File.GetAttributes(current.Path) & FileAttributes.ReparsePoint) != 0) continue;
                // 自动识别维护者已确认的普通入口与 CBL 包装入口，不猜测安装器或辅助 EXE。
                foreach (var name in GameNames)
                {
                    var executable = Path.Combine(current.Path, name);
                    if (!File.Exists(executable)) continue;
                    if (name.Equals("CBLministart.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(Path.Combine(current.Path, "cbl_game_info.json"))) continue;
                        try { _ = SuggestedArguments(executable); }
                        catch (JsonException) { continue; }
                        catch (InvalidDataException) { continue; }
                    }
                    found.Add(new(executable, sourceId, sourceName, "识别到游戏入口 " + name)); break;
                }
                if (current.Depth < 4)
                    foreach (var child in Directory.EnumerateDirectories(current.Path).Take(500))
                        if (!Path.GetFileName(child).Equals(".staging", StringComparison.OrdinalIgnoreCase)) queue.Enqueue((child, current.Depth + 1));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }

    public static IReadOnlyList<MiniWorldLocalCandidate> DetectCommonInstallations(CancellationToken ct = default)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(roaming)) return Array.Empty<MiniWorldLocalCandidate>();
        var roots = new[]
        {
            ("miniworldgameguan110", "official", "官方正式版"), ("miniworldgame4399", "4399", "4399 社区渠道"),
            ("miniworldgame7k7k", "7k7k", "7k7k 社区渠道"), ("miniworldXianqian", "beta", "官方先遣服"),
            ("miniworldOverseasgame", "international", "官方国际版")
        };
        return roots.Where(root => Directory.Exists(Path.Combine(roaming, root.Item1)))
            .SelectMany(root => ScanDirectory(Path.Combine(roaming, root.Item1), root.Item2, root.Item3, ct)).ToArray();
    }
}
