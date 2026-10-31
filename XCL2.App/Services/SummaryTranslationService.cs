using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Click-only translation of public resource summaries. No account/configuration data is accepted.</summary>
internal sealed class SummaryTranslationService
{
    internal const int MaxQueryUtf8Bytes = 500;
    internal const int MaxSummaryUtf8Bytes = 4000;
    private const int MaxTranslationSegments = 32;
    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxTranslatedCharacters = 16000;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly int _cacheCapacity;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();

    public static SummaryTranslationService Shared { get; } = new(CreateHttpClient());

    internal SummaryTranslationService(HttpClient http, TimeSpan? timeout = null, int cacheCapacity = 64)
    {
        _http = http;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _cacheCapacity = Math.Clamp(cacheCapacity, 1, 64);
    }

    private static HttpClient CreateHttpClient()
    {
        // A redirect must not send the summary to an unexpected service. There are no credentials/cookies.
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher/2.2 public-summary-translation");
        return http;
    }

    public async Task<string> TranslateToChineseAsync(string summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(summary)) return summary;
        if (Encoding.UTF8.GetByteCount(summary) > MaxSummaryUtf8Bytes)
            throw new SummaryTranslationException(SummaryTranslationFailure.TooLong);
        var segments = SplitSummary(summary);
        if (segments.Count(segment => segment.Translate) > MaxTranslationSegments)
            throw new SummaryTranslationException(SummaryTranslationFailure.TooLong);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var entered = false;
        try
        {
            await _requestGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (_cache.TryGetValue(summary, out var cached)) return cached;

            var translated = new StringBuilder();
            foreach (var segment in segments)
            {
                timeout.Token.ThrowIfCancellationRequested();
                translated.Append(segment.Translate
                    ? await TranslateSegmentAsync(segment.Text, timeout.Token).ConfigureAwait(false)
                    : segment.Text);
                if (translated.Length > MaxTranslatedCharacters)
                    throw new SummaryTranslationException(SummaryTranslationFailure.InvalidResponse);
            }

            // Only a complete successful result enters the bounded in-memory cache.
            timeout.Token.ThrowIfCancellationRequested();
            var result = translated.ToString();
            if (_cache.Count >= _cacheCapacity) _cache.Remove(_cacheOrder.Dequeue());
            _cache.Add(summary, result);
            _cacheOrder.Enqueue(summary);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SummaryTranslationException(SummaryTranslationFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            throw new SummaryTranslationException(SummaryTranslationFailure.Unavailable);
        }
        catch (IOException)
        {
            throw new SummaryTranslationException(SummaryTranslationFailure.Unavailable);
        }
        catch (JsonException)
        {
            throw new SummaryTranslationException(SummaryTranslationFailure.InvalidResponse);
        }
        finally
        {
            if (entered) _requestGate.Release();
        }
    }

    private async Task<string> TranslateSegmentAsync(string text, CancellationToken cancellationToken)
    {
        // MyMemory documents a 500 UTF-8 byte limit, not 500 UTF-16 characters.
        var url = "https://api.mymemory.translated.net/get?langpair=en%7Czh-CN&q=" + Uri.EscapeDataString(text);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SummaryTranslationException(SummaryTranslationFailure.QuotaExceeded);
        if (!response.IsSuccessStatusCode)
            throw new SummaryTranslationException(SummaryTranslationFailure.Unavailable);
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new SummaryTranslationException(SummaryTranslationFailure.InvalidResponse);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new SummaryTranslationException(SummaryTranslationFailure.InvalidResponse);
            buffer.Write(chunk, 0, read);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SummaryTranslationException(SummaryTranslationFailure.InvalidResponse);
        if (root.TryGetProperty("quotaFinished", out var quota) &&
            (quota.ValueKind == JsonValueKind.True ||
             (quota.ValueKind == JsonValueKind.String && string.Equals(quota.GetString(), "true", StringComparison.OrdinalIgnoreCase))))
            throw new SummaryTranslationException(SummaryTranslationFailure.QuotaExceeded);
        var statusCode = 0;
        if (root.TryGetProperty("responseStatus", out var status))
        {
            if (status.ValueKind == JsonValueKind.Number) status.TryGetInt32(out statusCode);
            else if (status.ValueKind == JsonValueKind.String) int.TryParse(status.GetString(), out statusCode);
        }
        if (statusCode == 429)
            throw new SummaryTranslationException(SummaryTranslationFailure.QuotaExceeded);
        if (statusCode != 200 || !root.TryGetProperty("responseData", out var data) ||
            data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("translatedText", out var textValue) ||
            textValue.ValueKind != JsonValueKind.String)
            throw new SummaryTranslationException(SummaryTranslationFailure.InvalidResponse);
        var result = WebUtility.HtmlDecode(textValue.GetString())?.Trim();
        if (string.IsNullOrWhiteSpace(result))
            throw new SummaryTranslationException(SummaryTranslationFailure.InvalidResponse);
        return result;
    }

    internal readonly record struct Segment(string Text, bool Translate);

    internal static IReadOnlyList<Segment> SplitSummary(string text)
    {
        var segments = new List<Segment>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            // Keep original line breaks, indentation and spaces around chunk boundaries locally.
            var start = cursor;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor > start) segments.Add(new Segment(text[start..cursor], false));
            if (cursor == text.Length) break;
            var lineEnd = cursor;
            while (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n') lineEnd++;
            var end = cursor;
            var bytes = 0;
            while (end < lineEnd)
            {
                var rune = Rune.GetRuneAt(text, end);
                if (bytes + rune.Utf8SequenceLength > MaxQueryUtf8Bytes) break;
                bytes += rune.Utf8SequenceLength;
                end += rune.Utf16SequenceLength;
            }
            if (end < lineEnd)
            {
                // Prefer a word boundary, but always make progress for a long URL or unbroken word.
                var boundary = end;
                while (boundary > cursor && !char.IsWhiteSpace(text[boundary - 1])) boundary--;
                if (boundary > cursor) end = boundary;
            }
            while (end > cursor && char.IsWhiteSpace(text[end - 1])) end--;
            segments.Add(new Segment(text[cursor..end], true));
            cursor = end;
        }
        return segments;
    }
}

internal enum SummaryTranslationFailure { TooLong, QuotaExceeded, Timeout, Unavailable, InvalidResponse }

internal sealed class SummaryTranslationException : Exception
{
    public SummaryTranslationFailure Failure { get; }
    public SummaryTranslationException(SummaryTranslationFailure failure) : base(failure.ToString()) => Failure = failure;
}
