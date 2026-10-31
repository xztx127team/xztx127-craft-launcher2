using System.IO;
using System.Text;

namespace XCL2.App.Services;

// Capacity violations are protocol failures, not a transient transport eligible for replay.
internal sealed class AiResponseLimitException(string message) : IOException(message);

/// <summary>Bounds actual received data, including providers that omit or misreport Content-Length.</summary>
internal static class AiResponseLimits
{
    internal const int MaxResponseBytes = 8 * 1024 * 1024;
    internal const int MaxSseLineChars = 2_000_000;
    internal const int MaxSseFrameChars = 2_000_000;
    // Answer, public reasoning and reasoning summary share this budget.
    internal const int MaxGeneratedChars = 2_000_000;

    internal static async Task<string> ReadResponseTextAsync(Stream stream, CancellationToken ct,
        Encoding? encoding = null)
    {
        using var body = new MemoryStream(64 * 1024);
        var buffer = new byte[8192];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // At the limit, read at most one extra byte to distinguish exact EOF from overflow.
            int requested = Math.Min(buffer.Length, MaxResponseBytes - checked((int)body.Length) + 1);
            int read = await stream.ReadAsync(buffer.AsMemory(0, requested), ct).ConfigureAwait(false);
            if (read == 0) break;
            if (read > MaxResponseBytes - body.Length)
                throw new AiResponseLimitException("AI 接口响应超过 8 MiB 上限，已停止读取；未执行不完整指令。");
            body.Write(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        body.Position = 0;
        using var reader = new StreamReader(body, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Unlike ReadLineAsync, rejects an overlong line before allocating the whole line.</summary>
    internal sealed class BoundedLineReader
    {
        private readonly TextReader _reader;
        private readonly int _maximum;
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _count;
        private bool _skipLf;

        internal BoundedLineReader(TextReader reader, int maximum = MaxSseLineChars)
        {
            if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
            _reader = reader;
            _maximum = maximum;
        }

        internal async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var line = new StringBuilder();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_position == _count)
                {
                    _count = await _reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                    _position = 0;
                    if (_count == 0) return line.Length == 0 ? null : line.ToString();
                }
                if (_skipLf)
                {
                    _skipLf = false;
                    if (_buffer[_position] == '\n') { _position++; continue; }
                }
                int start = _position;
                while (_position < _count && _buffer[_position] is not ('\r' or '\n')) _position++;
                int length = _position - start;
                if (length > _maximum - line.Length)
                    throw new AiResponseLimitException("SSE 单行超过长度上限，已停止读取；未执行不完整指令。");
                line.Append(_buffer, start, length);
                if (_position < _count)
                {
                    _skipLf = _buffer[_position++] == '\r';
                    return line.ToString();
                }
            }
        }
    }
}
