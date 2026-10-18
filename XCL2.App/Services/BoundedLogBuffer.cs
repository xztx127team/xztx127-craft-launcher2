using System.Text;

namespace XCL2.App.Services;

/// <summary>Bounded live view; the caller can persist discarded prefixes to retain the session on disk.</summary>
public sealed class BoundedLogBuffer
{
    public const string TruncatedNotice = "[实时视图仅保留最近日志；完整内容请查看会话日志文件]";
    private readonly StringBuilder _buffer = new();
    private readonly int _maximum;
    private readonly int _retained;
    private string? _cachedText;
    public long Revision { get; private set; }
    public bool HasDiscarded { get; private set; }
    public int Length => _buffer.Length;

    public BoundedLogBuffer(int maximum = 1024 * 1024, int retained = 768 * 1024)
    {
        if (maximum < 256 || retained < 1 || retained > maximum) throw new ArgumentOutOfRangeException(nameof(maximum));
        _maximum = maximum; _retained = retained;
    }

    public string Append(string text)
    {
        if (text.Length == 0) return "";
        _buffer.Append(text);
        Revision++; _cachedText = null;
        if (_buffer.Length <= _maximum) return "";
        var remove = _buffer.Length - _retained;
        // Prefer complete lines, but a single huge line must not defeat the memory bound.
        for (var i = remove; i < _buffer.Length; i++)
            if (_buffer[i] == '\n') { remove = i + 1; break; }
        if (remove > 0 && remove < _buffer.Length && char.IsHighSurrogate(_buffer[remove - 1]) && char.IsLowSurrogate(_buffer[remove])) remove++;
        var discarded = _buffer.ToString(0, remove);
        _buffer.Remove(0, remove);
        if (_buffer.Capacity > _maximum) _buffer.Capacity = _maximum;
        HasDiscarded = true;
        return discarded;
    }

    public string GetText(bool includeTruncationNotice = true)
    {
        if (!includeTruncationNotice) return _buffer.ToString();
        return _cachedText ??= (HasDiscarded ? TruncatedNotice + Environment.NewLine : "") + _buffer;
    }

    public bool TryGetChangedText(ref long revision, out string text)
    {
        if (revision == Revision) { text = ""; return false; }
        text = GetText(); revision = Revision; return true;
    }

    public string GetTail(int maximum)
    {
        var count = Math.Min(_buffer.Length, Math.Max(0, maximum));
        var text = _buffer.ToString(_buffer.Length - count, count);
        if (_buffer.Length > count)
        {
            var newline = text.IndexOf('\n');
            return newline >= 0 ? text[(newline + 1)..] : "[超长单行日志已省略]";
        }
        return text;
    }
}
