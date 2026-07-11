using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Web research tools exposed to AI agents via <c>AIFunctionFactory.Create</c>:
/// web search (Brave Search API) and page fetching with HTML-to-text extraction.
/// Lets the AI look up SQL Server documentation, known issues, and optimization
/// techniques while analyzing a database.
/// </summary>
public sealed partial class WebSearchToolWrapper : IDisposable
{
    private readonly HttpClient _http;
    private readonly WebSearchSettings _settings;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;

    public WebSearchToolWrapper(WebSearchSettings settings, ILogger logger, HttpClient? httpClient = null)
    {
        _settings = settings;
        _logger = logger;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds));
    }

    [Description("Searches the web and returns the top results (title, URL, snippet). Use this to research SQL Server optimization techniques, error messages, DMV documentation, or version-specific behavior.")]
    public async Task<string> WebSearch(
        [Description("The search query, e.g. 'SQL Server 2022 parameter sensitive plan optimization'")] string query)
    {
        _logger.LogDebug("AI tool: WebSearch called with: {Query}", query);

        if (!_settings.IsConfigured)
            return "ERROR: Web search is not configured. Set OptimizeEngine:WebSearch:ApiKey to enable it.";

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_settings.Endpoint}?q={Uri.EscapeDataString(query)}&count={Math.Clamp(_settings.MaxResults, 1, 20)}");
            request.Headers.Add("X-Subscription-Token", _settings.ApiKey);
            request.Headers.Add("Accept", "application/json");

            using var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Web search failed: HTTP {Status}", (int)response.StatusCode);
                return $"ERROR: search request failed with HTTP {(int)response.StatusCode}.";
            }

            var results = ParseBraveResults(json, _settings.MaxResults);
            if (results.Count == 0)
                return "(no search results)";

            var sb = new StringBuilder();
            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.AppendLine($"{i + 1}. {r.Title}");
                sb.AppendLine($"   URL: {r.Url}");
                if (!string.IsNullOrWhiteSpace(r.Description))
                    sb.AppendLine($"   {r.Description}");
                sb.AppendLine();
            }
            sb.AppendLine("Use FetchWebPage to read the full content of a result.");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: WebSearch failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Fetches a web page and returns its readable text content (HTML stripped). Use after WebSearch to read documentation or articles.")]
    public async Task<string> FetchWebPage(
        [Description("The absolute http(s) URL to fetch")] string url)
    {
        _logger.LogDebug("AI tool: FetchWebPage called with: {Url}", url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "ERROR: only absolute http(s) URLs are supported.";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.8");
            request.Headers.Add("User-Agent", "Tedd.AIOptimizeSql/1.0 (SQL analysis research agent)");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return $"ERROR: fetch failed with HTTP {(int)response.StatusCode}.";

            var html = await response.Content.ReadAsStringAsync();
            var text = HtmlToText(html);

            if (Encoding.UTF8.GetByteCount(text) > _settings.MaxPageBytes)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                text = Encoding.UTF8.GetString(bytes, 0, _settings.MaxPageBytes) +
                       $"\n... truncated at {_settings.MaxPageBytes} bytes ...";
            }

            return string.IsNullOrWhiteSpace(text) ? "(page contained no extractable text)" : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: FetchWebPage failed");
            return $"ERROR: {ex.Message}";
        }
    }

    /// <summary>One parsed web search hit.</summary>
    internal sealed record SearchHit(string Title, string Url, string? Description);

    /// <summary>
    /// Parses a Brave Search API response (<c>web.results[]</c> with
    /// <c>title</c>/<c>url</c>/<c>description</c> fields).
    /// </summary>
    internal static List<SearchHit> ParseBraveResults(string json, int maxResults)
    {
        var hits = new List<SearchHit>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("web", out var web) ||
                !web.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return hits;

            foreach (var item in results.EnumerateArray())
            {
                if (hits.Count >= maxResults)
                    break;

                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                hits.Add(new SearchHit(
                    HtmlToText(title ?? url),
                    url,
                    description is null ? null : HtmlToText(description)));
            }
        }
        catch (JsonException)
        {
            // Malformed response — return what we have (likely empty).
        }

        return hits;
    }

    /// <summary>
    /// Very small HTML-to-text conversion: drops script/style/head blocks,
    /// converts structural tags to line breaks, strips remaining tags, and
    /// decodes common entities. Good enough for AI consumption.
    /// </summary>
    internal static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
            return "";

        var text = ScriptStyleBlockPattern().Replace(html, " ");
        text = BlockLevelTagPattern().Replace(text, "\n");
        text = AnyTagPattern().Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = HorizontalWhitespacePattern().Replace(text, " ");
        text = BlankLinesPattern().Replace(text, "\n\n");
        return text.Trim();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    [GeneratedRegex(@"<(script|style|head|noscript|svg|iframe)\b[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleBlockPattern();

    [GeneratedRegex(@"</?(p|div|br|li|ul|ol|h[1-6]|tr|table|section|article|header|footer|blockquote|pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockLevelTagPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagPattern();

    [GeneratedRegex(@"[ \t\r\f]+")]
    private static partial Regex HorizontalWhitespacePattern();

    [GeneratedRegex(@"\n\s*\n\s*(\s*\n)+")]
    private static partial Regex BlankLinesPattern();
}
