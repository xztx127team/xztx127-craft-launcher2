using System.IO;
using System.Text;

namespace XCL2.App.Services;

/// <summary>Bound diagnostic reads before allocation, including files growing while read.</summary>
internal static class DiagnosticLogReader
{
    internal const int MaximumBytes = 512 * 1024;
    internal const string TruncationNotice = "[XCL2: 日志过大，诊断仅保留限定片段；完整记录仍在原文件中。]";

    internal static string Read(string path, bool includeHead = false)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = file.Length;
        var (encoding, alignment) = DetectEncoding(file);
        if (length <= MaximumBytes) return ReadPart(file, 0, (int)length, encoding);
        var headBytes = includeHead ? MaximumBytes / 2 : 0;
        var head = headBytes > 0 ? ReadPart(file, 0, headBytes, encoding) : "";
        // A cut line can separate a credential from its identifying key. Never return
        // either boundary fragment for a later diagnostic/AI sanitizer to guess at.
        var headNewline = head.LastIndexOf('\n');
        head = headNewline >= 0 ? head[..(headNewline + 1)] : "";
        var offset = length - (MaximumBytes - headBytes);
        offset += (alignment - offset % alignment) % alignment;
        var tail = ReadPart(file, offset, (int)(length - offset), encoding);
        var newline = tail.IndexOf('\n');
        tail = newline >= 0 ? tail[(newline + 1)..] : "[超长单行日志已省略]";
        return head + TruncationNotice + "\n" + tail;
    }

    private static (Encoding Encoding, int Alignment) DetectEncoding(FileStream file)
    {
        Span<byte> prefix = stackalloc byte[4];
        var count = file.Read(prefix);
        if (count >= 4 && prefix[0] == 0xff && prefix[1] == 0xfe && prefix[2] == 0 && prefix[3] == 0) return (Encoding.UTF32, 4);
        if (count >= 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff) return (new UTF32Encoding(true, true), 4);
        if (count >= 2 && prefix[0] == 0xff && prefix[1] == 0xfe) return (Encoding.Unicode, 2);
        if (count >= 2 && prefix[0] == 0xfe && prefix[1] == 0xff) return (Encoding.BigEndianUnicode, 2);
        return (Encoding.UTF8, 1);
    }

    private static string ReadPart(FileStream file, long offset, int count, Encoding encoding)
    {
        file.Position = offset;
        var bytes = new byte[count];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = file.Read(bytes, read, bytes.Length - read);
            if (n == 0) break;
            read += n;
        }
        using var buffer = new MemoryStream(bytes, 0, read, writable: false);
        using var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks: offset == 0);
        return reader.ReadToEnd();
    }
}
