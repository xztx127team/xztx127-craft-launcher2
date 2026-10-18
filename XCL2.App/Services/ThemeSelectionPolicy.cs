namespace XCL2.App.Services;

/// <summary>配色选择的纯逻辑：固定模式只保留一个候选，轮换模式可保留多选。</summary>
public static class ThemeSelectionPolicy
{
    public static bool IsSameChoice(string oldSkin, string? oldColor, string newSkin, string? newColor)
        => string.Equals(oldSkin, newSkin, StringComparison.Ordinal)
           && (!string.Equals(newSkin, "Custom", StringComparison.Ordinal)
               || string.Equals(oldColor, newColor, StringComparison.OrdinalIgnoreCase));

    public static List<string> FixedCandidates(IEnumerable<string> candidates, string? preferredSkin)
    {
        var distinct = candidates.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        var chosen = distinct.Contains(preferredSkin ?? "", StringComparer.Ordinal) ? preferredSkin : distinct.FirstOrDefault();
        return chosen == null ? new List<string>() : new List<string> { chosen };
    }
}
