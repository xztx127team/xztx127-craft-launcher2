using System.Text;
using System.Text.Json;
using XCL2.App.Services;

internal static class ProviderParameterMemoryCases
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Provider parameter memory: " + message);
        }

        var directory = Path.Combine(Path.GetTempPath(), "xcl-provider-parameters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ai", AiProviderParameterMemory.FileName);
        try
        {
            const string provider = "named-provider-SECRET-PROVIDER";
            const string url = "https://secret-tenant.example/v1/chat/completions?token=SECRET-QUERY&tenant=alice#local";
            var memory = new AiProviderParameterMemory(directory);
            Check(memory.GetOmittedParameters(provider, url).Count == 0 && memory.PersistenceWarning == null,
                "a missing cache starts empty without creating a warning");
            Check(memory.Remember(provider, url, "temperature") && !memory.Remember(provider, url, "temperature"),
                "first rejection is learned once");
            Check(memory.Remember(provider, url, "reasoning_effort"), "additional optional parameters are merged");
            var reloaded = new AiProviderParameterMemory(directory);
            Check(reloaded.GetOmittedParameters(provider, url).OrderBy(value => value)
                .SequenceEqual(new[] { "reasoning_effort", "temperature" }), "rejections survive a restart");
            Check(reloaded.GetOmittedParameters(provider.ToUpperInvariant(),
                "https://secret-tenant.example/v1/models?token=SECRET-QUERY&tenant=alice").Count == 2,
                "provider ID case, known endpoint suffixes and fragments normalize to one scope");
            Check(reloaded.GetOmittedParameters(provider, "https://secret-tenant.example/v1?token=SECRET-QUERY&tenant=bob").Count == 0,
                "query tenant changes never share a rejection cache");
            Check(reloaded.GetOmittedParameters("different-provider", url).Count == 0
                && reloaded.GetOmittedParameters(provider, "https://another.example/v1").Count == 0
                && reloaded.GetOmittedParameters(provider, "https://secret-tenant.example/v2?token=SECRET-QUERY&tenant=alice").Count == 0,
                "provider IDs, hosts and API prefixes stay isolated");
            Check(!reloaded.Remember(provider, url, "messages") && !reloaded.Remember(provider, url, "Authorization")
                && !reloaded.Remember(provider, url, "api_key") && !reloaded.Remember("", url, "temperature")
                && !reloaded.Remember(provider, "not an absolute URL", "temperature"),
                "unknown or essential fields and invalid identities are never remembered");
            var snapshot = reloaded.GetOmittedParameters(provider, url);
            if (snapshot is string[] values) values[0] = "messages";
            Check(!reloaded.GetOmittedParameters(provider, url).Contains("messages"),
                "callers cannot mutate the stored omission set through a returned snapshot");
            var disk = File.ReadAllText(path);
            Check(!disk.Contains("SECRET", StringComparison.Ordinal) && !disk.Contains("https://", StringComparison.Ordinal)
                && !disk.Contains("alice", StringComparison.Ordinal) && !disk.Contains("secret-tenant", StringComparison.Ordinal),
                "persistence contains no raw provider identities, URLs, query credentials or tenant identifiers");
            using (var json = JsonDocument.Parse(disk))
                Check(json.RootElement.GetProperty("version").GetInt32() == 1
                    && json.RootElement.GetProperty("entries")[0].GetProperty("scope").GetString()!.Length == 64,
                    "cache uses the versioned JSON format with a SHA-256 scope digest");

            var fields = new[] { "temperature", "reasoning_effort", "stream_options", "stream", "tools", "max_tokens", "max_completion_tokens" };
            Parallel.For(0, 70, index => memory.Remember("concurrent", "https://concurrent.example/v1", fields[index % fields.Length]));
            Check(memory.GetOmittedParameters("concurrent", "https://concurrent.example/v1").Count == fields.Length
                && new AiProviderParameterMemory(directory).GetOmittedParameters("concurrent", "https://concurrent.example/v1").Count == fields.Length,
                "concurrent discoveries on one instance persist every newly rejected field");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(),
                "atomic saves leave no temporary files behind");

            // A model-test service and an already opened chat service use the
            // same data directory but hold independent memory objects.
            var firstService = new AiProviderParameterMemory(directory);
            var secondService = new AiProviderParameterMemory(directory);
            Check(firstService.Remember("shared-service", "https://shared.example/v1", "temperature")
                && secondService.GetOmittedParameters("shared-service", "https://shared.example/v1").Contains("temperature"),
                "an existing service observes a sibling service's newly learned fields");
            Check(secondService.Remember("shared-service", "https://shared.example/v1", "tools")
                && firstService.GetOmittedParameters("shared-service", "https://shared.example/v1").Contains("tools")
                && new AiProviderParameterMemory(directory).GetOmittedParameters("shared-service", "https://shared.example/v1").Count == 2,
                "sibling writes merge prior observations instead of overwriting a stale snapshot");
            Parallel.For(0, 70, index => (index % 2 == 0 ? firstService : secondService)
                .Remember("parallel-services", "https://shared.example/v1", fields[index % fields.Length]));
            Check(new AiProviderParameterMemory(directory).GetOmittedParameters("parallel-services", "https://shared.example/v1").Count == fields.Length,
                "concurrent services serialize refresh/save and preserve every rejected field");

            foreach (var damaged in new[]
            {
                "{broken", "null", "{}", "{\"version\":2,\"entries\":[]}", "{\"version\":\"bad\",\"entries\":[]}",
                "{\"version\":1,\"entries\":{}}", new string('x', 1024 * 1024 + 1),
                JsonSerializer.Serialize(new { version = 1, entries = Enumerable.Range(0, 1025)
                    .Select(_ => new { scope = new string('A', 64), omittedParameters = new[] { "temperature" } }).ToArray() })
            })
            {
                File.WriteAllText(path, damaged, Encoding.UTF8);
                var damagedMemory = new AiProviderParameterMemory(directory);
                Check(damagedMemory.GetOmittedParameters(provider, url).Count == 0 && damagedMemory.PersistenceWarning != null,
                    "malformed, unsupported or oversized caches are ignored without breaking requests");
                Check(damagedMemory.Remember(provider, url, "temperature") && damagedMemory.PersistenceWarning == null
                    && new AiProviderParameterMemory(directory).GetOmittedParameters(provider, url).Contains("temperature"),
                    "a new valid observation replaces an unreadable cache with a valid atomic snapshot");
            }
            File.WriteAllText(path, "{\"version\":1,\"entries\":[{\"scope\":\"raw-secret-url\",\"omittedParameters\":[\"temperature\"]},"
                + "{\"scope\":\"" + new string('A', 64) + "\",\"omittedParameters\":[\"messages\",\"Authorization\"]}]}");
            var untrusted = new AiProviderParameterMemory(directory);
            Check(untrusted.GetOmittedParameters(provider, url).Count == 0
                && untrusted.Remember(provider, url, "stream_options"),
                "invalid scope digests and non-allow-listed imported fields cannot poison requests");

            var blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "a file blocks directory creation");
            var unavailable = new AiProviderParameterMemory(blocked);
            Check(unavailable.Remember(provider, url, "temperature") && unavailable.PersistenceWarning != null
                && unavailable.GetOmittedParameters(provider, url).Contains("temperature"),
                "an unwritable cache location keeps the learned field effective in memory and exposes a safe warning");
            var warning = unavailable.PersistenceWarning ?? "";
            Check(!warning.Contains(blocked, StringComparison.Ordinal)
                && !warning.Contains("SECRET", StringComparison.Ordinal), "persistence warnings disclose no path or endpoint secrets");
            File.Delete(blocked);
            Check(!unavailable.Remember(provider, url, "temperature") && unavailable.PersistenceWarning == null
                && new AiProviderParameterMemory(blocked).GetOmittedParameters(provider, url).Contains("temperature"),
                "a later save retries a previous persistence failure even for a previously learned field");
            var noDirectory = new AiProviderParameterMemory(null!);
            Check(noDirectory.PersistenceWarning != null && noDirectory.Remember(provider, url, "temperature")
                && noDirectory.GetOmittedParameters(provider, url).Contains("temperature"),
                "a missing data directory cannot stop in-memory compatibility learning");
        }
        finally { Directory.Delete(directory, recursive: true); }
        return checks;
    }
}
