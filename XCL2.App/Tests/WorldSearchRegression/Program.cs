using XCL2.App.Services.Worlds;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}
var survival = new WorldArchive(@"C:\Games\profiles\Fabric-1.21\saves\spring-home",
    new WorldSettings("春日家园", 0, 2, false, false, -123456789, "1.21.4", 4189), DateTime.UnixEpoch);
var creative = new WorldArchive(@"D:\Minecraft\saves\castle",
    new WorldSettings("Creative Castle", 1, 0, true, false, 42, "1.20.1", 3465), DateTime.UnixEpoch);
var hardcore = new WorldArchive(@"C:\Games\saves\extreme",
    new WorldSettings("极限挑战", 0, 3, false, true, null, "1.21.4", 4189), DateTime.UnixEpoch);

Check(WorldSearch.Matches(survival, null), "blank search includes every world");
Check(WorldSearch.Matches(survival, "家园"), "Chinese display name supports substring search");
Check(WorldSearch.Matches(survival, "fabric"), "instance directory is searchable without case sensitivity");
Check(WorldSearch.Matches(survival, "春日 1.21.4"), "tokens can match separate world fields");
Check(WorldSearch.Matches(survival, "-123456789"), "negative seed is searchable without locale formatting");
Check(WorldSearch.Matches(creative, "creative CASTLE"), "multiple English terms ignore case");
Check(!WorldSearch.Matches(survival, "家园 城堡"), "all query terms must match");
Check(WorldSearch.Matches(survival, "\t春日\u3000Fabric  \n"), "Unicode and repeated whitespace separate query terms");
Check(WorldSearch.Matches(survival, "", 1), "survival mode filter includes survival");
Check(!WorldSearch.Matches(creative, "", 1), "survival mode filter excludes creative");
Check(WorldSearch.Matches(creative, "", 2), "creative filter maps to actual game-mode index");
Check(!WorldSearch.Matches(survival, "", 5), "hardcore filter does not include normal survival");
Check(WorldSearch.Matches(hardcore, "hardcore", 5), "hardcore is a flag filter and searchable alias");
Check(WorldSearch.Matches(survival, "", 0, "1.21.4"), "selected version includes matching world");
Check(!WorldSearch.Matches(survival, "", 0, "1.21"), "version filter is exact rather than ambiguous prefix");
Check(!WorldSearch.Matches(hardcore, "家园", 5, "1.21.4"), "search, mode and version are combined");
Check(WorldSearch.Matches(hardcore, "极限", 1, "1.21.4"), "hardcore can also be selected as its underlying survival mode");
Check(WorldSearch.MatchesText("种子", "世界种子", "seed", "只影响以后生成的区块"), "setting label search");
Check(WorldSearch.MatchesText("PLAYER.health", "玩家生命", "player.health"), "setting field search ignores case");
Check(WorldSearch.MatchesText("以后 区块", "世界种子 seed 只影响以后生成的区块"), "setting notes participate in matching");
Check(WorldSearch.MatchesText("keepinventory", "游戏规则", "keepInventory=true\ndoDaylightCycle=false"), "actual game rule names are searchable");
Check(!WorldSearch.MatchesText("不存在", "世界种子 seed"), "unknown setting query yields no result");

Console.WriteLine($"WorldSearchRegression: {checks} checks passed.");
