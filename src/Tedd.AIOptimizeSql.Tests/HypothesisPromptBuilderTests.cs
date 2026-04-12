using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.Tests;

public class HypothesisPromptBuilderTests
{
    private static Experiment Experiment(string? instructions = null, string? benchmarkSql = null) =>
        new()
        {
            Name = "Test experiment",
            Instructions = instructions,
            BenchmarkSql = benchmarkSql,
        };

    private static HypothesisBatch Batch(string? hints = null) =>
        new() { Hints = hints };

    [Fact]
    public void BuildInstructions_always_includes_guidelines()
    {
        var text = HypothesisPromptBuilder.BuildInstructions(Experiment(), Batch(), []);

        Assert.Contains("You are an expert SQL Server performance analyst", text, StringComparison.Ordinal);
        Assert.Contains("Guidelines:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("=== Experiment-specific instructions ===", text, StringComparison.Ordinal);
        Assert.DoesNotContain("=== Benchmark SQL", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstructions_includes_experiment_sections_when_present()
    {
        var text = HypothesisPromptBuilder.BuildInstructions(
            Experiment(instructions: "Do X", benchmarkSql: "SELECT 1"),
            Batch(),
            []);

        Assert.Contains("=== Experiment-specific instructions ===", text, StringComparison.Ordinal);
        Assert.Contains("Do X", text, StringComparison.Ordinal);
        Assert.Contains("=== Benchmark SQL (the query to optimise) ===", text, StringComparison.Ordinal);
        Assert.Contains("SELECT 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstructions_omits_whitespace_only_optional_sections()
    {
        var text = HypothesisPromptBuilder.BuildInstructions(
            Experiment(instructions: "   ", benchmarkSql: "\t"),
            Batch(),
            []);

        Assert.DoesNotContain("=== Experiment-specific instructions ===", text, StringComparison.Ordinal);
        Assert.DoesNotContain("=== Benchmark SQL", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_includes_hints_when_present()
    {
        var text = HypothesisPromptBuilder.BuildPrompt(Batch(hints: "Focus on indexes"), []);

        Assert.Contains("=== Additional hints for this batch ===", text, StringComparison.Ordinal);
        Assert.Contains("Focus on indexes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_lists_prior_hypotheses_with_improvement_format()
    {
        var batch = Batch();
        var priors = new List<Hypothesis>
        {
            new()
            {
                HypothesisBatchId = default,
                Description = "Add index A",
                ImpovementPercentage = 12.5f,
            },
            new()
            {
                HypothesisBatchId = default,
                Description = "Rewrite join",
                ImpovementPercentage = -3f,
            },
            new()
            {
                HypothesisBatchId = default,
                Description = "No change",
                ImpovementPercentage = 0f,
            },
        };

        var text = HypothesisPromptBuilder.BuildPrompt(batch, priors);

        Assert.Contains("=== Prior hypotheses in this batch", text, StringComparison.Ordinal);
        Assert.Contains("Hypothesis #1 (improvement: +12.5%):", text, StringComparison.Ordinal);
        Assert.Contains("Add index A", text, StringComparison.Ordinal);
        Assert.Contains("Hypothesis #2 (improvement: -3%):", text, StringComparison.Ordinal);
        Assert.Contains("Rewrite join", text, StringComparison.Ordinal);
        Assert.Contains("Hypothesis #3 (improvement: 0%):", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_without_priors_ends_with_proposal_instructions()
    {
        var text = HypothesisPromptBuilder.BuildPrompt(Batch(), []);

        Assert.DoesNotContain("Prior hypotheses", text, StringComparison.Ordinal);
        Assert.Contains("Please analyse the database and propose your next optimisation hypothesis.", text, StringComparison.Ordinal);
        Assert.Contains("Use the available tools", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_null_description_shows_placeholder()
    {
        var priors = new List<Hypothesis>
        {
            new() { HypothesisBatchId = default, Description = null, ImpovementPercentage = 0 },
        };

        var text = HypothesisPromptBuilder.BuildPrompt(Batch(), priors);

        Assert.Contains("(no description)", text, StringComparison.Ordinal);
    }
}
