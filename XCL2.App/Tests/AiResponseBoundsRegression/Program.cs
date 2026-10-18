using System.Text;
using System.Text.Json;
using XCL2.App.Services;

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
async Task Throws<T>(Func<Task> action, string message) where T : Exception
{
    checks++;
    try { await action(); } catch (T) { return; }
    throw new Exception(message);
}
string Frame(object delta) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta } } }) + "\n\n";
MemoryStream Utf8(string value) => new(Encoding.UTF8.GetBytes(value));

// The sum includes both reasoning channels; an offending delta is never partially appended.
var payload = Frame(new { content = "kept" })
    + Frame(new { reasoning_content = new string('r', 750_000) })
    + Frame(new { reasoning_summary = new string('s', 750_000) })
    + Frame(new { content = new string('x', 600_000) }) + "data: [DONE]\n\n";
string answer = "", thought = "", summary = "";
int started = 0;
using (var stream = Utf8(payload))
    await Throws<IOException>(async () => await AiStreamingCompletion.ReadAsync(stream, x => answer = x,
        (r, s) => { thought = r; summary = s; }, default, responseStarted: () => started++), "Shared output cap must reject multi-frame reasoning overflow.");
Check(answer == "kept" && thought.Length == 750_000 && summary.Length == 750_000, "Previously received output must survive overflow.");
Check(started == 1, "Partial response must disable replay once, including before a limit failure.");

// Pure reasoning is bounded, even when a provider never emits an answer or finish frame.
using (var stream = Utf8(string.Concat(Enumerable.Repeat(Frame(new { reasoning_content = new string('r', 700_000) }), 3))))
    await Throws<IOException>(async () => await AiStreamingCompletion.ReadAsync(stream, null, null, default), "Reasoning-only response must be bounded.");

// A deliberately huge, unterminated SSE line is rejected before the entire line is read.
using (var stream = new RepeatedByteStream(AiResponseLimits.MaxSseLineChars + 100_000, (byte)'x', 1024))
{
    await Throws<IOException>(async () => await AiStreamingCompletion.ReadAsync(stream, null, null, default), "Unterminated SSE line must fail.");
    Check(stream.BytesRead < stream.Length && stream.BytesRead <= AiResponseLimits.MaxSseLineChars + 8192,
        "Line rejection must happen during bounded incremental reads.");
}

// CRLF may be split across reads. Exact-limit lines and the last EOF line remain compatible.
var lines = new AiResponseLimits.BoundedLineReader(new ChunkedTextReader("abcd\r\n\r\nx\ry", 1), 4);
Check(await lines.ReadLineAsync(default) == "abcd", "Exact-length CRLF line accepted.");
Check(await lines.ReadLineAsync(default) == "", "Blank SSE separator retained.");
Check(await lines.ReadLineAsync(default) == "x" && await lines.ReadLineAsync(default) == "y"
    && await lines.ReadLineAsync(default) == null, "Bare CR and EOF semantics retained.");

// Ignore Content-Length entirely: actual non-seekable/chunked bytes determine the limit.
using (var stream = new RepeatedByteStream(AiResponseLimits.MaxResponseBytes + 100_000, (byte)'a', 113))
{
    await Throws<IOException>(async () => await AiResponseLimits.ReadResponseTextAsync(stream, default), "Chunked response over byte limit must fail.");
    Check(stream.BytesRead == AiResponseLimits.MaxResponseBytes + 1, "Byte overflow reads only one extra byte.");
}
using (var stream = new RepeatedByteStream(AiResponseLimits.MaxResponseBytes, (byte)'a', 8192))
    Check((await AiResponseLimits.ReadResponseTextAsync(stream, default)).Length == AiResponseLimits.MaxResponseBytes, "Exact byte limit accepted.");
using (var stream = Utf8("\uFEFF你好"))
    Check(await AiResponseLimits.ReadResponseTextAsync(stream, default) == "你好", "UTF-8 BOM and multibyte text decoded.");

// Cancellation during a pending network read is observed by both readers.
using (var canceled = new CancellationTokenSource(20))
using (var stream = new BlockingStream())
    await Throws<OperationCanceledException>(async () => await AiResponseLimits.ReadResponseTextAsync(stream, canceled.Token), "HTTP reader must propagate cancellation.");
using (var canceled = new CancellationTokenSource(20))
using (var stream = new BlockingStream())
    await Throws<OperationCanceledException>(async () => await AiStreamingCompletion.ReadAsync(stream, null, null, canceled.Token), "SSE reader must propagate cancellation.");

// Hidden control-prefix output still signals no replay; malformed tails preserve visible text.
started = 0;
using (var stream = Utf8(Frame(new { content = "safe <<XCL2_" }) + "data: {broken\n\n"))
    await Throws<InvalidOperationException>(async () => await AiStreamingCompletion.ReadAsync(stream, x => answer = x,
        null, default, responseStarted: () => started++), "Malformed tail remains a protocol failure.");
Check(answer == "safe" && started == 1, "Hidden incomplete control text is not displayed or replayed.");
using (var stream = Utf8(Frame(new { content = "ok" }) + "data: [DONE]\n\n"))
    Check(await AiStreamingCompletion.ReadAsync(stream, null, null, default) == "ok", "Normal SSE completion unchanged.");
Console.WriteLine($"AiResponseBoundsRegression: {checks} checks passed.");

sealed class ChunkedTextReader(string text, int chunk) : TextReader
{
    private int _position;
    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int n = Math.Min(Math.Min(chunk, buffer.Length), text.Length - _position);
        text.AsSpan(_position, n).CopyTo(buffer.Span); _position += n;
        return ValueTask.FromResult(n);
    }
}
sealed class RepeatedByteStream(long length, byte value, int chunk) : Stream
{
    public long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = (int)Math.Min(Math.Min(count, chunk), length - BytesRead);
        buffer.AsSpan(offset, n).Fill(value); BytesRead += n; return n;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int n = (int)Math.Min(Math.Min(buffer.Length, chunk), length - BytesRead);
        buffer.Span[..n].Fill(value); BytesRead += n; return ValueTask.FromResult(n);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
sealed class BlockingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
