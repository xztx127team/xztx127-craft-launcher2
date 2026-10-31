using System.Net.Http;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

public static class ForgeVersionQueryService
{
    public const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    /// <summary>备用 host：极少数情况下 files.minecraftforge.net 抽风（DNS/CDN 问题）时的兜底，
    /// 官方镜像文档里两个 host 都出现过服务该文件，具体以实际探测为准，两个都失败才真正报错。</summary>
    public const string ForgePromotionsUrlFallback = "https://maven.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    public const string NeoForgeMavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    private static async Task<JsonDocument> GetPromotionsDocAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var json = await http.GetStringAsync(ForgePromotionsUrl, ct);
            return JsonDocument.Parse(json);
        }
        catch (Exception primaryEx) when (primaryEx is not OperationCanceledException)
        {
            try
            {
                var json = await http.GetStringAsync(ForgePromotionsUrlFallback, ct);
                return JsonDocument.Parse(json);
            }
            catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"获取 Forge 版本信息失败：主/备用地址均无法访问。\n主地址：{ForgePromotionsUrl}\n错误：{primaryEx.Message}\n" +
                    "这通常是网络问题或 Forge 官方服务临时不可用，请稍后重试；如果长期失败可能是官方接口地址又变了。",
                    fallbackEx);
            }
        }
    }

    /// <summary>Forge：有官方安装器构建的 MC 版本列表（从 promotions_slim.json 的 key 中提取）。</summary>
    public static async Task<List<string>> GetForgeVersionsAsync(HttpClient http, CancellationToken ct = default)
    {
        using var doc = await GetPromotionsDocAsync(http, ct);
        var result = new List<string>();
        // promotions_slim.json 的 promos 对象 key 形如 "1.20.1-recommended" / "1.20.1-latest"
        foreach (var promo in doc.RootElement.GetProperty("promos").EnumerateObject())
        {
            var mcVer = promo.Name.Split('-')[0];
            if (!result.Contains(mcVer)) result.Add(mcVer);
        }
        return result;
    }

    /// <summary>Forge：某个 MC 版本对应的 recommended/latest 安装器版本号（完整 "mcver-forgever" 格式）。</summary>
    public static async Task<List<ServerCoreBuild>> GetForgeInstallerVersionsAsync(HttpClient http, string mcVersion, CancellationToken ct = default)
    {
        using var doc = await GetPromotionsDocAsync(http, ct);
        var result = new List<ServerCoreBuild>();
        foreach (var promo in doc.RootElement.GetProperty("promos").EnumerateObject())
        {
            if (!promo.Name.StartsWith(mcVersion + "-")) continue;
            result.Add(new ServerCoreBuild
            {
                // promo.Value 就是完整 forge 版本号字符串，如 "47.2.20"
                DisplayVersion = $"{mcVersion}-{promo.Value.GetString()}",
                IsRecommended = promo.Name.EndsWith("-recommended")
            });
        }
        return result.DistinctBy(r => r.DisplayVersion).ToList();
    }

    /// <summary>
    /// NeoForge：可用的完整版本号列表（NeoForge 版本号和 MC 版本号不是直接对应的独立编号体系）。
    ///
    /// 修复"获取版本信息失败，404"：NeoForged 的 maven 仓库和绝大多数标准 Maven 仓库一样，
    /// 实际只发布 maven-metadata.xml，并不提供 maven-metadata.json 端点——之前的代码把 json
    /// 当第一选择去请求，请求直接 404，.NET 的 HttpClient.GetStringAsync 对非成功状态码抛出的
    /// 是 HttpRequestException，而原来的 catch 只捕获了 JsonException，所以 xml 回退分支根本
    /// 不会被触发，异常直接原样冒泡到上层，表现为"获取版本信息失败"。
    /// 现在直接把 xml 作为主路径（这是官方唯一保证存在的格式），不再尝试不存在的 json 端点。
    /// </summary>
    public static async Task<List<string>> GetNeoForgeVersionsAsync(HttpClient http, CancellationToken ct = default)
    {
        var xml = await http.GetStringAsync($"{NeoForgeMavenBase}/maven-metadata.xml", ct);
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        var nodes = doc.SelectNodes("//version");
        var result = new List<string>();
        if (nodes != null)
            foreach (System.Xml.XmlNode n in nodes)
                if (!string.IsNullOrWhiteSpace(n.InnerText)) result.Add(n.InnerText);
        result.Reverse();
        return result;
    }
}
