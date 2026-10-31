using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class NativeProtocolCases
{
    private const string Room = "U/0000-0000-0000-0000"; // 官方 base34 checksum 为 0 % 7。
    private static readonly Uri Endpoint = new("http://127.0.0.1:35678/");
    private const string Waiting = "{\"state\":\"waiting\",\"index\":0}";
    private const string Scanning = "{\"state\":\"host-scanning\",\"index\":1}";
    private const string Connecting = "{\"state\":\"guest-connecting\",\"index\":1,\"room\":\"U/0000-0000-0000-0000\"}";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Invalid(Action action, string description)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (ArgumentException) { return; }
        throw new Exception(description);
    }
    private static async Task RejectAsync<T>(Func<Task<T>> action, string description) where T : class
    {
        try { await action(); }
        catch (TerracottaCommandException) { return; }
        catch (InvalidOperationException) { return; }
        throw new Exception(description);
    }

    public static async Task RunAsync()
    {
        Check(TerracottaProtocol.TryNormalizeRoomCode("加入该房间：u/OOOO-0000-0000-0000，等你", out var normalized) && normalized == Room,
            "Same room-code extraction and I/O aliases as upstream");
        Check(!TerracottaProtocol.TryNormalizeRoomCode("U/1000-0000-0000-0000", out _), "Incorrect base34 checksum was accepted");
        Check(!TerracottaProtocol.TryNormalizeRoomCode("U/0000_0000-0000-0000", out _), "Incorrect separator was accepted");
        Check(!TerracottaProtocol.TryNormalizeRoomCode("just a room name", out _), "Room name silently became a code");
        var create = TerracottaProtocol.CreateApiUri(Endpoint, TerracottaApiOperation.CreateRoom, null, "你&我?", new[] { "tcp://node.example:11010", "https://node.example/list?a=1&b=2" });
        Check(create.AbsolutePath == "/state/scanning", "Create route differs from upstream");
        Check(!create.Query.Contains("room=") && create.Query.Contains("player=%E4%BD%A0%26%E6%88%91%3F") && create.Query.Split("public_nodes=").Length == 3,
            "Advanced settings were omitted, appended unsafely or collapsed repeated public_nodes");
        Check(Uri.UnescapeDataString(create.Query).Contains("https://node.example/list?a=1&b=2"), "Node query encoding was damaged");
        Invalid(() => TerracottaProtocol.CreateApiUri(Endpoint, TerracottaApiOperation.CreateRoom, "invalid code"), "Invalid custom code silently generates a different room");
        Invalid(() => TerracottaProtocol.CreateApiUri(Endpoint, TerracottaApiOperation.JoinRoom), "Empty join code accepted");
        Invalid(() => TerracottaProtocol.CreateApiUri(Endpoint, TerracottaApiOperation.JoinRoom, Room, "line\nbreak"), "Control character in player accepted");
        Invalid(() => TerracottaProtocol.CreateApiUri(new Uri("https://example.com/"), TerracottaApiOperation.State), "Remote API origin accepted");
        Invalid(() => TerracottaProtocol.CreateApiUri(new Uri("http://127.0.0.1:35678/other"), TerracottaApiOperation.State), "Unexpected root path accepted");
        Invalid(() => TerracottaProtocol.CreateApiUri(Endpoint, TerracottaApiOperation.CreateRoom, null, null, new[] { "file:///tmp/node" }), "Non-network public node accepted");

        var metadata = TerracottaProtocol.ParseMetadata("{\"version\":\"0.4.2\",\"compile_timestamp\":\"123\",\"easytier_version\":\"2.5.0-terracotta.2\",\"yggdrasil_port\":13448,\"target_tuple\":\"x86_64-pc-windows-msvc\",\"target_arch\":\"x86_64\",\"target_vendor\":\"pc\",\"target_os\":\"windows\",\"target_env\":\"msvc\"}");
        Check(metadata is { Version: "0.4.2", YggdrasilPort: 13448, TargetVendor: "pc", TargetEnvironment: "msvc" }, "Metadata fields differ from v0.4.2");
        foreach (var json in new[] { "[]", "{}", "{\"version\":\"\",\"easytier_version\":\"x\"}", "{\"version\":\"0.4.2\",\"easytier_version\":\"x\",\"yggdrasil_port\":0}" })
            Invalid(() => TerracottaProtocol.ParseMetadata(json), "Invalid metadata accepted as a compatible local service");
        var hosting = TerracottaProtocol.ParseState("{\"state\":\"host-ok\",\"index\":9,\"profile_index\":3,\"room\":\"U/0000-0000-0000-0000\",\"profiles\":[{\"machine_id\":\"a\",\"name\":\"房主\",\"vendor\":\"Terracotta\",\"kind\":\"HOST\"},{\"machine_id\":\"b\",\"name\":\"\",\"vendor\":\"\",\"kind\":\"GUEST\"}]}");
        Check(hosting.Mode == TerracottaRoomMode.Hosting && hosting.RoomCode == Room && hosting.Members.Count == 2 && hosting.ProfileIndex == 3,
            "Hosted room/member state mapping");
        Check(hosting.Members[0].RoleText == "房主" && hosting.Members[1].Name == "未命名成员", "Valid empty member name must not break the room");
        var joined = TerracottaProtocol.ParseState("{\"state\":\"guest-ok\",\"index\":2,\"url\":\"127.0.0.1:45678\",\"profiles\":[]}");
        Check(joined.IsConnected && joined.IsGuest && joined.RoomCode == null && joined.ConnectAddress == "127.0.0.1:45678", "Guest-ok must not fabricate a room code");
        Check(TerracottaProtocol.ParseState("{\"state\":\"guest-starting\",\"index\":3,\"room\":\"U/0000-0000-0000-0000\",\"difficulty\":\"TOUGH\"}").DifficultyText!.Contains("较高"), "Actual connection difficulty");
        Check(TerracottaProtocol.ParseState("{\"state\":\"future-state\",\"index\":4}").Mode == TerracottaRoomMode.Unknown, "Unknown state must not become successful connection");
        for (var code = 0; code <= 5; code++)
        {
            var failure = TerracottaProtocol.ParseState("{\"state\":\"exception\",\"index\":3,\"type\":" + code + "}").Failure;
            Check(failure?.Code == code && !string.IsNullOrWhiteSpace(failure.RepairSteps), "Missing upstream failure mapping or repair hint");
        }
        foreach (var json in new[] { "[]", "null", "{", "{}", "{\"state\":\"waiting\",\"index\":\"0\"}",
            "{\"state\":\"guest-ok\",\"index\":1,\"url\":\"example.com:25565\",\"profiles\":[]}",
            "{\"state\":\"guest-ok\",\"index\":1,\"url\":\"127.0.0.1:0\",\"profiles\":[]}",
            "{\"state\":\"host-ok\",\"index\":1,\"room\":\"U/0000-0000-0000-0000\"}" })
            Invalid(() => TerracottaProtocol.ParseState(json), "Malformed/incomplete state looked successful: " + json);

        using (var fixture = new Fixture(("/state", Waiting, HttpStatusCode.OK), ("/state/scanning", "", HttpStatusCode.OK), ("/state", Scanning, HttpStatusCode.OK)))
        {
            Check((await fixture.Api.CreateRoomAsync(Endpoint)).Mode == TerracottaRoomMode.HostScanning, "Accepted creation remains scanning, not already hosting");
            fixture.VerifyComplete();
        }
        using (var fixture = new Fixture(("/state", Waiting, HttpStatusCode.OK), ("/state/scanning", "", HttpStatusCode.OK), ("/state", Waiting, HttpStatusCode.OK)))
        {
            await RejectAsync(() => fixture.Api.CreateRoomAsync(Endpoint), "HTTP 200 with unchanged waiting state was reported as successful creation");
            fixture.VerifyComplete();
        }
        using (var fixture = new Fixture(("/state", Scanning, HttpStatusCode.OK)))
        {
            await RejectAsync(() => fixture.Api.CreateRoomAsync(Endpoint), "Creation sent while core is already busy");
            fixture.VerifyComplete();
        }
        var joinPath = TerracottaProtocol.CreateApiUri(Endpoint, TerracottaApiOperation.JoinRoom, Room, "名字&?", new[] { "tcp://node.example:11010" }).PathAndQuery;
        using (var fixture = new Fixture(("/state", Waiting, HttpStatusCode.OK), (joinPath, "", HttpStatusCode.OK), ("/state", Connecting, HttpStatusCode.OK)))
        {
            Check((await fixture.Api.JoinRoomAsync(Endpoint, Room, "名字&?", new[] { "tcp://node.example:11010" })).Mode == TerracottaRoomMode.GuestConnecting, "Native join parameters/confirmation");
            fixture.VerifyComplete();
        }
        using (var fixture = new Fixture(("/state", Waiting, HttpStatusCode.OK), (TerracottaProtocol.CreateApiUri(Endpoint, TerracottaApiOperation.JoinRoom, Room).PathAndQuery, "", HttpStatusCode.BadRequest)))
        {
            await RejectAsync(() => fixture.Api.JoinRoomAsync(Endpoint, Room), "HTTP 400 must not become successful join");
            fixture.VerifyComplete();
        }
        using (var fixture = new Fixture(("/state/ide", "", HttpStatusCode.OK), ("/state", Waiting, HttpStatusCode.OK)))
        {
            Check((await fixture.Api.LeaveRoomAsync(Endpoint)).Mode == TerracottaRoomMode.Waiting, "Leaving did not verify waiting");
            fixture.VerifyComplete();
        }
        using (var fixture = new Fixture(("/state", Waiting, HttpStatusCode.OK), ("/state/scanning", "", HttpStatusCode.TemporaryRedirect)))
        {
            await RejectAsync(() => fixture.Api.CreateRoomAsync(Endpoint), "Redirect should not count as a confirmed native command");
            fixture.VerifyComplete();
        }
        const string metaJson = "{\"version\":\"0.4.2\",\"easytier_version\":\"2.5.0-terracotta.2\"}";
        using (var fixture = new Fixture(("/meta", metaJson, HttpStatusCode.OK), ("/panic?peaceful=true", ":connection-refused", HttpStatusCode.OK),
            ("/meta", ":connection-refused", HttpStatusCode.OK), ("/meta", ":connection-refused", HttpStatusCode.OK)))
        {
            Check((await fixture.Api.StopServiceAsync(Endpoint)).ConfirmedUnavailable, "Shutdown should confirm repeated local connection refusal, not require a response from an exiting process");
            fixture.VerifyComplete();
        }
        using (var fixture = new Fixture(("/meta", metaJson, HttpStatusCode.OK), ("/panic?peaceful=true", "", HttpStatusCode.OK),
            ("/meta", metaJson, HttpStatusCode.OK), ("/meta", metaJson, HttpStatusCode.OK), ("/meta", metaJson, HttpStatusCode.OK), ("/meta", metaJson, HttpStatusCode.OK)))
        {
            Check(!(await fixture.Api.StopServiceAsync(Endpoint)).ConfirmedUnavailable, "HTTP 200 must not falsely confirm that a still-listening service stopped");
            fixture.VerifyComplete();
        }
        using (var fixture = new Fixture(("/state", Waiting, HttpStatusCode.OK), ("/state/scanning", "", HttpStatusCode.OK), ("/state", "{bad json", HttpStatusCode.OK)))
        {
            await RejectAsync(() => fixture.Api.CreateRoomAsync(Endpoint), "Malformed confirmation must not become a successful room operation");
            fixture.VerifyComplete();
        }
        Console.WriteLine("Terracotta v0.4.2 native states, exact routes, encoded settings, member/failure data and confirmed command regression cases passed.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Queue<(string Path, string Body, HttpStatusCode Status)> _responses;
        private readonly HttpClient _client;
        public TerracottaApiClient Api { get; }
        public Fixture(params (string Path, string Body, HttpStatusCode Status)[] responses)
        {
            _responses = new(responses);
            _client = new HttpClient(new Handler(_responses));
            Api = new TerracottaApiClient(_client);
        }
        public void VerifyComplete() => Check(_responses.Count == 0, "Expected native HTTP requests were not issued");
        public void Dispose() => _client.Dispose();
    }
    private sealed class Handler(Queue<(string Path, string Body, HttpStatusCode Status)> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(request.Method == HttpMethod.Get && request.RequestUri?.Host == "127.0.0.1", "Command escaped actual local GET API");
            Check(responses.Count > 0, "Unexpected extra native HTTP request");
            var next = responses.Dequeue();
            Check(request.RequestUri!.PathAndQuery == next.Path, "Wrong native route: " + request.RequestUri.PathAndQuery + ", expected " + next.Path);
            if (next.Body == ":connection-refused") throw new HttpRequestException("fixture: local listener refused", new SocketException((int)SocketError.ConnectionRefused));
            return Task.FromResult(new HttpResponseMessage(next.Status) { Content = new StringContent(next.Body, Encoding.UTF8, "application/json") });
        }
    }
}
