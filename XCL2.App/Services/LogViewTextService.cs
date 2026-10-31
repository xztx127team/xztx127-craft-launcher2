using System.Text;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public sealed record LogViewLine(int SourceLine, string Text);
public sealed record LogViewSnapshot(string Text, IReadOnlyList<LogViewLine> Lines, bool IsFiltered);

/// <summary>View-only projection. Never alters the original log, analysis input or exported file.</summary>
public static class LogViewTextService
{
    public static bool AcceptLiveOutput(object? source, object? subscribed, object? selected,
        int generation, int currentGeneration, bool loaded)
        => loaded && generation == currentGeneration && source != null &&
            ReferenceEquals(source, subscribed) && ReferenceEquals(source, selected);

    private static readonly Regex Severity = new(@"(?:\[|\b)(TRACE|DEBUG|INFO|WARN(?:ING)?|ERROR|FATAL)(?:\]|\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static LogViewSnapshot Project(string? raw, string? keyword, string? level)
    {
        raw ??= "";
        keyword = keyword?.Trim() ?? "";
        level = level?.ToUpperInvariant() ?? "";
        var filtered = keyword.Length > 0 || level.Length > 0;
        var rows = new List<LogViewLine>();
        var currentLevel = "";
        var sourceLines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < sourceLines.Length; i++)
        {
            var line = sourceLines[i];
            Match? match = null;
            try { match = Severity.Match(line); } catch (RegexMatchTimeoutException) { }
            if (match?.Success == true) currentLevel = match.Groups[1].Value.ToUpperInvariant().Replace("WARNING", "WARN");
            // Unknown leading lines stay visible so an unclassified error cannot silently disappear.
            var matchesLevel = level.Length == 0 || currentLevel.Length == 0 || currentLevel == level;
            if (matchesLevel && (keyword.Length == 0 || line.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                rows.Add(new(i + 1, line));
        }
        return new(string.Join(Environment.NewLine, rows.Select(x => x.Text)), rows, filtered);
    }

    public static string CopySelection(LogViewSnapshot view, int startCharacter, string selectedText, bool lineNumbers)
    {
        if (!lineNumbers || selectedText.Length == 0) return selectedText;
        var start = Math.Clamp(startCharacter, 0, view.Text.Length);
        var firstLine = 0;
        for (var i = 0; i < start; i++) if (view.Text[i] == '\n') firstLine++;
        var selected = selectedText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new StringBuilder();
        for (var i = 0; i < selected.Length; i++)
        {
            if (i > 0) output.AppendLine();
            // An empty terminal fragment belongs to the newline, not to another selected log line.
            if (i == selected.Length - 1 && selected[i].Length == 0) break;
            var line = firstLine + i;
            var number = line < view.Lines.Count ? view.Lines[line].SourceLine : line + 1;
            output.Append(number).Append(": ").Append(selected[i]);
        }
        return output.ToString();
    }
}
