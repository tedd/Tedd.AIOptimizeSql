using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Writes the sandbox setup and teardown scripts for an experiment so nobody has to hand-write
/// them. Generation is deterministic and reads the live catalog: tables are recreated with their
/// real physical design (keys, indexes, computed columns, defaults, collations), filled from the
/// source, and — for a clone database, where names do not change — the views, functions and
/// procedures the benchmark needs are copied across too. The AI pass is a refinement on top of
/// that, never the thing that makes the script exist.
/// </summary>
public interface ISandboxScriptService
{
    /// <summary>
    /// Generates the setup/teardown pair from catalog metadata alone. Read-only: nothing is
    /// created on the server, so this is safe on an analyze-only connection.
    /// </summary>
    Task<SandboxScripts> GenerateAsync(
        string connectionString, SandboxScriptRequest request, CancellationToken ct = default);

    /// <summary>
    /// Generates deterministically, then asks the AI to close the gaps the generator reported —
    /// chiefly repointing modules at a sandbox schema, which is a judgement call. Falls back to
    /// the deterministic scripts, with a warning, whenever the AI is unavailable or returns
    /// something unusable, so the caller always gets runnable scripts.
    /// </summary>
    Task<SandboxScripts> GenerateWithAiAsync(
        AIConnection aiConnection,
        string connectionString,
        SandboxScriptRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
