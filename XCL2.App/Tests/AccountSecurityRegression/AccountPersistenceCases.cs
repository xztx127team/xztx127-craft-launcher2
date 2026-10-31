using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class AccountPersistenceCases
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "XCL2-account-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var path = Path.Combine(root, "accounts.json");
            var vault = Path.Combine(root, "account-secrets.dpapi");
            foreach (var type in new[] { AccountType.Microsoft, AccountType.AuthServer, AccountType.Offline })
            {
                var account = new Account
                {
                    Type = type, Username = "Player", Uuid = "0123456789abcdef0123456789abcdef",
                    MsRefreshToken = "refresh-secret-fixture", MinecraftAccessToken = "access-secret-fixture",
                    AuthServerClientToken = "client-secret-fixture", IsSelected = true
                };
                File.WriteAllText(vault, "obsolete encrypted credential fixture");
                AccountSecretStore.Save(path, new[] { account }, options);
                Require(!File.Exists(vault), "Saving an account retained the old credential vault.");
                CheckNoSecrets(File.ReadAllText(path));
                CheckNoSecrets(JsonSerializer.Serialize(account));
                Require(account.MinecraftAccessToken == "access-secret-fixture" && account.MsRefreshToken == "refresh-secret-fixture"
                    && account.AuthServerClientToken == "client-secret-fixture", "Saving the profile cleared the live session.");
                var loaded = AccountSecretStore.Load(path, options).Single();
                Require(loaded.Id == account.Id && loaded.Type == type && loaded.Username == account.Username && loaded.IsSelected,
                    "Credential-free account storage lost profile metadata.");
                Require(loaded.MinecraftAccessToken == null && loaded.MsRefreshToken == null && loaded.AuthServerClientToken == null,
                    "An account session was restored from disk.");
            }

            // A real old plaintext profile plus an obsolete encrypted vault must be
            // migrated to metadata only, without restoring either source's tokens.
            File.WriteAllText(path, "[{\"Id\":\"legacy\",\"Type\":1,\"Username\":\"Legacy\",\"MsRefreshToken\":\"refresh-secret-fixture\",\"MinecraftAccessToken\":\"access-secret-fixture\",\"AuthServerClientToken\":\"client-secret-fixture\"},null]");
            File.WriteAllText(vault, "obsolete encrypted credential fixture");
            var legacy = AccountSecretStore.Load(path, options).Single();
            Require(legacy.Id == "legacy" && legacy.Username == "Legacy", "Legacy account identity was not retained.");
            Require(legacy.MsRefreshToken == null && legacy.MinecraftAccessToken == null && legacy.AuthServerClientToken == null,
                "Legacy plaintext credentials became a live session.");
            CheckNoSecrets(File.ReadAllText(path));
            Require(!File.Exists(vault), "Legacy import retained an encrypted token file.");

            var interruptedVault = vault + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var unrelatedFile = vault + ".notes.tmp";
            File.WriteAllText(interruptedVault, "interrupted encrypted credentials");
            File.WriteAllText(unrelatedFile, "unrelated user file");
            AccountSecretStore.RemoveLegacyVault(path);
            Require(!File.Exists(interruptedVault) && File.Exists(unrelatedFile),
                "Credential cleanup retained an interrupted vault or removed an unrelated file.");

            foreach (var type in new[] { AccountType.Microsoft, AccountType.AuthServer })
            {
                var old = new Account { Type = type, Uuid = "12345678-90AB-CDEF-1234-567890ABCDEF", AuthServerApiRoot = "https://skin.example/api/" };
                var renewed = new Account { Type = type, Uuid = "1234567890abcdef1234567890abcdef", AuthServerApiRoot = "https://skin.example/api" };
                Require(ReferenceEquals(AccountUpdatePolicy.FindExisting(new[] { old }, renewed), old),
                    "Reauthentication did not find the existing profile.");
                var malformed = new Account { Type = type, Uuid = null! };
                Require(ReferenceEquals(AccountUpdatePolicy.FindExisting(new[] { malformed, old }, renewed), old),
                    "A malformed legacy UUID blocked reauthentication of another profile.");
                renewed.Type = type == AccountType.Microsoft ? AccountType.AuthServer : AccountType.Microsoft;
                Require(AccountUpdatePolicy.FindExisting(new[] { old }, renewed) == null, "Reauthentication merged different identity providers.");
                renewed.Type = type;
                if (type == AccountType.AuthServer)
                {
                    renewed.AuthServerApiRoot = "https://another.example/api";
                    Require(AccountUpdatePolicy.FindExisting(new[] { old }, renewed) == null, "Different authentication servers were merged.");
                }
            }

            File.WriteAllText(vault, "obsolete encrypted credential fixture");
            AccountSecretStore.Save(path, Array.Empty<Account>(), options);
            Require(!File.Exists(vault) && AccountSecretStore.Load(path, options).Count == 0,
                "Removing the last account retained a credential file.");

            File.WriteAllText(path, "invalid account JSON");
            File.WriteAllText(vault, "obsolete encrypted credential fixture");
            var rejected = false;
            try { AccountSecretStore.Load(path, options); }
            catch (JsonException) { rejected = true; }
            Require(rejected && !File.Exists(vault), "Malformed account data prevented removal of the obsolete vault.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("Account persistence: runtime sessions retained; disk profiles and legacy migration exclude tokens.");
    }

    private static void CheckNoSecrets(string json)
    {
        foreach (var value in new[] { "MsRefreshToken", "MinecraftAccessToken", "AuthServerClientToken", "refresh-secret-fixture", "access-secret-fixture", "client-secret-fixture" })
            Require(!json.Contains(value, StringComparison.Ordinal), "An account serialization retained a credential.");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
