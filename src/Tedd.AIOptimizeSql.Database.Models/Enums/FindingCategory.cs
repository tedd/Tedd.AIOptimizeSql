namespace Tedd.AIOptimizeSql.Database.Models.Enums;

public enum FindingCategory
{
    MissingIndex,
    IndexFragmentation,
    UnusedIndex,
    DuplicateIndex,
    OutdatedStatistics,
    MissingStatistics,
    ExpensiveQuery,
    StoredProcedure,
    View,
    Configuration,
    WaitStatistics,
    TempDb,
    Schema,
    Storage,
    Concurrency,
    Other
}
