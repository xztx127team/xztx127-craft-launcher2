using System.Globalization;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; }
var uuid = "e0189674-e1af-47aa-88b7-309686b635e2";
var metadata = """
{"version":{"versions":{"gdk":{"release":["1.26.9.1",null,7,"1.26.52.3","1.26.52.3"],"preview":["1.26.70.20"]},
"uwp":{"release":["1.21.31.4"],"preview":["1.21.120.20"]}}}}
""";
var parsed = BedrockVersionCatalog.ParseMetadata(metadata);
Check(parsed.Count == 5, "bad individual metadata items are skipped and duplicates are removed");
Check(parsed.Count(value => value.Preview) == 2, "preview channel is not inferred incorrectly from numeric version names");
Check(parsed.First(value => !value.Preview).Name == "1.26.52.3", "versions sort numerically rather than lexically");
Check(parsed.First(value => value.Name == "1.26.52.3").DirectUrlAvailable, "GDK entries with no Store UUID remain valid");
Check(!parsed.First(value => value.Name == "1.21.31.4").DirectUrlAvailable, "UWP entries are not mislabeled as GDK");
var legacy = BedrockVersionCatalog.ParseLegacy($"[[\"1.21.31.4\",\"{uuid}\",0],[\"1.21.70.20\",\"{uuid}\",2],[\"bad\",\"{uuid}\",0],[\"1.21.0\",\"not-a-uuid\",0],[\"1.20.0\",\"{uuid}\",\"bad\"]]");
Check(legacy.Count == 2 && legacy.Count(value => value.Preview) == 1, "legacy numeric beta flag and real IDs are preserved");
var merged = BedrockVersionCatalog.MergeMetadataAndLegacy(parsed, legacy);
Check(merged.Any(value => value.Preview && value.Name == "1.21.70.20" && value.Uuid == uuid),
    "production merge preserves preview-only legacy entries");
var matchedPreview = BedrockVersionCatalog.MergeMetadataAndLegacy(parsed,
    new[] { new BedrockCatalogEntry(uuid, "1.21.120.20", "1.21.120.20", null, true) });
Check(matchedPreview.Count == parsed.Count && matchedPreview.Single(value => value.Name == "1.21.120.20" && value.Preview).Uuid == uuid,
    "production merge supplies the real Store UUID to a matching metadata preview");
var cache = BedrockVersionCatalog.SerializeCache(merged);
var restored = BedrockVersionCatalog.ParseCache(cache);
Check(restored.Count == 6, "new cache envelope survives round trip without losing empty-UUID GDK versions");
Check(restored.Count(value => value.Preview) == 3, "cache round trip preserves preview classification");
Check(restored.First(value => value.Name == "1.21.31.4").Uuid == uuid, "duplicate metadata gets its real Store ID");
var oldCache = BedrockVersionCatalog.ParseCache($"[{{\"Uuid\":\"{uuid}\",\"Name\":\"1.21.31.4\",\"BaseVersion\":\"1.21.31.4\",\"Channel\":0}},{{\"Uuid\":\"\",\"Name\":\"1.26.70.20\",\"Channel\":1,\"DirectUrlAvailable\":true}}]");
Check(oldCache.Count == 2 && oldCache.Any(value => value.Preview && value.DirectUrlAvailable), "old PascalCase cache remains readable");
Check(BedrockVersionCatalog.ParseCache($"[{{\"uuid\":\"{uuid}\",\"name\":\"1.21.31.4\",\"base\":\"1.21.31.4\"}}]").Count == 1,
    "historical lowercase cache remains readable");
Check(BedrockVersionCatalog.ParseMetadata("<html>mirror error</html>").Count == 0 &&
    BedrockVersionCatalog.ParseCache("broken").Count == 0, "invalid response bodies cannot count as successful catalogs");
var fallback = BedrockVersionCatalog.ParseCache(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fallback.json")));
Check(fallback.Count >= 100 && fallback.Any(value => value.Preview) && fallback.Any(value => !value.Preview), "embedded fallback has both real channels");
Check(fallback.All(value => value.Uuid.Length == 0 || Guid.TryParse(value.Uuid, out _)), "embedded fallback IDs are valid GUIDs");
Check(fallback.First(value => value.Name == "1.21.31.4" && !value.Preview).Uuid == uuid,
    "embedded fallback uses the upstream Store ID instead of an invented identifier");

var culture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    Check(DownloadDisplayFormatting.FormatCapacity(1024, DownloadDisplayUnit.B) == "1024\u00A0B", "explicit bytes");
    Check(DownloadDisplayFormatting.FormatCapacity(1048576, DownloadDisplayUnit.KB) == "1024\u00A0KB", "explicit kilobytes");
    Check(DownloadDisplayFormatting.FormatCapacity(1048576, DownloadDisplayUnit.MB) == "1\u00A0MB", "explicit megabytes");
    Check(DownloadDisplayFormatting.FormatCapacity(536870912, DownloadDisplayUnit.GB) == "0.5\u00A0GB", "explicit gigabytes preserve fractions");
    Check(DownloadDisplayFormatting.FormatCapacity(1, DownloadDisplayUnit.GB).StartsWith("< 0.01"), "tiny values are not rounded to zero");
    Check(DownloadDisplayFormatting.FormatCapacity(-1, DownloadDisplayUnit.B) == "0\u00A0B", "negative values are clamped");
    Check(DownloadDisplayFormatting.FormatCapacity(1024, DownloadDisplayUnit.Automatic, _ => "existing preference") == "existing preference", "automatic keeps existing launcher preference");
    Check(DownloadDisplayFormatting.FormatCapacity(1024, (DownloadDisplayUnit)999, _ => "fallback") == "fallback", "corrupt enum values use automatic");
    Check(DownloadDisplayFormatting.FormatCapacity(1024, DownloadDisplayUnit.KB, uniformSpacing: false) == "1 KB", "existing spacing preference is honored");
    Check(DownloadDisplayFormatting.KiBToBytes(1.5) == 1536 && DownloadDisplayFormatting.KiBToBytes(double.NaN) == 0 &&
        DownloadDisplayFormatting.KiBToBytes(double.MaxValue) == long.MaxValue, "speed conversion is finite and overflow safe");
}
finally { CultureInfo.CurrentCulture = culture; }
Console.WriteLine($"PASS: {checks} catalog, cache, fallback and display-unit checks");
