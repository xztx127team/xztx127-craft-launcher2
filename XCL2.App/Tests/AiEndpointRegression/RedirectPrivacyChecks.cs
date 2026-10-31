using System.Net;
using System.Net.Sockets;
using System.Text;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class RedirectPrivacyChecks
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        foreach (var status in new[] { 301, 302, 303, 307, 308 })
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var source = new TcpListener(IPAddress.Loopback, 0);
            using var recipient = new TcpListener(IPAddress.Loopback, 0);
            source.Start(); recipient.Start();
            var recipientUrl = $"http://127.0.0.1:{((IPEndPoint)recipient.LocalEndpoint).Port}/collect";
            var recipientRequest = ServeAsync(recipient, 200, null, stop.Token);
            var sourceRequest = ServeAsync(source, status, recipientUrl, stop.Token);
            using var http = AiEndpointRouting.CreateHttpClient(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(method,
                $"http://127.0.0.1:{((IPEndPoint)source.LocalEndpoint).Port}/v1/messages");
            AiEndpointRouting.ApplyAuthentication(request, "synthetic-provider-key", AiApiProtocol.Anthropic);
            if (method == HttpMethod.Post)
                request.Content = new StringContent("{\"messages\":[\"private-chat-and-attachment\"]}", Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, stop.Token);
            var first = await sourceRequest;
            stop.Cancel();
            var redirected = await recipientRequest;
            if ((int)response.StatusCode != status || redirected != null)
                throw new InvalidOperationException($"{status} {method}: AI traffic followed an unapproved redirect.");
            if (first == null || !first.Contains("synthetic-provider-key", StringComparison.Ordinal)
                || method == HttpMethod.Post && !first.Contains("private-chat-and-attachment", StringComparison.Ordinal))
                throw new InvalidOperationException("The authorized endpoint did not receive the original request.");
            checks += 2;
        }
        return checks;
    }

    private static async Task<string?> ServeAsync(TcpListener listener, int status, string? location, CancellationToken ct)
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            var captured = new StringBuilder();
            var contentLength = 0;
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) break;
                captured.AppendLine(line);
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line[15..].Trim());
            }
            var body = new char[contentLength];
            var offset = 0;
            while (offset < body.Length)
            {
                var read = await reader.ReadAsync(body.AsMemory(offset), ct);
                if (read == 0) throw new IOException("Incomplete synthetic request.");
                offset += read;
            }
            captured.Append(body);
            var headers = $"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\n"
                + (location == null ? "" : "Location: " + location + "\r\n") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
            return captured.ToString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
    }
}
