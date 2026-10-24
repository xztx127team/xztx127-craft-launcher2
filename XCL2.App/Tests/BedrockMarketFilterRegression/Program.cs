using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    checks++;
}

var old = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
var recent = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
var files = new[]
{
    new BedrockMarketFile { Name = "Zulu.MCPACK", Source = "GitHub", Size = 300, PublishedAt = recent },
    new BedrockMarketFile { Name = "Alpha.mcaddon", Source = "CurseForge", Size = 100, PublishedAt = old, GameVersions = "1.21.50, 1.20.80" },
    new BedrockMarketFile { Name = "Beta.zip", Source = "CurseForge", Size = 200, GameVersions = "1.2; 1.21-preview" },
    new BedrockMarketFile { Name = "empty.zip", Source = "GitHub", Size = 0 }
};

Check(BedrockMarketFileFilter.Apply(files).Count == 4, "all files remain with no filter");
Check(BedrockMarketFileFilter.Apply(files, source: "GitHub").Count == 2, "exact GitHub source");
Check(BedrockMarketFileFilter.Apply(files, source: "curseforge").Count == 2, "source case insensitive");
Check(BedrockMarketFileFilter.Apply(files, fileType: "mcpack").Single() == files[0], "uppercase extension");
Check(BedrockMarketFileFilter.Apply(files, fileType: "mcaddon").Single() == files[1], "addon extension");
Check(BedrockMarketFileFilter.Apply(files, fileType: "zip").Count == 2, "zip extension");
Check(BedrockMarketFileFilter.Apply(files, gameVersion: "1.21").Count == 2, "version line and prerelease token");
Check(BedrockMarketFileFilter.Apply(files, gameVersion: "1.2").Single() == files[2], "version boundary avoids 1.20");
Check(BedrockMarketFileFilter.Apply(files, gameVersion: "1.21.50").Single() == files[1], "exact declared version");
Check(BedrockMarketFileFilter.Apply(files, gameVersion: " 1.21.50 ").Single() == files[1], "trimmed query");
Check(BedrockMarketFileFilter.Apply(files, gameVersion: "9.99").Count == 0, "unknown game versions excluded");
Check(BedrockMarketFileFilter.Apply(files, source: "GitHub", gameVersion: "1.21").Count == 0, "unmarked files never treated as compatible");
Check(BedrockMarketFileFilter.Apply(files, source: "CurseForge", fileType: "zip", gameVersion: "1.21").Single() == files[2], "combined filters");
Check(BedrockMarketFileFilter.Apply(files).First() == files[0], "newest first");
Check(BedrockMarketFileFilter.Apply(files).Last() == files[3], "unknown date deterministic tie by name");
Check(BedrockMarketFileFilter.Apply(files, sort: "name").First() == files[1], "name ordering");
Check(BedrockMarketFileFilter.Apply(files, sort: "size").Select(file => file.Size).SequenceEqual(new long[] { 300, 200, 100, 0 }), "descending size");
Check(BedrockMarketFileFilter.Apply(files, source: "missing").Count == 0, "empty subset");
Check(files[0].Name == "Zulu.MCPACK" && files[0].GameVersions == "", "filter leaves upstream records unchanged");

Console.WriteLine($"Bedrock market file filters: {checks} checks passed.");
