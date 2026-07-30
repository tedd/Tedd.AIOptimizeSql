namespace Tedd.AIOptimizeSql.Database.Models.Enums;

/// <summary>
/// How much isolation an experiment gets before hypotheses are applied and benchmarked.
/// Every mode still reverts each hypothesis; isolation decides what "the database"
/// means while the experiment runs.
/// </summary>
public enum ExperimentIsolationMode
{
    /// <summary>
    /// Measure only. Hypotheses are applied to, benchmarked against, and reverted from
    /// the target database as-is. Nothing is provisioned or torn down.
    /// </summary>
    None,

    /// <summary>
    /// Copy the objects the benchmark touches into a dedicated sandbox schema in the same
    /// database (tables with their indexes/constraints and data, views/procedures rewritten
    /// to point at the sandbox copies), run the experiment there, then drop the schema.
    /// </summary>
    SandboxSchema,

    /// <summary>
    /// Provision a separate database on the same server, populate it from the source
    /// database, run the whole experiment against it, then drop it.
    /// </summary>
    CloneDatabase
}
