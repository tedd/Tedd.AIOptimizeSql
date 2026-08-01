using System.ComponentModel.DataAnnotations;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum DatabaseConnectionId { }
public record DatabaseConnection
{
    [Key]
    public DatabaseConnectionId Id { get; set; }

    [Required, MaxLength(512)]
    public required string Name { get; set; }

    [Required, MaxLength(4000)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Production-safe mode: when true, nothing on this connection may modify the
    /// target database. AI tools are restricted to read-only statements (SELECT,
    /// estimated plans, DMV queries), hypothesis apply/benchmark/revert is skipped,
    /// and views/stored procedures/data are never touched.
    /// </summary>
    public bool AnalyzeOnly { get; set; }

    /// <summary>
    /// AI connection this database works with. Everything started from inside the
    /// database — analyses, experiments, the wizard — inherits it, so an AI has to
    /// exist before a database can be added. The AI cannot be deleted while a database
    /// still points at it; unbind it here first.
    /// </summary>
    public AIConnectionId? AIConnectionId { get; set; }
    public AIConnection? AIConnection { get; set; }

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// Last modified UTC
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}