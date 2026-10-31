using XCL2.App.Models;

namespace XCL2.App.Services;

public static class ModDisplayNameResolver
{
    /// <summary>WikiEntry.All 按 (Source, Slug) 建的索引，Slug 统一转小写比较——
    /// Modrinth slug 大小写在实践中基本都是小写，这里做一次 ToLowerInvariant 兜底更保险。
    /// 惰性构建一次，跟 WikiEntry.All 本身的惰性加载策略一致。</summary>
    private static readonly Lazy<Dictionary<(ModSource Source, string Slug), string>> _bySlug = new(() =>
    {
        var dict = new Dictionary<(ModSource, string), string>();
        foreach (var entry in WikiEntry.All)
        {
            if (string.IsNullOrEmpty(entry.ChineseName)) continue;
            foreach (var (source, slug) in entry.Slugs)
            {
                if (string.IsNullOrEmpty(slug)) continue;
                var key = (source, slug.ToLowerInvariant());
                // 同一个 slug 在数据文件里可能出现多次（不同别名行），保留第一条即可，
                // 数据整理时越靠前的别名通常越是"官方/最常用"的那个中文名。
                if (!dict.ContainsKey(key)) dict[key] = entry.ChineseName!;
            }
        }
        return dict;
    });

    /// <summary>
    /// 给定平台 + Slug + 该结果原本的英文标题，返回展示用的标题：
    /// 查到中文名 → "中文名 (English Title)"；查不到 → 原样返回英文标题。
    /// slug 为空（目前只有 CurseForge 会传空）时直接返回原标题，不查表。
    /// </summary>
    public static string Resolve(ModSource source, string? slug, string englishTitle)
    {
        if (string.IsNullOrEmpty(slug)) return englishTitle;
        if (!_bySlug.Value.TryGetValue((source, slug.ToLowerInvariant()), out var chineseName))
            return englishTitle;

        // 中文名和英文标题偶尔会重复（数据库里个别条目中文名就是照抄英文名），这种情况不用再
        // 显示一遍 "Foo (Foo)"，直接返回英文标题即可。
        if (string.Equals(chineseName, englishTitle, StringComparison.OrdinalIgnoreCase))
            return englishTitle;

        return $"{chineseName} ({englishTitle})";
    }

    /// <summary>
    /// 给定平台 + Slug，查该 Mod/资源的中文名，查不到返回 null。
    /// 跟 <see cref="Resolve"/> 共用同一份索引，区别是该方法不拼英文标题，
    /// 单纯给"要不要在文件名里加中文名"这类场景用——查不到就什么都不加，不用占位符。
    /// slug 为空（CurseForge 目前查不到 slug）时直接返回 null，不查表。
    /// </summary>
    public static string? GetChineseName(ModSource source, string? slug)
    {
        if (string.IsNullOrEmpty(slug)) return null;
        return _bySlug.Value.TryGetValue((source, slug.ToLowerInvariant()), out var chineseName) ? chineseName : null;
    }
}
