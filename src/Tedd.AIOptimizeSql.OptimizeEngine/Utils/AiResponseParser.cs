using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Parses structured JSON responses from the AI agent.
/// Handles markdown code fence stripping and JSON extraction.
/// </summary>
internal static partial class AiResponseParser
{
    public static AiHypothesisResponse? ParseHypothesisResponse(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return null;

        var json = StripMarkdownFences(rawResponse);
        json = ExtractJsonObject(json);

        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var result = JsonSerializer.Deserialize<AiHypothesisResponse>(json, _jsonOptions);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripMarkdownFences(string text)
    {
        text = text.Trim();

        // Remove ```json ... ``` or ``` ... ```
        var match = CodeFencePattern().Match(text);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        return text;
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;

            if (depth == 0)
                return text[start..(i + 1)];
        }

        return null;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [GeneratedRegex(@"^```(?:json)?\s*\n([\s\S]*?)\n\s*```\s*$", RegexOptions.Compiled)]
    private static partial Regex CodeFencePattern();
}

internal sealed class AiHypothesisResponse
{
    public string? Description { get; set; }
    public string? Optimize_sql { get; set; }
    public string? Revert_sql { get; set; }
}
