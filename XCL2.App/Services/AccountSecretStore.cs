using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>账户文件仅保存个人资料；令牌不写盘，也不恢复旧的凭据缓存。</summary>
internal static class AccountSecretStore
{
    internal static List<Account> Load(string accountsPath, JsonSerializerOptions options)
    {
        RemoveLegacyVault(accountsPath);
        // JsonIgnore also prevents legacy plaintext tokens from becoming live
        // sessions. Rewrite the profile to remove those obsolete secret fields.
        var accounts = JsonSerializer.Deserialize<List<Account>>(File.ReadAllBytes(accountsPath), options) ?? new();
        accounts.RemoveAll(account => account == null);
        Save(accountsPath, accounts, options);
        return accounts;
    }

    internal static void Save(string accountsPath, IEnumerable<Account> accounts, JsonSerializerOptions options)
    {
        RemoveLegacyVault(accountsPath);
        // Do not clear tokens on live objects: profile saves must not log out
        // the current running session. Account excludes all token properties.
        AtomicWrite(accountsPath, JsonSerializer.SerializeToUtf8Bytes(accounts.ToArray(), options));
    }

    internal static void RemoveLegacyVault(string accountsPath)
    {
        var directory = Path.GetDirectoryName(accountsPath)!;
        if (!Directory.Exists(directory)) return;
        const string vaultName = "account-secrets.dpapi";
        File.Delete(Path.Combine(directory, vaultName));
        // Older atomic writes could leave encrypted credential files behind
        // after interruption. Only remove the exact GUID names that writer used;
        // current profile temporary files are credential-free and may be in use.
        foreach (var temporary in Directory.EnumerateFiles(directory, vaultName + ".*.tmp", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(temporary);
            if (name.Length != vaultName.Length + 1 + 32 + 4 ||
                !name.StartsWith(vaultName + ".", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var identifier = name[(vaultName.Length + 1)..^4];
            if (Guid.TryParseExact(identifier, "N", out _)) File.Delete(temporary);
        }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
