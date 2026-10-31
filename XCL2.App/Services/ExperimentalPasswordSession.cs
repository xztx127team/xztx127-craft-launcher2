using System.Security.Cryptography;
using System.Text;

namespace XCL2.App.Services;

public sealed class ExperimentalPasswordSession
{
    private sealed record Ticket(byte[] Hash, DateTimeOffset ExpiresAt, int Attempts);
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
    private readonly Dictionary<string, string> _passwords = ExperimentalFeatureCatalog.All
        .ToDictionary(feature => feature.Id, _ => CreatePassword(), StringComparer.Ordinal);
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    // 每个进程预先生成独立密码；查看只重新授权十分钟，不改变本次启动密码。
    private static string CreatePassword()
    {
        var length = RandomNumberGenerator.GetInt32(10, 26);
        return new string(Enumerable.Range(0, length)
            .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
    }

    public string Issue(string featureId)
    {
        var feature = ExperimentalFeatureCatalog.All.Single(f => f.Id == featureId);
        lock (_sync)
        {
            var password = _passwords[feature.Id];
            Revoke(featureId);
            _tickets[featureId] = new(Hash(password), DateTimeOffset.UtcNow.AddMinutes(10), 0);
            return password;
        }
    }

    public bool Consume(string featureId, string? input)
    {
        lock (_sync)
        {
            if (!_tickets.TryGetValue(featureId, out var ticket)) return false;
            if (DateTimeOffset.UtcNow >= ticket.ExpiresAt) { Revoke(featureId); return false; }
            var actual = Hash(input ?? "");
            bool match = CryptographicOperations.FixedTimeEquals(actual, ticket.Hash);
            CryptographicOperations.ZeroMemory(actual);
            if (match || ticket.Attempts >= 4) Revoke(featureId);
            else _tickets[featureId] = ticket with { Attempts = ticket.Attempts + 1 };
            return match;
        }
    }

    public void Revoke(string featureId)
    {
        lock (_sync)
            if (_tickets.Remove(featureId, out var ticket)) CryptographicOperations.ZeroMemory(ticket.Hash);
    }

    private static byte[] Hash(string input) => SHA256.HashData(Encoding.UTF8.GetBytes(input.Trim().ToUpperInvariant()));
}
