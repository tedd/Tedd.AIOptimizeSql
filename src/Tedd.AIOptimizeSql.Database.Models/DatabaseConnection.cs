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
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// Last modified UTC
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}