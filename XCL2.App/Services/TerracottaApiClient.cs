using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// Terracotta v0.4.2 的原生 HTTP 控制客户端。只调用上游存在的接口，变更后读取实际状态验证，
/// 不将 /state/scanning 返回的 HTTP 200 直接当成建房成功。可注入 HttpClient 供协议回归使用。
/// </summary>
public sealed class TerracottaApiClient
{
    private readonly HttpClient _http;
    // 上游核心是系统单例。跨页面串行变更，避免同一启动器的创建/加入/离开互相覆盖。
    private static readonly SemaphoreSlim CommandGate = new(1, 1);
    private const int JsonLimitBytes = 512 * 1024;

    public TerracottaApiClient(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<TerracottaMetadata> GetMetadataAsync(Uri endpoint, CancellationToken token = default)
        => TerracottaProtocol.ParseMetadata(await GetJsonAsync(TerracottaProtocol.CreateApiUri(endpoint, TerracottaApiOperation.Metadata), token).ConfigureAwait(false));

    public async Task<TerracottaRoomState> GetStateAsync(Uri endpoint, CancellationToken token = default)
        => TerracottaProtocol.ParseState(await GetJsonAsync(TerracottaProtocol.CreateApiUri(endpoint, TerracottaApiOperation.State), token).ConfigureAwait(false));

    public Task<TerracottaRoomState> CreateRoomAsync(Uri endpoint, string? room = null, string? player = null,
        IReadOnlyList<string>? publicNodes = null, CancellationToken token = default)
        => StartRoomAsync(endpoint, TerracottaApiOperation.CreateRoom, room, player, publicNodes, token);

    public Task<TerracottaRoomState> JoinRoomAsync(Uri endpoint, string roomCode, string? player = null,
        IReadOnlyList<string>? publicNodes = null, CancellationToken token = default)
        => StartRoomAsync(endpoint, TerracottaApiOperation.JoinRoom, roomCode, player, publicNodes, token);

    private async Task<TerracottaRoomState> StartRoomAsync(Uri endpoint, TerracottaApiOperation operation,
        string? room, string? player, IReadOnlyList<string>? publicNodes, CancellationToken token)
    {
        // 输入校验在任何网络变更之前完成，空白 custom room 才允许随机生成。
        var request = TerracottaProtocol.CreateApiUri(endpoint, operation, room, player, publicNodes);
        await CommandGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = await GetStateAsync(endpoint, token).ConfigureAwait(false);
            if (!before.CanCreateOrJoin)
                throw new InvalidOperationException("陶瓦当前尚未处于等待状态：" + before.StatusText + "。请先离开房间或结束当前操作。");
            await SendCommandAsync(request, token).ConfigureAwait(false);
            var after = await ReadCommandStateAsync(endpoint, token).ConfigureAwait(false);
            if (after.Mode == TerracottaRoomMode.Exception) return after;
            var accepted = operation == TerracottaApiOperation.CreateRoom ? after.IsHost : after.IsGuest;
            if (!string.IsNullOrWhiteSpace(room) && after.RoomCode != null &&
                TerracottaProtocol.TryNormalizeRoomCode(room, out var requestedCode) &&
                TerracottaProtocol.TryNormalizeRoomCode(after.RoomCode, out var actualCode) && requestedCode != actualCode)
                throw new TerracottaCommandException("陶瓦当前房间码与本次请求不一致，核心可能已被其他客户端切换。请刷新状态后确认实际房间。");
            if (!accepted)
                throw new TerracottaCommandException("陶瓦没有确认本次" + (operation == TerracottaApiOperation.CreateRoom ? "创建" : "加入") +
                    "请求。当前状态：" + after.StatusText + "。请刷新状态后再决定是否重试。");
            return after;
        }
        finally { CommandGate.Release(); }
    }

    public async Task<TerracottaRoomState> LeaveRoomAsync(Uri endpoint, CancellationToken token = default)
    {
        var request = TerracottaProtocol.CreateApiUri(endpoint, TerracottaApiOperation.LeaveRoom);
        await CommandGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await SendCommandAsync(request, token).ConfigureAwait(false);
            var after = await ReadCommandStateAsync(endpoint, token).ConfigureAwait(false);
            if (after.Mode != TerracottaRoomMode.Waiting)
                throw new TerracottaCommandException("陶瓦尚未确认离开房间。当前状态：" + after.StatusText + "。请刷新状态后重试。");
            return after;
        }
        finally { CommandGate.Release(); }
    }

    /// <summary>退出全局陶瓦核心，会影响共用该核心的其他页面/启动器；页面卸载不得调用。</summary>
    public async Task<TerracottaServiceStopResult> StopServiceAsync(Uri endpoint, CancellationToken token = default)
    {
        var request = TerracottaProtocol.CreateApiUri(endpoint, TerracottaApiOperation.StopService);
        await CommandGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // 先确认当前监听者确为兼容的陶瓦服务，禁止盲目向旧端口发退出命令。
            await GetMetadataAsync(endpoint, token).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await _http.GetAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new TerracottaCommandException("陶瓦未确认退出服务请求（HTTP " + (int)response.StatusCode + "）。");
            }
            // 上游 /panic?peaceful=true 直接 process::exit(0)，可能先关连接而没有 HTTP 响应。
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            var refused = 0;
            for (var i = 0; i < 4; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await GetMetadataAsync(endpoint, token).ConfigureAwait(false);
                    refused = 0;
                }
                catch (HttpRequestException ex) when (IsConnectionRefused(ex)) { refused++; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { return new(false, "退出请求已发送，但本机服务响应超时，尚未确认停止。请刷新状态。"); }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                { return new(false, "退出请求已发送，但未能确认服务已停止。请刷新状态。"); }
                if (refused >= 2) return new(true, "陶瓦本机服务已停止监听。");
                await Task.Delay(200, token).ConfigureAwait(false);
            }
            return new(false, "陶瓦仍在响应，尚未确认停止服务。请刷新后重试。");
        }
        finally { CommandGate.Release(); }
    }

    private static bool IsConnectionRefused(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused) return true;
        return false;
    }

    private async Task<TerracottaRoomState> ReadCommandStateAsync(Uri endpoint, CancellationToken token)
    {
        try { return await GetStateAsync(endpoint, token).ConfigureAwait(false); }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException) && !token.IsCancellationRequested)
        {
            throw new TerracottaCommandException("请求已发送，但未能读取陶瓦确认状态。请刷新状态，避免重复提交。", ex);
        }
    }

    private async Task SendCommandAsync(Uri request, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.GetAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new InvalidOperationException("陶瓦拒绝了请求：房间码无效，或当前核心状态不允许此操作。请刷新状态并重新核对房间码。");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new TerracottaCommandException("陶瓦未确认操作（HTTP " + (int)response.StatusCode + "）。请刷新状态后重试。");
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TerracottaCommandException("等待陶瓦响应超时；请求可能已经执行。请先刷新状态，避免重复提交。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TerracottaCommandException("与陶瓦的连接中断，操作结果未确认。请刷新状态后重试。", ex);
        }
    }

    private async Task<string> GetJsonAsync(Uri request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _http.GetAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("读取陶瓦状态失败（HTTP " + (int)response.StatusCode + "）。", null, response.StatusCode);
        return await ReadBoundedTextAsync(response.Content, JsonLimitBytes, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("陶瓦返回的数据超出允许大小。");
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (data.Length + count > limit) throw new InvalidDataException("陶瓦返回的数据超出允许大小。");
            data.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(data.ToArray());
    }
}
