namespace Tedd.AIOptimizeSql.Database.Models.Enums;

/// <summary>
/// How the benchmark query's <em>output</em> is proven unchanged by an optimization.
/// A baseline fingerprint is taken before the experiment starts; every hypothesis is
/// fingerprinted again after its benchmark and compared against that baseline.
/// </summary>
public enum OutputVerificationMode
{
    /// <summary>No output comparison. Only speed is measured.</summary>
    None,

    /// <summary>
    /// Order-insensitive: each row is hashed, and the row hashes are combined with a
    /// commutative aggregate. Detects changed values, added/removed/duplicated rows,
    /// but tolerates a different row order. The right default for a query without
    /// a meaningful ORDER BY.
    /// </summary>
    UnorderedHash,

    /// <summary>
    /// Order-sensitive: rows are hashed together with their position, so a different
    /// row order is a mismatch. Use when the query has an ORDER BY that callers rely on.
    /// </summary>
    OrderedHash
}
