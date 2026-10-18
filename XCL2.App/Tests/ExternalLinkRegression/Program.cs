using XCL2.App.Services;
using XCL2.App.Views.Tools;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

Require(ExternalLinkService.TryGetWebUri(" https://example.com/path?q=hello ", out var url)
    && url!.AbsoluteUri == "https://example.com/path?q=hello", "Valid HTTPS address rejected");
Require(ExternalLinkService.TryGetWebUri("http://localhost:8080/", out _), "Local HTTP address rejected");
foreach (var invalid in new[] { "", "file:///tmp/test", "javascript:alert(1)", "shell:AppsFolder", "https://", "https://example.com/\nnext" })
    Require(!ExternalLinkService.TryGetWebUri(invalid, out _), "Invalid/non-web address accepted: " + invalid);

var page = new Web2BrowserTool();
Require(!page.HasFallback, "External fallback shown before any failure");
var core = await page.AttachFixtureBrowserAsync();
core.Start(1, "about:blank");
core.Complete(1, false);
Require(!page.HasFallback, "Initialization navigation enabled fallback");

const string address = "https://example.com/page";
page.RequestFixtureNavigation(address);
core.Start(10, address);
core.Complete(10, false);
Require(page.HasFallback && page.Fallback == address, "Current navigation failure missing fallback");

// The identical address is retried. Old completion events can arrive on either side of NavigationStarting.
page.RequestFixtureNavigation(address);
Require(!page.HasFallback, "Retry did not clear previous failure");
core.Complete(10, false);
core.Complete(10, true);
Require(!page.HasFallback && page.Status != "页面已加载。", "Old completion accepted before retry starts");
core.Start(11, address);
core.Complete(10, false);
core.Complete(10, true);
Require(!page.HasFallback && page.Status == "正在加载页面…", "Old same-address completion overwrote retry status");
core.Complete(11, true);
Require(!page.HasFallback && page.Status == "页面已加载。", "Current retry success not applied");
core.Complete(10, false);
Require(!page.HasFallback && page.Status == "页面已加载。", "Late old failure replaced successful page");

// A redirect follows the current navigation ID; external fallback targets that actual address.
page.RequestFixtureNavigation(address);
core.Start(12, address);
const string redirected = "https://example.com/redirected";
core.Start(12, redirected);
core.Complete(12, false);
Require(page.HasFallback && page.Fallback == redirected, "Redirect failed with stale external target");

page.EditAddress("https://different.example/");
Require(!page.HasFallback, "Editing address retained old external target");
core.Complete(12, false);
Require(!page.HasFallback, "Old page failure applied after address edit");
page.EditAddress(address);
core.Complete(12, false);
Require(!page.HasFallback, "Editing back to old address revived stale failure");

page.RequestFixtureNavigation(address);
core.Start(13, address);
core.Complete(13, false);
Require(page.HasFallback, "Current address failure not shown after edit/retry");
page.LeavePage();
Require(!page.HasFallback, "Leaving page retained external target");
core.Start(14, address);
core.Complete(14, false);
Require(!page.HasFallback, "Disposed browser updated fallback");
Console.WriteLine("External web links and Web2 fallback regression checks passed.");
