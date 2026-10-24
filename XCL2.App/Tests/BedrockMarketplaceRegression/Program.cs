using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    checks++;
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(name);
}
void RejectArgument(Action action, string name)
{
    try { action(); }
    catch (ArgumentException) { checks++; return; }
    throw new InvalidOperationException(name);
}

var list = """
<input name="platform" type="hidden" value="2" />
<span>当前 2 / 7 页，共计 206 条。</span>
<a href="/class/999.html">Navigation must not become a result</a>
<div class="modlist-block"><div class="intro"><a href="/class/10.html" class="intro-content"><span>Behavior &amp; resources</span></a></div>
<div class="cover"><img src="//i.mcmod.cn/class/cover/test.png" /></div><div class="title"><p class="name"><a href="/class/10.html">测试附加包</a></p><p class="ename"><a href="/class/10.html">Test Addon</a></p></div></div>
<div class="modlist-block"><div class="title"><p class="name"><a href="/class/11.html">中文名字 &amp; Other</a></p><p class="ename"><a href="/class/11.html">English Name</a></p></div></div>
""";
var result = BedrockMarketplaceParser.ParseSearch(list);
Check(result.Projects.Count == 2 && result.TotalCount == 206 && result.TotalPages == 7 && result.Page == 2, "real pagination and card boundaries");
Check(result.Projects[0].Name == "测试附加包" && result.Projects[0].Summary == "Behavior & resources", "Chinese names and decoded description");
Check(result.Projects[0].EnglishName == "Test Addon" && result.Projects[0].IconUrl.StartsWith("https://i.mcmod.cn/"), "English name and normalized icon");
Check(result.Projects[1].Name == "中文名字 & Other", "title entities");
Check(result.Projects[0].PageUrl == "https://www.mcmod.cn/class/10.html", "encyclopedia URL remains the actual indexed page");
var publicationProject = result.Projects[0];
var publicationNotifications = new HashSet<string>();
publicationProject.PropertyChanged += (_, args) => publicationNotifications.Add(args.PropertyName ?? "");
publicationProject.SetPublicationPageState(resolving: true);
Check(!publicationProject.CanOpenPublicationPage && publicationProject.PublicationActionText == "正在读取发布页…" &&
    publicationNotifications.Contains(nameof(BedrockMarketProject.CanOpenPublicationPage)), "item state keeps recycled cards disabled during a single open action");
publicationProject.SetPublicationPageState(resolving: false, unavailable: true);
Check(!publicationProject.CanOpenPublicationPage && publicationProject.PublicationActionText == "发布页面不可用", "missing source gives persistent explicit unavailable state");
publicationProject.SetPublicationPageState(resolving: false);
Check(publicationProject.CanOpenPublicationPage && publicationProject.PublicationActionText == "打开发布页面", "completed or failed lookup releases the loading state");
Reject(() => BedrockMarketplaceParser.ParseSearch(list.Replace("value=\"2\"", "value=\"1\"")), "Java directory must be rejected");
Reject(() => BedrockMarketplaceParser.ParseSearch("<html>captcha</html>"), "challenge page must not be empty success");
Reject(() => BedrockMarketplaceParser.ParseSearch("<input name='platform' value='2'><span>当前 1 / 1 页，共计 3 条。</span>"), "missing cards are source failure");
Check(BedrockMarketplaceParser.ParseSearch("<input name='platform' value='2'><p>没有找到符合条件的模组</p>").TotalCount == 0, "explicit empty result");
var onePage = "<div class='modlist-list-frame'>" + list.Replace("<span>当前 2 / 7 页，共计 206 条。</span>", "") + "</div>";
var single = BedrockMarketplaceParser.ParseSearch(onePage);
Check(single.TotalCount == 2 && single.Page == 1 && single.TotalPages == 1 && single.Projects.Count == 2, "real single-page search has no pagination markup");
Reject(() => BedrockMarketplaceParser.ParseSearch(list.Replace("<span>当前 2 / 7 页，共计 206 条。</span>", "")), "cards without real directory container and pagination are rejected");
var query = BedrockMarketplaceParser.BuildSearchUrl("中文 & x", 3);
Check(query.Contains("platform=2&page=3&key=") && query.Contains("%26") && !query.Contains(" 中文"), "query encoding retains Bedrock filter and page");
var filteredOptions = new BedrockMarketSearchOptions
{
    Query = "  中文 & Addon  ", Page = 4, Category = 3, Status = 1,
    Sort = BedrockMarketSort.RecentlyUpdated, FuzzySearch = true
};
var filteredUrl = BedrockMarketplaceParser.BuildSearchUrl(filteredOptions);
Check(filteredUrl.StartsWith("https://www.mcmod.cn/modlist.html?platform=2&page=4&key="), "combined filters keep Bedrock and requested page");
Check(filteredUrl.Contains("&category=3") && filteredUrl.Contains("&status=1") && filteredUrl.Contains("&sort=lastedittime") && filteredUrl.Contains("&nlp=ngram"), "real directory filter parameters combined");
Check(filteredUrl.Contains("%26") && !filteredUrl.Contains("%20%20"), "trimmed full-directory search is safely escaped");
Check(!filteredUrl.Contains("mcver") && !filteredUrl.Contains("api="), "unavailable Bedrock version and type parameters are not fabricated");
Check(BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Sort = BedrockMarketSort.Newest }).EndsWith("&sort=createtime"), "newest means source collection time");
Check(BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { FuzzySearch = true }) == "https://www.mcmod.cn/modlist.html?platform=2&page=1", "blank fuzzy query does not send meaningless option");
Check(BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Query = "中文 & Addon", Page = 4, Category = 3, Status = 1, Sort = BedrockMarketSort.RecentlyUpdated, FuzzySearch = true }) == filteredUrl, "normalized equivalent filters share cache identity");
Check(BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Category = 3 }) != BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Category = 23 }), "different full-directory themes have different cache keys");
Check(BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Status = 1 }) != BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Status = 3 }), "different status filters have different cache keys");
Check(BedrockMarketplaceParser.CategoryLabel(3) == "冒险" && BedrockMarketplaceParser.CategoryLabel(23) == "实用" && BedrockMarketplaceParser.CategoryLabel(24) == "辅助", "verified theme labels are not content package types");
Check(BedrockMarketplaceParser.ParseSearch(list, 3).Projects.All(p => p.CategoryLabel == "冒险"), "source-filtered category is carried to result cards");
RejectArgument(() => BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Page = 0 }), "invalid page rejected");
RejectArgument(() => BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Category = 99 }), "unverified category rejected");
RejectArgument(() => BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Status = 4 }), "unverified status rejected");
RejectArgument(() => BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Sort = (BedrockMarketSort)99 }), "unverified ordering rejected");
RejectArgument(() => BedrockMarketplaceParser.BuildSearchUrl(new BedrockMarketSearchOptions { Query = new string('x', 256) }), "oversized query rejected");

var lazyCover = list.Replace("src=\"//i.mcmod.cn/class/cover/test.png\"", "src=\"/static/public/images/loading.gif\" data-src=\"//i.mcmod.cn/class/cover/lazy.png?v=1&amp;a=2\"");
Check(BedrockMarketplaceParser.ParseSearch(lazyCover).Projects[0].IconUrl == "https://i.mcmod.cn/class/cover/lazy.png?v=1&a=2", "lazy cover preferred over placeholder with entities decoded");
var originalCover = list.Replace("src=\"//i.mcmod.cn/class/cover/test.png\"", "src=\"data:image/gif;base64,AA==\" data-original=\"/class/cover/original.png\"");
Check(BedrockMarketplaceParser.ParseSearch(originalCover).Projects[0].IconUrl == "https://www.mcmod.cn/class/cover/original.png", "relative lazy cover normalized to MC encyclopedia host");
var hostileCover = list.Replace("src=\"//i.mcmod.cn/class/cover/test.png\"", "data-src=\"https://i.mcmod.cn.evil.invalid/class/cover/p.png\" src=\"//i.mcmod.cn/class/cover/test.png\"");
Check(BedrockMarketplaceParser.ParseSearch(hostileCover).Projects[0].IconUrl == "https://i.mcmod.cn/class/cover/test.png", "untrusted lazy URL falls back to safe real cover");
var noCover = list.Replace("<div class=\"cover\"><img src=\"//i.mcmod.cn/class/cover/test.png\" /></div>", "<div class=\"items\"><img src=\"//i.mcmod.cn/item/icon/item.png\" /></div>");
Check(BedrockMarketplaceParser.ParseSearch(noCover).Projects[0].IconUrl.Length == 0, "item icon cannot masquerade as community project cover");
Check(BedrockMarketplaceParser.ParseSearch(list + "<footer><img src='//i.mcmod.cn/class/cover/footer.png'></footer>").Projects[1].IconUrl.Length == 0, "last missing cover cannot leak footer logo into project");
Check(BedrockMarketplaceParser.ParseSearch(list.Replace("src=\"//i.mcmod.cn/class/cover/test.png\"", "src=\"http://i.mcmod.cn/class/cover/test.png\"")).Projects[0].IconUrl.Length == 0, "insecure cover URL rejected");
Check(BedrockMarketplaceParser.ParseSearch(list.Replace("src=\"//i.mcmod.cn/class/cover/test.png\"", "src=\"https://i.mcmod.cn:444/class/cover/test.png\"")).Projects[0].IconUrl.Length == 0, "nondefault cover port rejected");
Check(BedrockMarketplaceParser.ParseSearch(list.Replace("src=\"//i.mcmod.cn/class/cover/test.png\"", "src=\"https://user@i.mcmod.cn/class/cover/test.png\"")).Projects[0].IconUrl.Length == 0, "cover credentials rejected");

var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://github.com/author/bedrock-addon"));
var detail = """
<li class="col-lg-4">支持平台: <a href="/modlist.html?platform=2">基岩版 (Bedrock Edition)</a></li>
<ul class="common-link-icon-frame common-link-icon-frame-style-3"><li><a href="//link.mcmod.cn/target/ENCODED">GitHub</a></li></ul>
<div class="description"><a href="https://github.com/unrelated/dependency">Unrelated dependency</a></div>
""".Replace("ENCODED", encoded);
var sources = BedrockMarketplaceParser.ParseProjectSources(detail);
Check(sources.Count == 1 && sources[0].AbsoluteUri == "https://github.com/author/bedrock-addon", "decode related link only");
var publicationPages = BedrockMarketplaceParser.PublicationPages(sources);
Check(publicationPages.Count == 1 && publicationPages[0].Url == sources[0].AbsoluteUri && publicationPages[0].Source == "GitHub",
    "publication action preserves the explicitly associated upstream page rather than the encyclopedia page");
Check(BedrockMarketplaceParser.PublicationPages(Array.Empty<Uri>()).Count == 0, "missing sources stay unavailable without a guessed address");
var otherPublisher = "https://mcpedl.com/my-real-addon/?from=project";
var publisherDetail = detail.Replace("</ul>", "<li><a href='" + otherPublisher + "'>作者发布页</a></li></ul>");
var expandedPages = BedrockMarketplaceParser.PublicationPages(BedrockMarketplaceParser.ParseProjectSources(publisherDetail));
Check(expandedPages.Count == 2 && expandedPages[1].Source == "mcpedl.com" && expandedPages[1].Url == otherPublisher,
    "MCPEDL and other explicitly associated publisher pages are preserved even without a supported download API");
var publisherPages = BedrockMarketplaceParser.PublicationPages(new[]
{
    new Uri("https://author.example/addons/real-pack/"), new Uri(otherPublisher),
    new Uri("https://www.curseforge.com/minecraft-bedrock/addons/test-pack"), sources[0],
    new Uri("https://cdn.author.example/asset"), new Uri("https://author.example/files/pack%2Emcaddon"),
    new Uri("https://author.example/download/pack"), new Uri("https://author.example/get?download=1"),
    new Uri("https://drive.google.com/uc?export=download&id=actual-file"),
    new Uri("https://author.example/export?export=download&id=actual-file"),
    new Uri("https://raw.githubusercontent.com/author/repo/main/manifest.json")
});
Check(publisherPages.Select(page => page.Source).SequenceEqual(new[] { "GitHub", "CurseForge", "mcpedl.com", "author.example" }),
    "recognized publishers are prioritized while direct files, CDN and explicit download endpoints stay unavailable");
Check(publisherPages.Last().Url == "https://author.example/addons/real-pack/", "other publisher URL is used verbatim rather than generating a release address");
Check(BedrockMarketplaceParser.PublicationPages(new[]
{
    sources[0], sources[0], new Uri("https://github.com/author/bedrock-addon/releases/download/1/p.mcpack"),
    new Uri("https://edge.forgecdn.net/files/1/p.mcpack"), new Uri("https://www.curseforge.com/minecraft-bedrock/addons/test-pack/download"),
    new Uri("https://www.curseforge.com/minecraft-bedrock/addons/test-pack")
}).Select(page => page.Source).SequenceEqual(new[] { "GitHub", "CurseForge" }), "publisher pages are distinct from asset and download links");
Reject(() => BedrockMarketplaceParser.ParseProjectSources(detail.Replace("支持平台:", "正文提到:")), "body text alone is not platform proof");
Reject(() => BedrockMarketplaceParser.ParseProjectSources(detail.Replace("platform=2", "platform=1")), "Java item must be rejected");
Check(BedrockMarketplaceParser.DecodeSourceLink("//link.mcmod.cn/target/not-base64!") == null, "invalid wrapper rejected");
Check(BedrockMarketplaceParser.DecodeSourceLink("http://github.com/a/b") == null, "insecure source rejected");
Check(BedrockMarketplaceParser.TryGithubRepository(new Uri("https://github.com/author/repo/releases"), out var owner, out var repository) && owner == "author" && repository == "repo", "GitHub repo from release link");
Check(!BedrockMarketplaceParser.TryGithubRepository(new Uri("https://github.com.evil.invalid/author/repo"), out _, out _), "GitHub host suffix spoof rejected");
Check(BedrockMarketplaceParser.TryCurseForgeProject(new Uri("https://www.curseforge.com/minecraft-bedrock/texture-packs/test-pack"), out var slug) && slug == "test-pack", "Bedrock resource pack source accepted");
Check(!BedrockMarketplaceParser.TryCurseForgeProject(new Uri("https://www.curseforge.com/minecraft/mc-mods/test-pack"), out _), "Java CurseForge source rejected");
Check(!BedrockMarketplaceParser.TryCurseForgeProject(new Uri("https://www.curseforge.com/minecraft-bedrock/maps/test-world"), out _), "world source excluded");
var cfSource = new Uri("https://www.curseforge.com/minecraft-bedrock/addons/test-addon");
const string cfJson = """{"gameId":78022,"slug":"test-addon","links":{"websiteUrl":"https://www.curseforge.com/minecraft-bedrock/addons/test-addon"}}""";
using var cf = JsonDocument.Parse(cfJson);
using var java = JsonDocument.Parse(cfJson.Replace("78022", "432"));
using var wrongSlug = JsonDocument.Parse(cfJson.Replace("\"slug\":\"test-addon\"", "\"slug\":\"test-addon-port\""));
using var wrongClass = JsonDocument.Parse(cfJson.Replace("/addons/", "/texture-packs/"));
Check(BedrockMarketplaceParser.MatchesCurseForgeProject(cf.RootElement, cfSource, "test-addon"), "exact Bedrock game slug and URL match");
Check(!BedrockMarketplaceParser.MatchesCurseForgeProject(java.RootElement, cfSource, "test-addon"), "same name in Java game rejected");
Check(!BedrockMarketplaceParser.MatchesCurseForgeProject(wrongSlug.RootElement, cfSource, "test-addon"), "nearby slug rejected");
Check(!BedrockMarketplaceParser.MatchesCurseForgeProject(wrongClass.RootElement, cfSource, "test-addon"), "same slug in other class rejected");

var releases = """
[
  {"tag_name":"1.2","html_url":"https://github.com/author/repo/releases/tag/1.2","published_at":"2026-01-01T00:00:00Z","assets":[
    {"name":"pack.mcpack","size":123,"browser_download_url":"https://github.com/renamed/repo/releases/download/1/pack.mcpack","digest":"sha256:abc"},
    {"name":"pack.mcaddon","browser_download_url":"https://github.com/author/repo/releases/download/1/pack.mcaddon"},
    {"name":"pack.mcpack.zip","browser_download_url":"https://github.com/author/repo/releases/download/1/pack.mcpack.zip"},
    {"name":"java.jar","browser_download_url":"https://github.com/author/repo/releases/download/1/java.jar"},
    {"name":"unsafe.mcpack","browser_download_url":"https://example.invalid/unsafe.mcpack"}
  ]},
  {"tag_name":"draft","draft":true,"assets":[{"name":"draft.mcpack","browser_download_url":"https://github.com/a/b/releases/download/1/draft.mcpack"}]}
]
""";
var files = BedrockMarketplaceParser.ParseGithubReleases(releases);
Check(files.Count == 3, "only supported public release assets");
Check(files[0].Size == 123 && files[0].Sha256 == "abc" && files[0].PublishedAt.HasValue, "preserve API checksum size and date");
Check(files.All(file => file.ReleasePageUrl == "https://github.com/author/repo/releases/tag/1.2") &&
    files.All(file => file.ReleasePageUrl != file.DownloadUrl), "GitHub API html_url remains separate from package download URLs");
Check(BedrockMarketplaceParser.ParseGithubReleases(releases.Replace("\"html_url\":\"https://github.com/author/repo/releases/tag/1.2\",", ""))
    .All(file => file.ReleasePageUrl.Length == 0), "missing release page never falls back to the asset URL");
foreach (var unavailablePage in new[]
{
    "javascript:alert(1)", "file:///tmp/p.mcpack", "http://github.com/author/repo/releases/tag/1.2",
    "https://github.com.evil.invalid/author/repo/releases/tag/1.2", "https://user@github.com/author/repo/releases/tag/1.2",
    "https://github.com:444/author/repo/releases/tag/1.2", "https://github.com/author/repo/releases/download/1/p.mcpack"
})
    Check(BedrockMarketplaceParser.ParseGithubReleases(releases.Replace("https://github.com/author/repo/releases/tag/1.2", unavailablePage))
        .All(file => file.ReleasePageUrl.Length == 0), "invalid or direct-download release page is unavailable: " + unavailablePage);
Check(BedrockMarketplaceParser.PublicationPageUrl("https://www.curseforge.com/minecraft-bedrock/addons/test-pack/files/42", "CurseForge").Length > 0 &&
    BedrockMarketplaceParser.PublicationPageUrl("https://www.curseforge.com/minecraft-bedrock/addons/test-pack/files/42/download", "CurseForge").Length == 0,
    "CurseForge explicit file webpage is allowed while download action is excluded");
Check(files[2].Name.EndsWith(".mcpack.zip"), "wrapped mcpack allowed for manifest inspection");
Check(!BedrockMarketplaceParser.IsOfficialFileUrl("https://forgecdn.net.evil.invalid/p.mcpack", "CurseForge"), "CDN suffix spoof rejected");
Check(!BedrockMarketplaceParser.IsOfficialFileUrl("https://user@github.com/a/b", "GitHub"), "userinfo rejected");
Check(!BedrockMarketplaceParser.IsOfficialFileUrl("https://github.com:444/a/b", "GitHub"), "nondefault port rejected");
Check(BedrockMarketplaceParser.IsOfficialFileUrl("https://edge.forgecdn.net/files/1/p.mcpack", "CurseForge"), "official CurseForge download host accepted");
Console.WriteLine($"Bedrock marketplace parser regression: {checks} checks passed.");
