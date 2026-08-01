using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Tolerant JSON extraction for agent replies. Models wrap objects in prose or code fences even
/// when told not to, so strip the fence and take the outermost balanced object before parsing.
/// </summary>
internal static partial class AiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Returns the JSON object embedded in <paramref name="rawResponse"/>, or null.</summary>
    public static string? ExtractObject(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return null;

        var text = rawResponse.Trim();
        var fence = CodeFencePattern().Match(text);
        if (fence.Success)
            text = fence.Groups[1].Value.Trim();

        var start = text.IndexOf('{');
        if (start < 0)
            return null;

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

    [GeneratedRegex(@"^```(?:json|sql)?\s*\n([\s\S]*?)\n\s*```\s*$", RegexOptions.Compiled)]
    private static partial Regex CodeFencePattern();
}
