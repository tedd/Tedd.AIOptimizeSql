namespace Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

/// <summary>Which way an edge runs relative to the graph's root.</summary>
public enum DependencyDirection
{
    /// <summary>Something depends on the root — the root is referenced (callers, parents).</summary>
    Incoming,

    /// <summary>The root depends on something — the root references it (tables, callees).</summary>
    Outgoing
}

/// <summary>A node in a dependency graph.</summary>
public sealed record DependencyNode
{
    /// <summary>Stable key, <c>SCHEMA.NAME</c> upper-cased. Used for edge endpoints and de-duplication.</summary>
    public required string Key { get; init; }

    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required SqlObjectKind Kind { get; init; }

    /// <summary>True for the object the graph was built around.</summary>
    public bool IsRoot { get; init; }

    /// <summary>
    /// Hops from the root: 0 for the root, negative toward callers (incoming),
    /// positive toward dependencies (outgoing). Drives the graph's column layout.
    /// </summary>
    public int Level { get; init; }

    /// <summary>Set when the object could not be inspected (cross-database, encrypted, missing).</summary>
    public string? Note { get; init; }
}

/// <summary>A directed edge between two <see cref="DependencyNode"/>s.</summary>
public sealed record DependencyLink
{
    /// <summary>The dependent object's <see cref="DependencyNode.Key"/>.</summary>
    public required string FromKey { get; init; }

    /// <summary>The depended-upon object's <see cref="DependencyNode.Key"/>.</summary>
    public required string ToKey { get; init; }

    public required DependencyDirection Direction { get; init; }

    /// <summary>Column-level reference when the catalog recorded one.</summary>
    public string? ColumnName { get; init; }

    public bool IsSchemabound { get; init; }
}

/// <summary>
/// Incoming and outgoing dependencies around one root object (or around every object a
/// query text touches, in which case there are several roots).
/// </summary>
public sealed record ObjectDependencyGraph
{
    public required IReadOnlyList<DependencyNode> Nodes { get; init; }
    public required IReadOnlyList<DependencyLink> Links { get; init; }

    /// <summary>Keys of the objects the graph was built around.</summary>
    public required IReadOnlyList<string> RootKeys { get; init; }

    /// <summary>Anything the traversal could not follow — reported to the user, not swallowed.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Base tables reachable from the roots, for checksum registration and sandbox provisioning.</summary>
    public IReadOnlyList<(string Schema, string Table)> BaseTables { get; init; } = [];

    public static ObjectDependencyGraph Empty { get; } = new()
    {
        Nodes = [],
        Links = [],
        RootKeys = []
    };
}
