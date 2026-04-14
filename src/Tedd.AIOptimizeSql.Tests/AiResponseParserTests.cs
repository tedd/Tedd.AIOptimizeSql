using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.Tests;

public class AiResponseParserTests
{
    [Fact]
    public void ParseHypothesisResponse_parses_plain_json()
    {
        var result = AiResponseParser.ParseHypothesisResponse("""
            {
              "description": "Add index",
              "optimize_sql": "CREATE INDEX IX_A ON dbo.T (A);",
              "revert_sql": "DROP INDEX IX_A ON dbo.T;"
            }
            """);

        Assert.NotNull(result);
        Assert.Equal("Add index", result.Description);
        Assert.Equal("CREATE INDEX IX_A ON dbo.T (A);", result.Optimize_sql);
        Assert.Equal("DROP INDEX IX_A ON dbo.T;", result.Revert_sql);
    }

    [Fact]
    public void ParseHypothesisResponse_parses_json_wrapped_in_markdown_fence()
    {
        var result = AiResponseParser.ParseHypothesisResponse("""
            ```json
            {
              "description": "Add filtered index",
              "optimize_sql": "CREATE INDEX IX_Filtered ON dbo.T (A) WHERE B = 1;",
              "revert_sql": "DROP INDEX IX_Filtered ON dbo.T;"
            }
            ```
            """);

        Assert.NotNull(result);
        Assert.Equal("Add filtered index", result.Description);
        Assert.Equal("CREATE INDEX IX_Filtered ON dbo.T (A) WHERE B = 1;", result.Optimize_sql);
        Assert.Equal("DROP INDEX IX_Filtered ON dbo.T;", result.Revert_sql);
    }

    [Fact]
    public void ParseHypothesisResponse_extracts_json_from_surrounding_text_and_ignores_casing()
    {
        var result = AiResponseParser.ParseHypothesisResponse("""
            Here is the corrected script:
            {
              "Description": "Fix revert",
              "Optimize_Sql": "",
              "Revert_Sql": "DROP INDEX IX_A ON dbo.T;",
            }
            Thanks.
            """);

        Assert.NotNull(result);
        Assert.Equal("Fix revert", result.Description);
        Assert.Equal(string.Empty, result.Optimize_sql);
        Assert.Equal("DROP INDEX IX_A ON dbo.T;", result.Revert_sql);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    public void ParseHypothesisResponse_returns_null_when_json_cannot_be_parsed(string? rawResponse)
    {
        var result = AiResponseParser.ParseHypothesisResponse(rawResponse);

        Assert.Null(result);
    }
}
