using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.Tests;

public class AnalysisPromptBuilderTests
{
    private static DatabaseAnalysis NewAnalysis() => new()
    {
        Id = (DatabaseAnalysisId)7,
        Name = "Test analysis",
        MaxAiFindings = 12,
    };

    [Fact]
    public void BuildInstructions_AlwaysDeclaresReadOnlyMode()
    {
        var text = AnalysisPromptBuilder.BuildInstructions(NewAnalysis(), webSearchEnabled: false);

        Assert.Contains("READ-ONLY", text);
        Assert.Contains("ReportFinding", text);
        Assert.Contains("ProposeExperiment", text);
        Assert.Contains("12", text); // MaxAiFindings cap
        Assert.DoesNotContain("WebSearch /", text);
    }

    [Fact]
    public void BuildInstructions_MentionsWebToolsWhenEnabled()
    {
        var text = AnalysisPromptBuilder.BuildInstructions(NewAnalysis(), webSearchEnabled: true);
        Assert.Contains("WebSearch", text);
        Assert.Contains("FetchWebPage", text);
    }

    [Fact]
    public void BuildInstructions_IncludesUserInstructions()
    {
        var analysis = NewAnalysis();
        analysis.Instructions = "Focus on the Orders module.";

        var text = AnalysisPromptBuilder.BuildInstructions(analysis, webSearchEnabled: false);
        Assert.Contains("Focus on the Orders module.", text);
    }

    [Fact]
    public void BuildPrompt_ListsDeterministicFindingsToAvoidDuplication()
    {
        var analysis = NewAnalysis();
        var findings = new List<AnalysisFinding>
        {
            new()
            {
                DatabaseAnalysisId = analysis.Id,
                Category = FindingCategory.MissingIndex,
                Severity = FindingSeverity.High,
                Title = "Missing index on dbo.Orders",
            },
        };

        var prompt = AnalysisPromptBuilder.BuildPrompt(analysis, "## metrics here", findings);

        Assert.Contains("## metrics here", prompt);
        Assert.Contains("Missing index on dbo.Orders", prompt);
        Assert.Contains("do not duplicate", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
