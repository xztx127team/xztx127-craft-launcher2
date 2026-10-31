using System.IO.Compression;
using System.Text;
using System.Text.Json;
using XCL2.App.Services;

int assertions = 0;
void Check(bool value, string label) { assertions++; if (!value) throw new Exception("FAIL: " + label); }
string Run(string id, params string[] values) => UtilityToolCatalog.Run(id, values);
void Reject(string id, string[] values, int? field = null)
{
    try { Run(id, values); throw new Exception("Expected rejection: " + id); }
    catch (UtilityToolInputException ex) { Check(field == null || ex.FieldIndex == field, id + " error identifies field"); }
}
Check(UtilityToolCatalog.All.Count == 64, "64 tools");
Check(UtilityToolCatalog.All.Select(t => t.Id).Distinct().Count() == 64, "unique stable ids");
foreach (var tool in UtilityToolCatalog.All)
    Check(Run(tool.Id, tool.Fields.Select(f => f.Example).ToArray()).Length > 0, tool.Id + " example runs");

Check(Run("chunk-coord", "-1", "-17").Contains("区块：-1, -2"), "negative chunk floor");
Check(Run("chunk-coord", "-1", "-17").Contains("区块内：15, 15"), "negative local coordinate");
Check(Run("region-coord", "-1", "-1").Contains("r.-1.-1.mca"), "negative region floor");
Check(Run("region-coord", "-1", "-1").Contains("位置表索引：1023"), "region local index");
Check(Run("region-file", "r.-1.0.mca").Contains("方块 X：-512 … -1"), "region boundaries");
Check(Run("fill-volume", "0 0 0", "2 2 2").Contains("外壳方块：26"), "3 cube shell");
Check(Run("fill-volume", "0 0 0", "0 0 0").Contains("内部方块：0"), "one block interior");
Check(Run("sphere-plan", "1").Contains("实心球总用量：7"), "unit sphere exact lattice");
Check(Run("circle-plan", "1").Contains("底面方块：5"), "unit circle exact lattice");
Check(Run("cylinder-plan", "1", "3").Contains("总用量：15"), "cylinder exact lattice");
Check(Run("line-plan", "0 0", "0 0") == "0 0", "degenerate pixel line");
Check(Run("line-plan", "2 2", "0 0") == "2 2\n1 1\n0 0", "reversed pixel line");
Check(Run("craft-batches", "0", "4").Contains("批次：0"), "zero craft demand");
Check(Run("craft-batches", "9", "4").Contains("多余：3"), "craft rounding");
Check(Run("recipe-bill", "{\"wood\":3}", "2", "5").Contains("wood: 9"), "recipe rounding");
Reject("recipe-bill", new[] { "{\"wood\":\"wrong\"}", "1", "1" }, 0);
Reject("fuel", new[] { "1", "0", "10" }, 1);
Reject("sphere-plan", new[] { "65" }, 0);
Reject("line-plan", new[] { "0 0", "20000 0" }, 1);
Reject("stats", new[] { "1 NaN 2" }, 0);
Reject("duration", new[] { "Infinity" }, 0);
Reject("percent", new[] { "0", "10" }, 0);
Reject("base-convert", new[] { "102", "2" }, 0);
Reject("base-convert", new[] { "--FF", "16" }, 0);
Reject("percent", new[] { "1e-300", "1e15" }, 0);
Check(Run("sort-lines", "10\n2\n", "numeric") == "2\n10", "numeric sorting ignores trailing blank line");
Check(Run("query-build", "#key=path\\file").Contains("%23key=path%5Cfile"), "query keys and backslashes are literal");
Check(Run("base-convert", "-FF", "16").Contains("10 进制：-255"), "signed radix");
Check(Run("base-convert", "0", "10").Contains("2 进制：0"), "zero radix");
Check(Run("subnet", "192.168.1.42/24").Contains("网络：192.168.1.0/24"), "subnet /24");
Check(Run("subnet", "1.2.3.4/0").Contains("地址总数：4294967296"), "subnet /0 overflow");
Check(Run("subnet", "192.0.2.0/31").Contains("可用主机数：2"), "subnet /31");
Check(Run("subnet", "192.0.2.1/32").Contains("可用范围：192.0.2.1 – 192.0.2.1"), "subnet /32");
Reject("subnet", new[] { "::1/128" }, 0);
Check(Run("version-compare", "1.0.0-rc.2", "1.0.0-rc.10").Contains(" < "), "numeric prerelease");
Check(Run("version-compare", "1.0.0+abc", "1.0.0+xyz").Contains(" = "), "build metadata ignored");
Check(Run("version-compare", "1.0.0-alpha", "1.0.0").Contains(" < "), "release precedence");
Reject("version-compare", new[] { "1.0.0-01", "1.0.0" }, 0);
Check(Run("hash-text", "hello").Contains("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"), "SHA-256 known vector");
Check(Run("hex-text", "decode", Run("hex-text", "encode", "你好\n👋")) == "你好\n👋", "UTF-8 byte roundtrip");
Reject("hex-text", new[] { "decode", "FF" });
string payload = "你好\r\nsecond line\n👋";
Check(Run("gzip-text", "decompress", Run("gzip-text", "compress", payload)) == payload, "gzip exact newline roundtrip");
using (var bomb = new MemoryStream())
{
    using (var gz = new GZipStream(bomb, CompressionLevel.Optimal, true)) gz.Write(new byte[1048577]);
    Reject("gzip-text", new[] { "decompress", Convert.ToBase64String(bomb.ToArray()) }, 1);
}
Check(Run("wrap-text", "A👋B", "2") == "A👋\nB", "wrap doesn't split surrogate pairs");
Check(Run("text-stats", "A👋").Contains("Unicode 码点：2"), "unicode rune count");
Check(Run("slug", "CON.txt") == "_CON.txt", "Windows reserved filename");
Check(Run("replace-text", "a.b a.b", "a.b", "x") == "x x", "literal replacement");
Check(Run("replace-text", "a b", " ", "") == "ab", "replace whitespace with empty string");
Reject("replace-text", new[] { new string('a', 262144), "a", new string('b', 262144) }, 2);
Reject("regex", new[] { "(?=(a+))", new string('a', 200000) });
Check(Run("version-compare", "1.0.0--1", "1.0.0-1").Contains(" > "), "hyphenated prerelease is nonnumeric");
Check(Run("mcfunction-clean", "# c\nsay #keep\n\n say hi") == "say #keep\n say hi", "preserve inline comments and leading command whitespace");
Check(Run("properties-lint", "pvp=maybe\nserver-port=99999\npvp=true").Contains("重复键"), "properties duplicates");
Reject("properties-json", new[] { "a=1\na=2" }, 0);
Reject("properties-json", new[] { "a=\\u0041" }, 0);
Reject("json-properties", new[] { "{\"key\":\" leading\"}" }, 0);
Reject("resource-id", new[] { "Minecraft:Stone" }, 0);
Check(Run("resource-id", "stone").Contains("minecraft:stone"), "default namespace");
Check(Run("contrast", "#000000", "#ffffff").Contains("21:1"), "contrast black white");
Check(Run("date-diff", "2026-01-01T00:00:00Z", "2026-01-01T08:00:00+08:00").Contains("总秒数：0"), "timezone equivalence");
Reject("date-add", new[] { "12:00", "1" }, 0);
Reject("date-add", new[] { "9999-12-31T23:59:59Z", "86400" }, 1);
Check(Run("properties-lint", "! comment\npvp=true").StartsWith("未发现"), "Java properties exclamation comments");
Check(Run("tellraw", "quote \"\nline", "#ffffff").Contains("\\n"), "tellraw newline JSON escaped");
using (var gradient = JsonDocument.Parse(Run("gradient", "A👋", "#000000", "#ffffff")[12..]))
{
    Check(gradient.RootElement.GetArrayLength() == 2, "gradient runes");
    Check(gradient.RootElement[1].GetProperty("color").GetString() == "#ffffff", "gradient endpoint");
}
string csv = "name,note\r\nSteve,\"hello, \"\"world\"\"\r\nnext\"";
using (var data = JsonDocument.Parse(Run("csv-json", csv)))
    Check(data.RootElement[0].GetProperty("note").GetString() == "hello, \"world\"\r\nnext", "CSV escaped multiline cell");
Reject("csv-json", new[] { "a,a\n1,2" }, 0);
Reject("csv-json", new[] { "a,b\n1" }, 0);
Reject("csv-json", new[] { "a\n\"unterminated" }, 0);
Reject("regex", new[] { "[", "text" }, 0);
Check(Run("filter-lines", "INFO\nERROR", "ERROR") == "ERROR", "line filtering");
Check(Run("password", "64", "ab").All(c => c is 'a' or 'b'), "password alphabet");
Check(Run("uuid-generate", "2").Split('\n').All(x => Guid.TryParse(x, out _) && x[14] == '4'), "UUID v4");
Reject("loot-weight", new[] { "a=0\nb=0" }, 0);
Reject("whitelist", new[] { "Steve 123e4567-e89b-42d3-a456-426614174000\nAlex 123e4567-e89b-42d3-a456-426614174000" }, 0);

// The optional repo argument checks persisted indices shared by navigation, favorites and global search.
if (args.Length > 0)
{
    string app = Path.GetFullPath(args[0]);
    using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "Resources/Data/FeatureSearchCatalog.json")));
    var tabs = catalog.RootElement.EnumerateArray().Where(x => x.GetProperty("Id").GetString()!.StartsWith("toolbox.tab.")).ToArray();
    Check(tabs.Select(x => x.GetProperty("TabIndex").GetInt32()).Distinct().Count() == tabs.Length, "unique tab indices");
    for (int i = 0; i < UtilityToolCatalog.All.Count; i++)
        Check(tabs.Single(x => x.GetProperty("TabIndex").GetInt32() == UtilityToolCatalog.FirstTabIndex + i).GetProperty("Title").GetString() == UtilityToolCatalog.All[i].Title, "search title maps to tool " + i);
    string[] assets = { "游戏图标与原版物品下载", "动画、粒子与绿幕视频素材", "MC 主题 3D 文字下载" };
    for (int i = 0; i < assets.Length; i++)
        Check(tabs.Single(x => x.GetProperty("TabIndex").GetInt32() == 101 + i).GetProperty("Title").GetString() == assets[i], "asset search mapping " + i);
}
Console.WriteLine($"PASS: {assertions} assertions across 64 tools.");
