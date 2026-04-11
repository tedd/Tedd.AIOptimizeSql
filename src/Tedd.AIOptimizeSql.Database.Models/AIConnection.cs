using System.ComponentModel.DataAnnotations;

using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum AIConnectionId { }
public record AIConnection
{
    [Key]
    public AIConnectionId Id { get; set; }

    [Required, MaxLength(512)]
    public required string Name { get; set; }
    [Required, MaxLength(1024)]
    public required AiProvider Provider { get; set; }

    /// <summary>
    /// Model identity
    /// </summary>
    [Required, MaxLength(128)]
    public required string Model { get; set; }

    [Required, MaxLength(512)]
    public required string Endpoint { get; set; }

    [Required, MaxLength(1024)]
    public required string ApiKey { get; set; }


    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// Last modified UTC
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}