using System.IO;
using System.IO.Compression;
using System.Text;

namespace XCL2.App.Services;

public sealed record DiagnosticExportSource(string Title, string Text);
public sealed record DiagnosticExportEntry(string FileName, string Title, string SanitizedText);

public static class DiagnosticsExportService
{
    public static IReadOnlyList<DiagnosticExportEntry> Prepare(IEnumerable<DiagnosticExportSource> sources)
    {
        var entries = new List<DiagnosticExportEntry>();
        foreach (var source in sources.Take(8))
        {
            if (string.IsNullOrWhiteSpace(source.Text)) continue;
            var title = DiagnosticRedactionService.Sanitize(source.Title, 256).Replace('\r', ' ').Replace('\n', ' ');
            entries.Add(new($"diagnostic-{entries.Count + 1:00}.txt", title,
                DiagnosticRedactionService.Sanitize(source.Text)));
        }
        return entries;
    }

    // Export uses exactly the prepared preview strings, never re-reads raw logs or settings.
    public static void Export(string destination, IReadOnlyList<DiagnosticExportEntry> prepared)
    {
        if (prepared.Count == 0 || prepared.Count > 8) throw new InvalidOperationException("请至少选择一项诊断内容。");
        var target = Path.GetFullPath(destination);
        var temp = Path.Combine(Path.GetDirectoryName(target)!, ".xcl2-diagnostic-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var index = 0;
                foreach (var entry in prepared)
                {
                    var file = archive.CreateEntry($"diagnostic-{++index:00}.txt", CompressionLevel.Optimal);
                    using var writer = new StreamWriter(file.Open(), new UTF8Encoding(false));
                    writer.WriteLine(entry.Title);
                    writer.Write(entry.SanitizedText);
                }
                var readme = archive.CreateEntry("README.txt");
                using var notes = new StreamWriter(readme.Open(), new UTF8Encoding(false));
                notes.Write("由用户主动导出的本地诊断包。内容与导出前预览一致，已隐藏常见凭据、邮箱和用户目录；自动规则不能识别所有个人信息，请在分享前人工检查。没有上传任何文件。\n");
            }
            File.Move(temp, target, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
