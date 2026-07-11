using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.Tests;

public class WebSearchToolWrapperTests
{
    [Fact]
    public void ParseBraveResults_ParsesWellFormedResponse()
    {
        const string json = """
            {
                "web": {
                    "results": [
                        { "title": "SQL Server Missing Indexes", "url": "https://learn.microsoft.com/a", "description": "About <strong>missing</strong> indexes" },
                        { "title": "Wait stats", "url": "https://example.com/waits", "description": "Wait types explained" }
                    ]
                }
            }
            """;

        var hits = WebSearchToolWrapper.ParseBraveResults(json, maxResults: 5);

        Assert.Equal(2, hits.Count);
        Assert.Equal("SQL Server Missing Indexes", hits[0].Title);
        Assert.Equal("https://learn.microsoft.com/a", hits[0].Url);
        Assert.Equal("About missing indexes", hits[0].Description); // HTML stripped
    }

    [Fact]
    public void ParseBraveResults_RespectsMaxResults()
    {
        const string json = """
            {
                "web": {
                    "results": [
                        { "title": "1", "url": "https://a" },
                        { "title": "2", "url": "https://b" },
                        { "title": "3", "url": "https://c" }
                    ]
                }
            }
            """;

        var hits = WebSearchToolWrapper.ParseBraveResults(json, maxResults: 2);
        Assert.Equal(2, hits.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "web": {} }""")]
    [InlineData("""{ "web": { "results": [] } }""")]
    [InlineData("not json at all")]
    public void ParseBraveResults_ToleratesMissingOrMalformedData(string json)
    {
        var hits = WebSearchToolWrapper.ParseBraveResults(json, maxResults: 5);
        Assert.Empty(hits);
    }

    [Fact]
    public void ParseBraveResults_SkipsEntriesWithoutUrl()
    {
        const string json = """
            {
                "web": {
                    "results": [
                        { "title": "no url" },
                        { "title": "ok", "url": "https://ok" }
                    ]
                }
            }
            """;

        var hits = WebSearchToolWrapper.ParseBraveResults(json, maxResults: 5);
        Assert.Single(hits);
        Assert.Equal("https://ok", hits[0].Url);
    }

    [Fact]
    public void HtmlToText_StripsMarkupAndDecodesEntities()
    {
        const string html = """
            <html><head><title>x</title><style>body { color: red; }</style></head>
            <body>
              <script>alert('DELETE');</script>
              <h1>Header</h1>
              <p>First &amp; second</p>
              <div>Third line</div>
            </body></html>
            """;

        var text = WebSearchToolWrapper.HtmlToText(html);

        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color: red", text);
        Assert.Contains("Header", text);
        Assert.Contains("First & second", text);
        Assert.Contains("Third line", text);
    }

    [Fact]
    public void HtmlToText_HandlesEmptyInput()
    {
        Assert.Equal("", WebSearchToolWrapper.HtmlToText(""));
    }
}
