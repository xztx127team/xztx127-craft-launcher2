namespace XCL2.App.Services;

/// <summary>Reads fenced code directly from the response, without introducing display-wrap line breaks.</summary>
internal static class AiMarkdownCodeText
{
    internal readonly record struct SourceLine(string Text, int Start, int End);

    internal static IReadOnlyList<SourceLine> SplitLines(string source)
    {
        var lines = new List<SourceLine>();
        int start = 0;
        while (start < source.Length)
        {
            int end = start;
            while (end < source.Length && source[end] is not '\r' and not '\n') end++;
            int next = end;
            if (next < source.Length && source[next++] == '\r' && next < source.Length && source[next] == '\n') next++;
            lines.Add(new SourceLine(source[start..end], start, next));
            start = next;
        }
        if (source.Length == 0 || source[^1] is '\r' or '\n')
            lines.Add(new SourceLine(string.Empty, source.Length, source.Length));
        return lines;
    }

    internal static bool IsOpeningFence(string line)
        => TryOpeningFence(line, out _, out _, out _);

    internal static bool TryRead(string source, IReadOnlyList<SourceLine> lines, int index,
        out string code, out string language, out int nextIndex)
    {
        code = language = string.Empty;
        nextIndex = index;
        if (index < 0 || index >= lines.Count ||
            !TryOpeningFence(lines[index].Text, out char marker, out int width, out language)) return false;

        int end = index + 1;
        while (end < lines.Count && !IsClosingFence(lines[end].Text, marker, width)) end++;
        int startOffset = lines[index].End;
        int endOffset = end < lines.Count ? lines[end].Start : source.Length;
        code = source[startOffset..endOffset];
        nextIndex = end < lines.Count ? end + 1 : end;
        return true;
    }

    private static bool TryOpeningFence(string line, out char marker, out int width, out string language)
    {
        var trimmed = FenceContent(line);
        marker = trimmed.Length > 0 ? trimmed[0] : '\0';
        width = 0;
        language = string.Empty;
        if (marker is not '`' and not '~') return false;
        while (width < trimmed.Length && trimmed[width] == marker) width++;
        if (width < 3) return false;
        language = trimmed[width..].Trim();
        return marker != '`' || !language.Contains('`');
    }

    private static bool IsClosingFence(string line, char marker, int width)
    {
        var trimmed = FenceContent(line).TrimEnd(' ', '\t');
        int count = 0;
        while (count < trimmed.Length && trimmed[count] == marker) count++;
        return count >= width && count == trimmed.Length;
    }

    // A fence may be indented by at most three spaces. More-indented backticks are code
    // content, so treating them as a closing fence would silently truncate copied output.
    private static string FenceContent(string line)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        return indent <= 3 ? line[indent..] : string.Empty;
    }
}
