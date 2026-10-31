using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 下载 Mesa3D 的 Windows 软件渲染 OpenGL 库（opengl32.dll 等），用于显卡驱动缺失或 OpenGL 版本过低的机器。
/// 来源固定为 GitHub pal1000/mesa-dist-win 的最新发布；只解压 x64 目录下的 DLL，不写入系统目录。
/// </summary>
public static class OpenGlDllDownloadService
{
    private const string ReleaseApi = "https://api.github.com/repos/pal1000/mesa-dist-win/releases/latest";
    private const string DownloadPrefix = "https://github.com/pal1000/mesa-dist-win/releases/download/";
    private static readonly Regex AssetName = new(@"^mesa3d-[\w.\-]+-release-msvc\.7z$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const long MaxEntryBytes = 300L * 1024 * 1024;
    private static int _running;

    public static string DefaultFolder => Path.Combine(App.DataDir, "OpenGLDriver", "x64");

    public static void StartWithConfirmation()
    {
        if (!OperatingSystem.IsWindows()) { MessageBoxDialog.ShowWarning("该功能仅支持 Windows。", "下载 OpenGL 驱动 DLL"); return; }
        if (Volatile.Read(ref _running) != 0) { ToastService.ShowInfo("OpenGL DLL 下载任务正在进行，请查看下载列表。"); return; }
        if (!MessageBoxDialog.ShowConfirm(
            "将从 GitHub（pal1000/mesa-dist-win 最新发布）下载 Mesa3D 的 64 位 OpenGL 库，包含 opengl32.dll 等文件，并解压到你选择的文件夹。\n\n" +
            "这是 CPU 软件渲染，不是显卡厂商驱动，速度明显低于真实显卡；用于显卡缺少 OpenGL 支持时让游戏能够启动。\n" +
            "XCL 不会把它写入 Windows 系统目录，也不会覆盖系统自带的 opengl32.dll。使用时请把这些 DLL 复制到游戏所用的 javaw.exe 同目录，不需要时删除即可。\n" +
            "文件来自第三方发布，下载完成后会在文件夹内生成 SHA256.txt 供核对。\n\n是否继续？",
            "下载 OpenGL 驱动 DLL")) return;

        string? target;
        using (var picker = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择保存 OpenGL DLL 的文件夹",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(DefaultFolder) ? DefaultFolder : Path.GetDirectoryName(DefaultFolder)!,
            ShowNewFolderButton = true
        })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultFolder)!);
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            target = picker.SelectedPath;
        }
        if (string.IsNullOrWhiteSpace(target)) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            DownloadQueueService.Instance.StartNew("OpenGL 驱动 DLL（Mesa3D）", async (item, ct) =>
            {
                try { await RunAsync(item, target, ct); }
                finally { Interlocked.Exchange(ref _running, 0); }
            }, originPage: "OpenGL 显卡驱动");
            ToastService.ShowInfo("OpenGL DLL 已加入下载管理。");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _running, 0);
            MessageBoxDialog.ShowWarning("无法登记下载任务：" + ex.Message, "下载 OpenGL 驱动 DLL");
        }
    }

    private static async Task RunAsync(DownloadQueueItem item, string target, CancellationToken ct)
    {
        var url = await ResolveLatestAssetAsync(ct);
        Directory.CreateDirectory(target);
        var staging = Path.Combine(target, ".xcl-opengl-" + item.Id.ToString("N"));
        try
        {
            using var downloader = new GenericFileDownloadService();
            var archive = await downloader.DownloadAsync(new[] { url }, staging, Path.GetFileName(new Uri(url).LocalPath),
                "XCL2", item.CreateProgress(), ct, validateFile: ValidateSevenZip);
            ct.ThrowIfCancellationRequested();
            var written = await Task.Run(() => ExtractX64Dlls(archive, target, ct), ct);
            if (written.Count == 0) throw new InvalidDataException("压缩包中没有找到 x64 目录下的 DLL，发布包结构可能已变化。");
            if (!written.Contains("opengl32.dll", StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("压缩包中缺少 opengl32.dll。");
            var sb = new StringBuilder("# SHA-256（由 XCL 在解压后计算；来源：" + url + "）\r\n");
            foreach (var name in written.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(Path.Combine(target, name));
                sb.Append(Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant()).Append("  ").Append(name).Append("\r\n");
            }
            File.WriteAllText(Path.Combine(target, "SHA256.txt"), sb.ToString(), new UTF8Encoding(false));
            item.CompletionText = $"已下载 {written.Count} 个 OpenGL DLL 到：{target}。请复制到 javaw.exe 同目录使用。";
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> ResolveLatestAssetAsync(CancellationToken ct)
    {
        using var http = LauncherHttpClientFactory.Create(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
        request.Headers.UserAgent.ParseAdd("XCL2");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var link = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name != null && link != null && AssetName.IsMatch(name)
                    && link.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                    return link;
            }
        throw new InvalidDataException("最新发布中没有找到 Mesa3D（MSVC release）压缩包。");
    }

    private static void ValidateSevenZip(string path)
    {
        var head = new byte[6];
        using var fs = File.OpenRead(path);
        if (fs.Read(head) != 6 || !head.AsSpan().SequenceEqual(new byte[] { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C }))
            throw new InvalidDataException("下载的文件不是有效的 7z 压缩包。");
    }

    private static List<string> ExtractX64Dlls(string archivePath, string target, CancellationToken ct)
    {
        var written = new List<string>();
        using var archive = ArchiveFactory.Open(archivePath);
        // 7z 通常是固态压缩；ExtractAllEntries 按顺序读取，比逐个随机打开快得多。
        using var reader = archive.ExtractAllEntries();
        var mz = new byte[2];
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory || entry.Key is not { } key) continue;
            var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            // 只取 x64/<文件>.dll，文件名只用最后一段，避免路径穿越。
            if (parts.Length != 2 || !parts[0].Equals("x64", StringComparison.OrdinalIgnoreCase)) continue;
            var name = parts[1];
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name != Path.GetFileName(name)
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.Size > MaxEntryBytes) continue;
            var temp = Path.Combine(target, name + ".xcl-part");
            using (var input = reader.OpenEntryStream())
            using (var output = File.Create(temp))
                input.CopyTo(output);
            using (var check = File.OpenRead(temp))
                if (check.Read(mz) != 2 || mz[0] != (byte)'M' || mz[1] != (byte)'Z')
                { File.Delete(temp); throw new InvalidDataException(name + " 不是有效的 Windows DLL。"); }
            File.Move(temp, Path.Combine(target, name), overwrite: true);
            written.Add(name);
        }
        return written;
    }
}
