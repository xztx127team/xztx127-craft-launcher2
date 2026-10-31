using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using XCL2.App.Services;

if (args is ["--child", var childPath, var field])
{
    var baseline = "{\"a\":0,\"b\":0}";
    var local = JsonNode.Parse(baseline)!; local[field] = 1;
    ConfigMergeStore.Commit(childPath, local.ToJsonString(), baseline, baseline,
        publishMirror: merged => ConfigMergeStore.WriteAtomically(childPath + ".mirror", merged));
    return;
}
var checks = 0;
void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; }
void Reject(Action action, string name) { try { action(); } catch (IOException) { checks++; return; } throw new InvalidOperationException(name); }
var original = "{\"theme\":\"light\",\"download\":{\"speed\":0,\"slots\":2}}";
var first = "{\"theme\":\"dark\",\"download\":{\"speed\":0,\"slots\":2}}";
var second = "{\"theme\":\"light\",\"download\":{\"speed\":10,\"slots\":2}}";
var merged = ConfigMergeStore.Merge(original, original, second, first);
Check(JsonNode.Parse(merged)!["theme"]!.GetValue<string>() == "dark", "Unchanged stale local theme must not overwrite external edit");
Check(JsonNode.Parse(merged)!["download"]!["speed"]!.GetValue<int>() == 10, "Independent edits are merged");
var laterLocal = "{\"theme\":\"light\",\"download\":{\"speed\":20,\"slots\":2}}";
var later = ConfigMergeStore.Merge(second, merged, laterLocal, merged);
Check(JsonNode.Parse(later)!["theme"]!.GetValue<string>() == "dark", "Repeated saves with stale in-memory fields preserve external values");
Reject(() => ConfigMergeStore.Merge(original, original, first, original.Replace("light", "blue")), "Overlapping edits must report conflict");
Check(JsonNode.DeepEquals(JsonNode.Parse(ConfigMergeStore.Merge(original, original, first, first)), JsonNode.Parse(first)), "Same edit by both processes is not a conflict");
// Missing JSON fields, default-filled local objects and unknown extension fields retain their distinct identities.
var absentDisk = "{\"theme\":\"light\",\"unknown\":{\"x\":1}}";
var defaultsLocal = "{\"theme\":\"light\",\"newDefault\":1}";
var changedLocal = "{\"theme\":\"light\",\"newDefault\":2}";
var changedDisk = "{\"theme\":\"dark\",\"unknown\":{\"x\":2}}";
var unchangedUnknown = JsonNode.Parse(ConfigMergeStore.Merge(defaultsLocal, absentDisk, changedLocal, absentDisk))!;
Check(unchangedUnknown["unknown"]!["x"]!.GetValue<int>() == 1 && unchangedUnknown["newDefault"]!.GetValue<int>() == 2,
    "An ordinary save preserves unknown disk fields absent from both local serializer snapshots");
var sparse = JsonNode.Parse(ConfigMergeStore.Merge(defaultsLocal, absentDisk, changedLocal, changedDisk))!;
Check(sparse["theme"]!.GetValue<string>() == "dark" && sparse["newDefault"]!.GetValue<int>() == 2
    && sparse["unknown"]!["x"]!.GetValue<int>() == 2, "Default-filled and unknown fields merge without overwriting external edits");
var deletedNull = JsonNode.Parse(ConfigMergeStore.Merge("{\"nullable\":null,\"other\":0}", "{\"nullable\":null,\"other\":0}",
    "{\"nullable\":null,\"other\":1}", "{\"other\":0}"))!.AsObject();
Check(!deletedNull.ContainsKey("nullable") && deletedNull["other"]!.GetValue<int>() == 1,
    "An externally removed explicit-null field is not resurrected by an unrelated save");
var directory = Path.Combine(Path.GetTempPath(), "xcl-config-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "config.json"); File.WriteAllText(path, original);
    ConfigMergeStore.Commit(path, first, original, original);
    Reject(() => ConfigMergeStore.Commit(path, original.Replace("light", "blue"), original, original), "Conflict must reject commit");
    Check(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse(first)), "Conflict leaves original file intact");
    File.WriteAllText(path, "broken-json");
    ConfigMergeStore.Commit(path, first, original, original, recoverInvalid: true);
    Check(Directory.GetFiles(directory, "*.corrupt-*").Length == 1, "Recovery preserves damaged original before replacing");
    Check(!Directory.GetFiles(directory, "*.tmp").Any(), "Successful commits leave no temporary file");

    const string goodTyped = "{\"count\":1}";
    const string badTyped = "{\"count\":\"invalid\"}";
    File.WriteAllText(path, badTyped);
    void ValidateTyped(string json) => JsonSerializer.Deserialize<Dictionary<string, int>>(json);
    bool badTypeRejected = false;
    try { ConfigMergeStore.Commit(path, goodTyped, goodTyped, goodTyped, validate: ValidateTyped); }
    catch (JsonException) { badTypeRejected = true; }
    Check(badTypeRejected && File.ReadAllText(path) == badTyped,
        "A normal save preserves an externally corrupted known field instead of silently resetting it");
    ConfigMergeStore.Commit(path, goodTyped, goodTyped, goodTyped, recoverInvalid: true, validate: ValidateTyped);
    Check(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse(goodTyped))
        && Directory.GetFiles(directory, "*.corrupt-*").Any(file => File.ReadAllText(file) == badTyped),
        "Explicit load recovery also backs up valid JSON with invalid known field types");

    File.WriteAllText(path, original);
    Exception? mirrorError = null;
    var published = ConfigMergeStore.Commit(path, first, original, original,
        publishMirror: _ => throw new IOException("mirror is read-only"), onMirrorError: ex => mirrorError = ex);
    Check(mirrorError is IOException && JsonNode.DeepEquals(JsonNode.Parse(published), JsonNode.Parse(first))
        && JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse(first)),
        "Mirror failure reports diagnostics while the successful primary commit remains successful");
    var afterMirrorFailure = ConfigMergeStore.Commit(path, first, first, published);
    Check(JsonNode.DeepEquals(JsonNode.Parse(afterMirrorFailure), JsonNode.Parse(first)),
        "Caller snapshots can advance after mirror failure without duplicate or stale changes");
    File.WriteAllText(path, original);
    var validationRejected = false;
    try { ConfigMergeStore.Commit(path, first, original, original, validate: _ => throw new InvalidDataException("invalid known field type")); }
    catch (InvalidDataException) { validationRejected = true; }
    Check(validationRejected && File.ReadAllText(path) == original, "Schema failure leaves the primary untouched before a durable commit");

    File.WriteAllText(path, "{\"a\":0,\"b\":0}");
    Process Start(string key)
    {
        var executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--child"); info.ArgumentList.Add(path); info.ArgumentList.Add(key);
        return Process.Start(info)!;
    }
    using var a = Start("a"); using var b = Start("b");
    foreach (var process in new[] { a, b })
    {
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new TimeoutException("Config writer did not finish"); }
        Check(process.ExitCode == 0, "Cross-process commit succeeded");
    }
    var final = JsonNode.Parse(File.ReadAllText(path))!;
    Check(final["a"]!.GetValue<int>() == 1 && final["b"]!.GetValue<int>() == 1, "Both cross-process edits survive");
    Check(JsonNode.DeepEquals(final, JsonNode.Parse(File.ReadAllText(path + ".mirror"))),
        "The last mirror is published inside the same cross-process critical section as its primary commit");
}
finally { Directory.Delete(directory, true); }
Console.WriteLine($"PASS: {checks} config merge checks");
