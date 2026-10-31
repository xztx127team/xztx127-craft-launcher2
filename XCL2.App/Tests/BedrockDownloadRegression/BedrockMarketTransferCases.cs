using System.Net;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class BedrockMarketTransferCases
{
    internal static async Task RunAsync(Action<bool, string> check, string parent, byte[] validPackage)
    {
        var root = Path.Combine(parent, "transfer-cases");
        Directory.CreateDirectory(root);
        BedrockMarketFile File(string name) => new() { Name = name, DownloadUrl = "https://github.com/author/repo/releases/download/v1/pack.mcpack" };
        void Cleaned(DownloadTransferPolicy.TransferProbe probe)
        {
            check(probe.Temporaries.Count == 1 && !probe.Temporaries[0].Resumable,
                "market temporary is registered without promising unsupported resume");
            check(probe.Temporaries.All(t => !System.IO.File.Exists(t.Path)), "market failure/cancel removes its registered temporary");
            check(probe.ActivePhases == 0, "all market observation phases end after completion");
        }
        async Task TimedOut(Func<Task> action, string fragment)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Expected downloader timeout."); }
            catch (TimeoutException ex)
            {
                check(ex.Message.Contains(fragment, StringComparison.Ordinal), "download timeout has a useful stage-specific explanation");
            }
        }
        async Task Canceled(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("User cancellation ignored."); }
            catch (OperationCanceledException) { check(true, "user cancellation stays cancellation rather than timeout"); }
        }

        using (var probe = DownloadTransferPolicy.Observe())
        using (var service = new BedrockMarketplaceDownloadService(new TransferHandler(async (_, ct) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new HttpResponseMessage(HttpStatusCode.OK); }), TimeSpan.FromMilliseconds(60)))
        {
            await TimedOut(() => service.DownloadAsync(File("header.mcpack"), root), "连接资源服务器");
            check(probe.Phases.Contains("连接") && probe.Completed.Count == 0, "header stall is observed as connection and not completed");
            Cleaned(probe);
        }
        using (var probe = DownloadTransferPolicy.Observe())
        using (var service = new BedrockMarketplaceDownloadService(new TransferHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PausedBody(validPackage[..16])) })), TimeSpan.FromMilliseconds(60)))
        {
            await TimedOut(() => service.DownloadAsync(File("body.mcpack"), root), "没有返回下载数据");
            check(probe.Received == 16 && probe.Phases.Contains("传输") && probe.Completed.Count == 0,
                "body stall records actual bytes and transfer phase before failing");
            Cleaned(probe);
        }
        using (var probe = DownloadTransferPolicy.Observe())
        using (var service = new BedrockMarketplaceDownloadService(new TransferHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(validPackage) })), TimeSpan.FromMilliseconds(40)))
        {
            probe.Consume = (_, ct) => Task.Delay(160, ct); // Longer than network-idle timeout; must remain legitimate throttling.
            var file = File("throttled.mcpack");
            System.IO.File.WriteAllText(Path.Combine(root, file.Name), "existing file must survive");
            var path = await service.DownloadAsync(file, root).WaitAsync(TimeSpan.FromSeconds(5));
            check(System.IO.File.ReadAllBytes(path).SequenceEqual(validPackage), "throttling longer than idle deadline does not time out valid download");
            check(probe.Received == validPackage.Length && probe.Completed.SequenceEqual(new[] { path }) && probe.Target == path,
                "completed observation uses actual collision-safe destination and network byte count");
            check(probe.Phases.Contains("连接") && probe.Phases.Contains("传输") && probe.Phases.Contains("校验"), "all market phases are visible");
            check(System.IO.File.ReadAllText(Path.Combine(root, file.Name)) == "existing file must survive", "commit still preserves existing resource");
            Cleaned(probe);
        }
        using (var cancellation = new CancellationTokenSource())
        using (var probe = DownloadTransferPolicy.Observe())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new BedrockMarketplaceDownloadService(new TransferHandler(async (_, ct) =>
            { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new HttpResponseMessage(HttpStatusCode.OK); }), TimeSpan.FromSeconds(3));
            var task = service.DownloadAsync(File("cancel-header.mcpack"), root, ct: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); await Canceled(task);
            check(probe.Completed.Count == 0, "canceling connection never commits a file");
            Cleaned(probe);
        }
        using (var cancellation = new CancellationTokenSource())
        using (var probe = DownloadTransferPolicy.Observe())
        {
            var body = new PausedBody(Array.Empty<byte>());
            using var service = new BedrockMarketplaceDownloadService(new TransferHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })), TimeSpan.FromSeconds(3));
            var task = service.DownloadAsync(File("cancel-body.mcpack"), root, ct: cancellation.Token);
            await body.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); await Canceled(task);
            check(probe.Completed.Count == 0, "canceling blocked body read never commits a file");
            Cleaned(probe);
        }
        using (var cancellation = new CancellationTokenSource())
        using (var probe = DownloadTransferPolicy.Observe())
        using (var service = new BedrockMarketplaceDownloadService(new TransferHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(validPackage) })), TimeSpan.FromMilliseconds(40)))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            probe.Consume = async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
            var task = service.DownloadAsync(File("cancel-throttle.mcpack"), root, ct: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); await Canceled(task);
            check(probe.Completed.Count == 0, "user can cancel a global-limit wait");
            Cleaned(probe);
        }
    }

    private sealed class TransferHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class PausedBody(byte[] prefix) : Stream
    {
        private int _position;
        internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
            }
            Paused.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
