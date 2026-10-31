using System.Text;
using System.Globalization;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed record ExperienceSearchPage(int Index, int Loaded, int FirstTotal, int SecondTotal);
public sealed record ExperienceSearchRecovery(int Index, string Caption, bool PreviousEnabled, bool NextEnabled);

/// <summary>Pure local matching and bounded, explicit search persistence. Never starts network work.</summary>
public static class ExperienceSearchState
{
    public const int HistoryLimit = 20;
    public const int PresetLimit = 10;

    public static ExperienceSearchRecovery RecoverCanceledPage(ExperienceSearchPage? page, int pageSize)
    {
        if (page == null) return new(0, "搜索已取消，可重新搜索。", false, false);
        var index = Math.Max(0, page.Index);
        var nextOffset = ((long)index + 1) * Math.Max(1, pageSize);
        return new(index, $"搜索已取消；保留第 {index + 1} 页，本页 {Math.Max(0, page.Loaded)} 条。", index > 0,
            nextOffset < page.FirstTotal || nextOffset < page.SecondTotal);
    }

    /// <summary>Imported JSON may contain explicit null collections, rows and dictionary values.</summary>
    public static void Normalize(AppConfig config)
    {
        config.Ux50SearchHistoryItems = (config.Ux50SearchHistoryItems ?? new())
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Scope) && item.Scope.Length <= 80 &&
                !string.IsNullOrWhiteSpace(item.Query) && item.Query.Length <= 200)
            .Take(HistoryLimit).ToList();
        config.Ux50SearchFilterPresets = (config.Ux50SearchFilterPresets ?? new())
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Scope) && item.Scope.Length <= 80)
            .Take(PresetLimit).ToList();
        foreach (var item in config.Ux50SearchFilterPresets)
        {
            item.Label = string.IsNullOrWhiteSpace(item.Label) ? "已保存筛选" : item.Label[..Math.Min(item.Label.Length, 80)];
            item.Values = CleanValues(item.Values);
        }
    }

    private static Dictionary<string, string> CleanValues(Dictionary<string, string>? values)
        => (values ?? new()).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 80)
            .Take(12).ToDictionary(pair => pair.Key, pair => (pair.Value ?? "")[..Math.Min(pair.Value?.Length ?? 0, 200)]);

    public static string[] Keywords(string? query, bool multiple = true)
    {
        var normalized = FeatureSearchMatcher.Normalize(query);
        if (normalized.Length == 0) return Array.Empty<string>();
        return multiple ? normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray()
            : new[] { normalized };
    }

    public static bool Matches(string? text, string? query, bool multiple = true)
    {
        var normalized = FeatureSearchMatcher.Normalize(text);
        return Keywords(query, multiple).All(word => normalized.Contains(word, StringComparison.Ordinal));
    }

    public static void Remember(AppConfig config, string scope, string? query)
    {
        if (!config.Ux50SearchHistory) return;
        Normalize(config);
        var value = query?.Trim() ?? "";
        if (value.Length == 0 || value.Length > 200 || scope.Length > 80) return;
        config.Ux50SearchHistoryItems ??= new();
        config.Ux50SearchHistoryItems.RemoveAll(item => item.Scope == scope && string.Equals(item.Query, value, StringComparison.OrdinalIgnoreCase));
        config.Ux50SearchHistoryItems.Insert(0, new() { Scope = scope, Query = value });
        if (config.Ux50SearchHistoryItems.Count > HistoryLimit)
            config.Ux50SearchHistoryItems.RemoveRange(HistoryLimit, config.Ux50SearchHistoryItems.Count - HistoryLimit);
    }

    public static void SavePreset(AppConfig config, string scope, string label, Dictionary<string, string> values)
    {
        if (!config.Ux50SearchSavedFilters) return;
        Normalize(config);
        var snapshot = CleanValues(values);
        config.Ux50SearchFilterPresets.RemoveAll(item => item.Scope == scope && item.Values.Count == snapshot.Count
            && item.Values.All(pair => snapshot.TryGetValue(pair.Key, out var value) && value == pair.Value));
        label ??= "已保存筛选";
        config.Ux50SearchFilterPresets.Insert(0, new() { Scope = scope, Label = label.Length > 80 ? label[..80] : label, Values = snapshot });
        if (config.Ux50SearchFilterPresets.Count > PresetLimit)
            config.Ux50SearchFilterPresets.RemoveRange(PresetLimit, config.Ux50SearchFilterPresets.Count - PresetLimit);
    }

    /// <summary>Indices refer to the original string; width normalization must not shift highlights.</summary>
    public static IReadOnlyList<(int Start, int Length)> HighlightRanges(string text, string? query)
    {
        var ranges = new List<(int Start, int Length)>();
        var normalized = new StringBuilder();
        var origins = new List<(int Start, int End)>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            string canonical = element.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
            foreach (char value in canonical)
            {
                normalized.Append(char.IsWhiteSpace(value) ? ' ' : value);
                origins.Add((elements.ElementIndex, elements.ElementIndex + element.Length));
            }
        }
        string searchable = normalized.ToString();
        foreach (var word in Keywords(query))
        {
            for (int offset = 0; offset < searchable.Length;)
            {
                int start = searchable.IndexOf(word, offset, StringComparison.Ordinal);
                if (start < 0) break;
                int originalStart = origins[start].Start;
                ranges.Add((originalStart, origins[start + word.Length - 1].End - originalStart));
                offset = start + word.Length;
            }
        }
        var merged = new List<(int Start, int Length)>();
        foreach (var range in ranges.OrderBy(item => item.Start).ThenByDescending(item => item.Length))
        {
            if (merged.Count == 0 || range.Start > merged[^1].Start + merged[^1].Length) merged.Add(range);
            else
            {
                var old = merged[^1];
                merged[^1] = (old.Start, Math.Max(old.Start + old.Length, range.Start + range.Length) - old.Start);
            }
        }
        return merged;
    }

    public static List<T> StableOrder<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, bool descending, IComparer<TKey>? comparer = null)
    {
        var indexed = items.Select((item, index) => (Item: item, Index: index));
        return (descending ? indexed.OrderByDescending(pair => key(pair.Item), comparer)
            : indexed.OrderBy(pair => key(pair.Item), comparer)).ThenBy(pair => pair.Index).Select(pair => pair.Item).ToList();
    }

    public static string LoadedSummary(int pageIndex, int loaded, int firstTotal, int secondTotal, int pageSize)
    {
        // Combined-source pagination advances each source independently. Totals may overlap projects.
        int pages = Math.Max((int)Math.Ceiling(Math.Max(0, firstTotal) / (double)pageSize),
            (int)Math.Ceiling(Math.Max(0, secondTotal) / (double)pageSize));
        return $"本页已加载 {Math.Max(0, loaded)} 条 · 第 {pageIndex + 1} 页" + (pages > 0 ? $" / {pages}" : "");
    }
}
