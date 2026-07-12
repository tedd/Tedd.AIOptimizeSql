using System.ComponentModel;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Tools that let the analysis AI agent persist its findings and propose
/// follow-up experiments. Writes go to the application's own database, never
/// to the analyzed target database.
/// </summary>
public sealed class AnalysisFindingToolWrapper(
    DatabaseAnalysisId analysisId,
    DatabaseConnectionId? databaseConnectionId,
    AIConnectionId? aiConnectionId,
    IServiceScopeFactory scopeFactory,
    ILogger logger)
{
    [Description("Records a finding from your analysis: a problem, an optimization opportunity, or a positive observation. Call this once per distinct finding. Severity must be one of: Critical, High, Medium, Low, Info, Good (use Good for things that are configured well). Category must be one of: MissingIndex, IndexFragmentation, UnusedIndex, DuplicateIndex, OutdatedStatistics, MissingStatistics, ExpensiveQuery, StoredProcedure, View, Configuration, WaitStatistics, TempDb, Schema, Storage, Concurrency, Other.")]
    public async Task<string> ReportFinding(
        [Description("Category, e.g. MissingIndex, ExpensiveQuery, StoredProcedure, View, Configuration")] string category,
        [Description("Severity: Critical, High, Medium, Low, Info, or Good")] string severity,
        [Description("Short one-line title of the finding")] string title,
        [Description("What you found and why it matters (markdown)")] string description,
        [Description("Supporting evidence: DMV output, plan fragments, measurements (markdown, optional)")] string? evidence = null,
        [Description("Recommended remediation in prose (markdown, optional)")] string? recommendation = null,
        [Description("Recommended remediation as runnable T-SQL (optional; it will NOT be executed, only stored for the user)")] string? recommendationSql = null,
        [Description("Schema of the primary affected object (optional)")] string? objectSchema = null,
        [Description("Name of the primary affected object: table, index, procedure, or view (optional)")] string? objectName = null,
        [Description("Relative impact estimate, larger = more impactful (optional)")] double impactScore = 0)
    {
        logger.LogDebug("AI tool: ReportFinding '{Title}' ({Category}/{Severity})", title, category, severity);

        if (string.IsNullOrWhiteSpace(title))
            return "ERROR: title is required.";

        if (!Enum.TryParse<FindingCategory>(category?.Trim(), ignoreCase: true, out var parsedCategory))
            parsedCategory = FindingCategory.Other;
        if (!Enum.TryParse<FindingSeverity>(severity?.Trim(), ignoreCase: true, out var parsedSeverity))
            parsedSeverity = FindingSeverity.Info;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;

            var finding = new AnalysisFinding
            {
                Id = AnalysisFindingId.Transient,
                DatabaseAnalysisId = analysisId,
                Category = parsedCategory,
                Severity = parsedSeverity,
                Title = title.Length > 1024 ? title[..1024] : title,
                Description = description,
                Evidence = evidence,
                Recommendation = recommendation,
                RecommendationSql = recommendationSql,
                ObjectSchema = Clip(objectSchema, 128),
                ObjectName = Clip(objectName, 256),
                ImpactScore = impactScore,
                Source = "AI",
                CreatedAt = now,
                ModifiedAt = now,
            };
            db.AnalysisFindings.Add(finding);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await TouchAnalysisAsync(db, now);

            return $"Finding recorded with id {(int)(object)finding.Id} ({parsedSeverity}/{parsedCategory}).";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI tool: ReportFinding failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Creates a new optimization experiment from your analysis so the user can benchmark a hypothesis with the full apply/benchmark/revert cycle on a non-production copy. Use this when a finding deserves measured verification (e.g. a promising index or query rewrite). The experiment is created in this application, not on the analyzed database. The experiment is later run by a separate optimization AI that sees ONLY: the description, instructions, benchmarkSql, and the full content of the finding linked via relatedFindingId. It cannot look up your other findings or this analysis, so write description and instructions to be self-contained: never reference finding numbers or analysis results without restating the relevant details (object names, candidate index DDL, parameter values, measurements) inline.")]
    public async Task<string> ProposeExperiment(
        [Description("Short experiment name")] string name,
        [Description("Human-readable description of what the experiment tests and why. Shown to the optimization AI running the experiment — must stand alone without access to your analysis")] string description,
        [Description("The SQL workload to benchmark (the query/procedure call whose performance should be measured)")] string benchmarkSql,
        [Description("Instructions for the optimization AI: which optimization directions to explore, with concrete details (candidate DDL, object names, representative parameter values) restated inline (optional)")] string? instructions = null,
        [Description("Id of a previously reported finding this experiment verifies. Strongly recommended: the linked finding's full content (description, evidence, recommendation, SQL) is automatically shown to the optimization AI (optional)")] int? relatedFindingId = null)
    {
        logger.LogDebug("AI tool: ProposeExperiment '{Name}'", name);

        if (string.IsNullOrWhiteSpace(name))
            return "ERROR: name is required.";
        if (string.IsNullOrWhiteSpace(benchmarkSql))
            return "ERROR: benchmarkSql is required.";

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;

            var experiment = new Experiment
            {
                Name = name.Length > 1024 ? name[..1024] : name,
                Description = $"{description}\n\n(Proposed by database analysis #{(int)(object)analysisId})",
                Instructions = instructions,
                BenchmarkSql = benchmarkSql,
                DatabaseConnectionId = databaseConnectionId,
                AIConnectionId = aiConnectionId,
                CreatedAt = now,
                ModifiedAt = now,
            };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            if (relatedFindingId is int findingIdInt)
            {
                var findingId = (AnalysisFindingId)findingIdInt;
                await db.AnalysisFindings
                    .Where(f => f.Id == findingId && f.DatabaseAnalysisId == analysisId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.ProposedExperimentId, experiment.Id)
                        .SetProperty(f => f.ModifiedAt, now));
            }

            await TouchAnalysisAsync(db, now);

            return $"Experiment '{experiment.Name}' created with id {(int)(object)experiment.Id}. " +
                   "The user can run research iterations on it to benchmark hypotheses.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI tool: ProposeExperiment failed");
            return $"ERROR: {ex.Message}";
        }
    }

    private async Task TouchAnalysisAsync(AIOptimizeDbContext db, DateTime now)
    {
        await db.DatabaseAnalyses
            .Where(a => a.Id == analysisId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ModifiedAt, now));
    }

    private static string? Clip(string? value, int max) =>
        value is null ? null : (value.Length > max ? value[..max] : value);
}
