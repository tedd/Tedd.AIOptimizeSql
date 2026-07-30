using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Turns a query in the editor into a complete, runnable experiment. Deterministic work
/// (dependency discovery, output-fingerprint SQL, sandbox scaffolding) happens without AI;
/// the AI fills in judgement calls — naming, what to measure, instructions, and the parts of
/// a sandbox script only a human-or-model reading of the schema can get right.
/// </summary>
public interface IExperimentBlueprintService
{
    /// <summary>
    /// Step 1: resolve what the query touches. Runs deterministic schema discovery and returns
    /// a blueprint pre-filled with the benchmark SQL, base tables, and a schema context summary.
    /// No AI, no writes.
    /// </summary>
    Task<(ExperimentBlueprint Blueprint, string SchemaContextMarkdown, ObjectDependencyGraph Graph)>
        AnalyzeQueryAsync(string connectionString, string benchmarkSql, CancellationToken ct = default);

    /// <summary>
    /// Step 2: ask the AI to complete the blueprint — name, description, instructions,
    /// measurement plan, isolation and verification recommendations, and the sandbox scripts
    /// for the chosen isolation mode. Falls back to the deterministic scaffolding when the AI
    /// is unavailable or returns something unusable, recording that on
    /// <see cref="ExperimentBlueprint.Warnings"/>.
    /// </summary>
    Task<ExperimentBlueprint> CompleteWithAiAsync(
        AIConnection aiConnection,
        string connectionString,
        ExperimentBlueprintRequest request,
        ExperimentBlueprint current,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Builds the output-fingerprint script for <paramref name="benchmarkSql"/> deterministically.
    /// When the benchmark is a single SELECT, its rows are materialized into a temp table, each
    /// row is hashed, and the hashes are aggregated per <paramref name="mode"/>. Otherwise — a
    /// stored procedure call or anything else with side effects and no result set of its own —
    /// <paramref name="affectedTables"/> (from <see cref="ExperimentBlueprint.BaseTables"/>) is
    /// hashed instead: the CURRENT CONTENTS of every table listed, combined into one value, so a
    /// procedure with no return rows can still be proven to leave the same data behind. Returns
    /// null for <see cref="OutputVerificationMode.None"/>.
    /// </summary>
    string? BuildOutputVerificationSql(
        string benchmarkSql,
        OutputVerificationMode mode,
        IReadOnlyList<(string Schema, string Table)>? affectedTables = null);

    /// <summary>
    /// Runs the verification script twice against the unmodified database and reports whether
    /// it produced the same value both times. A script that is not stable on its own can never
    /// prove an optimization safe, so the wizard blocks on this before creating the experiment.
    /// </summary>
    Task<(bool Deterministic, string? FirstHash, string? SecondHash, string? Error)>
        TestOutputVerificationAsync(
            string connectionString, string verificationSql, CancellationToken ct = default);

    /// <summary>
    /// Dry-runs the sandbox setup and teardown scripts, reporting what failed. Never leaves the
    /// sandbox behind: teardown runs even when setup fails partway.
    /// </summary>
    Task<(bool Ok, string Log, string? Error)> ValidateSandboxAsync(
        string connectionString,
        ExperimentIsolationMode mode,
        string? setupSql,
        string? teardownSql,
        string? sandboxDatabaseName,
        CancellationToken ct = default);
}
