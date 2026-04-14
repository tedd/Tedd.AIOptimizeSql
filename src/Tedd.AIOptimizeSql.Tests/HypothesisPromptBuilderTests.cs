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

    private static ResearchIteration Iteration(string? hints = null, int maxHypotheses = 10) =>
        new() { Hints = hints, MaxNumberOfHypotheses = maxHypotheses };

    [Fact]
    public void BuildInstructions_always_includes_core_sections()
    {
        var text = HypothesisPromptBuilder.BuildInstructions(Experiment(), Iteration(), []);

        Assert.Contains("You are a MSSQL performance optimization expert.", text, StringComparison.Ordinal);
        Assert.Contains("## Important Constraints", text, StringComparison.Ordinal);
        Assert.Contains("## Allowed Optimization Categories", text, StringComparison.Ordinal);
        Assert.Contains("1. Query shape and relational rewrite", text, StringComparison.Ordinal);
        Assert.Contains("16. Observability, regression detection, and validation", text, StringComparison.Ordinal);
        Assert.Contains("## Required Response Format", text, StringComparison.Ordinal);
        Assert.Contains("\"optimize_sql\"", text, StringComparison.Ordinal);
        Assert.Contains("\"revert_sql\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Experiment-specific instructions", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Benchmark SQL (the query to optimise)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstructions_includes_experiment_sections_when_present()
    {
        var text = HypothesisPromptBuilder.BuildInstructions(
            Experiment(instructions: "Do X", benchmarkSql: "SELECT 1"),
            Iteration(),
            [],
            schemaDiscoveryMarkdown: "# Schema",
            baselinePerformanceSummary: "CPU 50ms");

        Assert.Contains("## Experiment-specific instructions", text, StringComparison.Ordinal);
        Assert.Contains("Do X", text, StringComparison.Ordinal);
        Assert.Contains("## Benchmark SQL (the query to optimise)", text, StringComparison.Ordinal);
        Assert.Contains("SELECT 1", text, StringComparison.Ordinal);
        Assert.Contains("## Schema Information (discovered from database catalog)", text, StringComparison.Ordinal);
        Assert.Contains("# Schema", text, StringComparison.Ordinal);
        Assert.Contains("## Baseline Performance (before any optimisation)", text, StringComparison.Ordinal);
        Assert.Contains("CPU 50ms", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstructions_omits_whitespace_only_optional_sections()
    {
        var text = HypothesisPromptBuilder.BuildInstructions(
            Experiment(instructions: "   ", benchmarkSql: "\t"),
            Iteration(),
            [],
            schemaDiscoveryMarkdown: " \t ",
            baselinePerformanceSummary: "\r\n");

        Assert.DoesNotContain("## Experiment-specific instructions", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Benchmark SQL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Schema Information", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Baseline Performance", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_includes_hints_when_present()
    {
        var text = HypothesisPromptBuilder.BuildPrompt(Iteration(hints: "Focus on indexes"), []);

        Assert.Contains("## Additional hints for this research iteration", text, StringComparison.Ordinal);
        Assert.Contains("Focus on indexes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_lists_prior_hypotheses_with_outcomes_sql_and_errors()
    {
        var iteration = Iteration(maxHypotheses: 4);
        var priors = new List<Hypothesis>
        {
            new()
            {
                ResearchIterationId = default,
                Status = HypothesisState.Completed,
                Description = "Add index A",
                OptimizeSql = "CREATE INDEX IX_A ON dbo.T (A);",
                ImpovementPercentage = 12.5f,
            },
            new()
            {
                ResearchIterationId = default,
                Status = HypothesisState.Completed,
                Description = "Rewrite join",
                OptimizeSql = "SELECT 1;",
                ImpovementPercentage = -6f,
            },
            new()
            {
                ResearchIterationId = default,
                Status = HypothesisState.Failed,
                ErrorMessage = "boom",
            },
        };

        var text = HypothesisPromptBuilder.BuildPrompt(iteration, priors);

        Assert.Contains("## Previous attempts in this iteration", text, StringComparison.Ordinal);
        Assert.Contains("### Attempt 1: GOOD", text, StringComparison.Ordinal);
        Assert.Contains("- **Improvement**: +12.5%", text, StringComparison.Ordinal);
        Assert.Contains("- **Status**: Completed", text, StringComparison.Ordinal);
        Assert.Contains("- **Description**: Add index A", text, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IX_A ON dbo.T (A);", text, StringComparison.Ordinal);
        Assert.Contains("### Attempt 2: BAD", text, StringComparison.Ordinal);
        Assert.Contains("- **Improvement**: -6%", text, StringComparison.Ordinal);
        Assert.Contains("- **Description**: Rewrite join", text, StringComparison.Ordinal);
        Assert.Contains("### Attempt 3: FAILED", text, StringComparison.Ordinal);
        Assert.Contains("- **Error**: boom", text, StringComparison.Ordinal);
        Assert.Contains("This is attempt 4 of 4.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_without_priors_includes_attempt_number_and_json_instruction()
    {
        var text = HypothesisPromptBuilder.BuildPrompt(Iteration(), []);

        Assert.DoesNotContain("## Previous attempts in this iteration", text, StringComparison.Ordinal);
        Assert.Contains("This is attempt 1 of 10.", text, StringComparison.Ordinal);
        Assert.Contains("Analyse the database using the available tools, then propose your optimisation as a JSON response matching the required format.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_omits_description_line_when_missing()
    {
        var priors = new List<Hypothesis>
        {
            new()
            {
                ResearchIterationId = default,
                Status = HypothesisState.Completed,
                Description = null,
                ImpovementPercentage = 0,
            },
        };

        var text = HypothesisPromptBuilder.BuildPrompt(Iteration(), priors);

        Assert.Contains("### Attempt 1: NO SIGNIFICANT CHANGE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- **Description**:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFixPrompt_includes_original_optimization_when_fixing_revert()
    {
        var text = HypothesisPromptBuilder.BuildFixPrompt(
            "DROP INDEX IX_A ON dbo.T;",
            "Cannot drop the index because it does not exist.",
            isRevert: true,
            originalOptimizeSql: "CREATE INDEX IX_A ON dbo.T (A);");

        Assert.Contains("The following revert SQL script failed with an error. Please fix it.", text, StringComparison.Ordinal);
        Assert.Contains("## Failed SQL Script", text, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX IX_A ON dbo.T;", text, StringComparison.Ordinal);
        Assert.Contains("## Original Optimisation SQL (this is what the revert needs to undo)", text, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IX_A ON dbo.T (A);", text, StringComparison.Ordinal);
        Assert.Contains("- The revert must fully undo all optimisation changes.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCombinedPrompt_includes_results_and_required_json()
    {
        var text = HypothesisPromptBuilder.BuildCombinedPrompt(
            [
                new Hypothesis
                {
                    ResearchIterationId = default,
                    Status = HypothesisState.Completed,
                    Description = "Add covering index",
                    OptimizeSql = "CREATE INDEX IX_A ON dbo.T (A) INCLUDE (B);",
                    RevertSql = "DROP INDEX IX_A ON dbo.T;",
                    ImpovementPercentage = 12.5f,
                },
                new Hypothesis
                {
                    ResearchIterationId = default,
                    Status = HypothesisState.Failed,
                    Description = "Force plan",
                    OptimizeSql = "EXEC sp_query_store_force_plan ...;",
                    RevertSql = "EXEC sp_query_store_unforce_plan ...;",
                    ImpovementPercentage = -2f,
                }
            ],
            schemaDiscoveryMarkdown: "# Schema",
            baselinePerformanceSummary: "CPU 50ms");

        Assert.Contains("Create one ULTIMATE optimization script", text, StringComparison.Ordinal);
        Assert.Contains("## Schema Information", text, StringComparison.Ordinal);
        Assert.Contains("# Schema", text, StringComparison.Ordinal);
        Assert.Contains("## Baseline Performance", text, StringComparison.Ordinal);
        Assert.Contains("CPU 50ms", text, StringComparison.Ordinal);
        Assert.Contains("## Previous Results", text, StringComparison.Ordinal);
        Assert.Contains("### GOOD (improvement: +12.5%)", text, StringComparison.Ordinal);
        Assert.Contains("**Description**: Add covering index", text, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX IX_A ON dbo.T;", text, StringComparison.Ordinal);
        Assert.Contains("### FAILED (improvement: -2%)", text, StringComparison.Ordinal);
        Assert.Contains("\"optimize_sql\"", text, StringComparison.Ordinal);
        Assert.Contains("\"revert_sql\"", text, StringComparison.Ordinal);
    }
}
