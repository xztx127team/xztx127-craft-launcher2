using System.IO;
using System.Text;

namespace XCL2.App.Services;

public static class LogTextEncoding
{
    public static Encoding Detect(Stream stream)
    {
        if (!stream.CanSeek) return Encoding.UTF8;
        long position = stream.Position;
        try
        {
            stream.Position = 0;
            var sample = new byte[(int)Math.Min(65536, stream.Length)];
            int count = stream.Read(sample, 0, sample.Length);
            if (count >= 4 && sample[0] == 0xff && sample[1] == 0xfe && sample[2] == 0 && sample[3] == 0) return Encoding.UTF32;
            if (count >= 4 && sample[0] == 0 && sample[1] == 0 && sample[2] == 0xfe && sample[3] == 0xff) return new UTF32Encoding(true, true);
            if (count >= 2 && sample[0] == 0xff && sample[1] == 0xfe) return Encoding.Unicode;
            if (count >= 2 && sample[0] == 0xfe && sample[1] == 0xff) return Encoding.BigEndianUnicode;
            if (count >= 3 && sample[0] == 0xef && sample[1] == 0xbb && sample[2] == 0xbf) return Encoding.UTF8;
            try
            {
                // 样本可能停在多字节字符中间，只有读到文件末尾才清空解码器。
                var decoder = new UTF8Encoding(false, true).GetDecoder();
                decoder.GetCharCount(sample, 0, count, count == stream.Length);
                return Encoding.UTF8;
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(936);
            }
        }
        finally { stream.Position = position; }
    }

    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Detect(stream), true);
        return reader.ReadToEnd();
    }
}
