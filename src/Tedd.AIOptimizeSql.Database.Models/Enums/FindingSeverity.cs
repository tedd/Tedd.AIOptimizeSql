namespace Tedd.AIOptimizeSql.Database.Models.Enums;

/// <summary>
/// Severity of an <see cref="AnalysisFinding"/>. <see cref="Good"/> marks
/// positive findings (things that are configured well) so the UI can show
/// good news alongside problems.
/// </summary>
public enum FindingSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info,
    Good
}
