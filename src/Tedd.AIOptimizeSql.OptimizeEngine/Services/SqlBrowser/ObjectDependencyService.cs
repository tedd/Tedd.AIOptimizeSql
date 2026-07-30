using System.Data.Common;
using System.Text;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Builds dependency graphs by breadth-first traversal of SQL Server catalog metadata.
/// Outgoing hops come from <c>sys.sql_expression_dependencies</c> (joined to <c>sys.objects</c>),
/// synonym base objects and foreign keys; incoming hops add triggers and the child side of
/// foreign keys. Read-only and deterministic -- no AI, no writes.
/// </summary>
public sealed class ObjectDependencyService(
    ISchemaDiscoveryService schemaDiscovery,
    ILogger<ObjectDependencyService> logger) : IObjectDependencyService
{
    // Shorter than SchemaDiscoveryService's 120s: these are small catalog reads driving an
    // interactive pane, so a hung query should surface quickly rather than freeze the UI.
    private const int CommandTimeout = 60;

    public async Task<ObjectDependencyGraph> GetGraphForObjectAsync(
        string connectionString,
        string schema,
        string name,
        int incomingDepth = 2,
        int outgoingDepth = 3,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var state = new GraphState();
        var root = await LoadObjectAsync(conn, schema, name, ct);

        if (root is null)
        {
            // A name the caller believed in but the catalog does not know: report it as a
            // placeholder root rather than handing back an empty graph with no explanation.
            var missing = new ObjectRef(
                MakeKey(schema, name), schema, name, TypeCode: "", ObjectId: 0,
                Note: "Object not found in sys.objects");
            state.AddNode(missing, level: 0, isRoot: true);
            state.AddWarning($"{Quote(schema, name)}: not found in sys.objects");
            return state.Build();
        }

        state.AddNode(root, level: 0, isRoot: true);
        await TraverseAsync(conn, state, [root], incomingDepth, outgoingDepth, ct);

        return LogAndReturn(state.Build(), rootCount: 1);
    }

    public async Task<ObjectDependencyGraph> GetGraphForSqlAsync(
        string connectionString,
        string sql,
        int incomingDepth = 1,
        int outgoingDepth = 3,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (string.IsNullOrWhiteSpace(sql))
            return ObjectDependencyGraph.Empty;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var state = new GraphState();
        var references = await schemaDiscovery.ResolveSqlReferencesAsync(sql, conn, ct);

        foreach (var unresolved in references.Where(r => !r.Resolved))
            state.AddWarning($"{unresolved.OriginalText}: could not be resolved to a catalog object");

        var roots = new List<ObjectRef>();
        foreach (var reference in references.Where(r => r.Resolved))
        {
            ct.ThrowIfCancellationRequested();

            var root = await LoadObjectAsync(conn, reference.Schema!, reference.Name!, ct);
            if (root is null)
            {
                state.AddWarning($"{Quote(reference.Schema!, reference.Name!)}: disappeared from sys.objects while building the graph");
                continue;
            }

            if (roots.Any(r => string.Equals(r.Key, root.Key, StringComparison.Ordinal)))
                continue;

            roots.Add(root);
            state.AddNode(root, level: 0, isRoot: true);
        }

        if (roots.Count == 0)
        {
            logger.LogInformation("No catalog objects resolved out of the supplied SQL; returning warnings only");
            return state.Build();
        }

        await TraverseAsync(conn, state, roots, incomingDepth, outgoingDepth, ct);

        return LogAndReturn(state.Build(), roots.Count);
    }

    private async Task TraverseAsync(
        SqlConnection conn,
        GraphState state,
        IReadOnlyList<ObjectRef> roots,
        int incomingDepth,
        int outgoingDepth,
        CancellationToken ct)
    {
        await TraverseOutgoingAsync(conn, state, roots, Math.Max(0, outgoingDepth), ct);
        await TraverseIncomingAsync(conn, state, roots, Math.Max(0, incomingDepth), ct);
    }

    private ObjectDependencyGraph LogAndReturn(ObjectDependencyGraph graph, int rootCount)
    {
        logger.LogInformation(
            "Dependency graph built from {Roots} root(s): {Nodes} nodes, {Links} links, {Tables} base tables, {Warnings} warnings",
            rootCount, graph.Nodes.Count, graph.Links.Count, graph.BaseTables.Count, graph.Warnings.Count);
        return graph;
    }

    #region Traversal

    /// <summary>
    /// Walks what the roots depend on. The visited set is per-direction, so a cycle
    /// (a procedure calling itself, or two views referencing each other) is expanded once
    /// and then terminates.
    /// </summary>
    private async Task TraverseOutgoingAsync(
        SqlConnection conn, GraphState state, IReadOnlyList<ObjectRef> roots, int maxDepth, CancellationToken ct)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(ObjectRef Object, int Level)>();
        foreach (var root in roots)
            queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (source, level) = queue.Dequeue();

            // Breadth-first, so the first dequeue of a key is always at its shallowest level.
            if (!expanded.Add(source.Key) || level >= maxDepth)
                continue;

            foreach (var edge in await LoadOutgoingEdgesAsync(conn, state, source, ct))
            {
                var target = edge.Target;
                state.AddNode(target, level + 1);
                state.AddLink(source.Key, target.Key, DependencyDirection.Outgoing, edge.ColumnName, edge.IsSchemabound);

                if (target.IsResolvable)
                    queue.Enqueue((target, level + 1));
            }
        }
    }

    /// <summary>Walks what depends on the roots. Levels run negative away from the roots.</summary>
    private async Task TraverseIncomingAsync(
        SqlConnection conn, GraphState state, IReadOnlyList<ObjectRef> roots, int maxDepth, CancellationToken ct)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(ObjectRef Object, int Level)>();
        foreach (var root in roots)
            queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (target, level) = queue.Dequeue();

            if (!expanded.Add(target.Key) || -level >= maxDepth)
                continue;

            foreach (var edge in await LoadIncomingEdgesAsync(conn, target, ct))
            {
                var dependent = edge.Target;
                state.AddNode(dependent, level - 1);
                state.AddLink(dependent.Key, target.Key, DependencyDirection.Incoming, edge.ColumnName, edge.IsSchemabound);

                if (dependent.IsResolvable)
                    queue.Enqueue((dependent, level - 1));
            }
        }
    }

    private async Task<List<RelatedEdge>> LoadOutgoingEdgesAsync(
        SqlConnection conn, GraphState state, ObjectRef source, CancellationToken ct)
    {
        // A synonym's base object is not recorded in sys.sql_expression_dependencies, and a
        // synonym has no other dependencies of its own, so this is the whole answer for one.
        if (string.Equals(source.TypeCode, "SN", StringComparison.OrdinalIgnoreCase))
            return await LoadSynonymBaseAsync(conn, state, source, ct);

        var edges = await LoadExpressionDependenciesAsync(conn, source.ObjectId, incoming: false, ct);
        edges.AddRange(await LoadUnresolvedDependenciesAsync(conn, state, source, ct));

        if (IsUserTable(source.TypeCode))
            edges.AddRange(await LoadForeignKeyEdgesAsync(conn, source.ObjectId, childSide: false, ct));

        return edges;
    }

    private async Task<List<RelatedEdge>> LoadIncomingEdgesAsync(
        SqlConnection conn, ObjectRef target, CancellationToken ct)
    {
        var edges = await LoadExpressionDependenciesAsync(conn, target.ObjectId, incoming: true, ct);

        if (IsUserTable(target.TypeCode))
        {
            edges.AddRange(await LoadTriggerEdgesAsync(conn, target.ObjectId, ct));
            edges.AddRange(await LoadForeignKeyEdgesAsync(conn, target.ObjectId, childSide: true, ct));
        }

        return edges;
    }

    #endregion

    #region Catalog reads

    private static async Task<ObjectRef?> LoadObjectAsync(
        SqlConnection conn, string schema, string name, CancellationToken ct)
    {
        const string sql = """
            SELECT
                o.object_id,
                ISNULL(SCHEMA_NAME(o.schema_id), 'dbo') AS object_schema,
                o.name AS object_name,
                o.type AS type_code,
                CASE WHEN m.object_id IS NOT NULL AND m.definition IS NULL THEN 1 ELSE 0 END AS is_encrypted
            FROM sys.objects o
            LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
            WHERE o.schema_id = SCHEMA_ID(@schema) AND o.name = @name
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name", name);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadObjectRef(reader, objectIdOrdinal: 0, schemaOrdinal: 1, nameOrdinal: 2, typeOrdinal: 3, encryptedOrdinal: 4);
    }

    /// <summary>
    /// Reads one direction of <c>sys.sql_expression_dependencies</c>. The view exposes
    /// <c>referenced_id</c>/<c>referenced_minor_id</c> rather than usable names, so the object
    /// is resolved through <c>sys.objects</c> and the column through <c>sys.columns</c>.
    /// </summary>
    private static async Task<List<RelatedEdge>> LoadExpressionDependenciesAsync(
        SqlConnection conn, int objectId, bool incoming, CancellationToken ct)
    {
        // is_schema_bound_reference is deliberately avoided (it has bitten this codebase on
        // real servers); the referencing module's own is_schema_bound carries the same signal.
        const string outgoingSql = """
            SELECT
                ISNULL(SCHEMA_NAME(ro.schema_id), 'dbo') AS related_schema,
                ro.name AS related_name,
                ro.type AS related_type,
                ro.object_id AS related_object_id,
                CASE WHEN rm.object_id IS NOT NULL AND rm.definition IS NULL THEN 1 ELSE 0 END AS related_is_encrypted,
                rc.name AS referenced_column,
                d.is_ambiguous,
                ISNULL(sm.is_schema_bound, 0) AS is_schema_bound
            FROM sys.sql_expression_dependencies d
            JOIN sys.objects ro ON ro.object_id = d.referenced_id
            LEFT JOIN sys.sql_modules rm ON rm.object_id = ro.object_id
            LEFT JOIN sys.sql_modules sm ON sm.object_id = d.referencing_id
            LEFT JOIN sys.columns rc
                ON rc.object_id = d.referenced_id
                AND rc.column_id = d.referenced_minor_id
                AND d.referenced_minor_id > 0
            WHERE d.referencing_id = @objectId
              AND d.referencing_class = 1
              AND d.referenced_class = 1
            """;

        const string incomingSql = """
            SELECT
                ISNULL(SCHEMA_NAME(o.schema_id), 'dbo') AS related_schema,
                o.name AS related_name,
                o.type AS related_type,
                o.object_id AS related_object_id,
                CASE WHEN om.object_id IS NOT NULL AND om.definition IS NULL THEN 1 ELSE 0 END AS related_is_encrypted,
                rc.name AS referenced_column,
                d.is_ambiguous,
                ISNULL(om.is_schema_bound, 0) AS is_schema_bound
            FROM sys.sql_expression_dependencies d
            JOIN sys.objects o ON o.object_id = d.referencing_id
            LEFT JOIN sys.sql_modules om ON om.object_id = o.object_id
            LEFT JOIN sys.columns rc
                ON rc.object_id = d.referenced_id
                AND rc.column_id = d.referenced_minor_id
                AND d.referenced_minor_id > 0
            WHERE d.referenced_id = @objectId
              AND d.referencing_class = 1
              AND d.referenced_class = 1
              AND o.is_ms_shipped = 0
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = incoming ? incomingSql : outgoingSql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@objectId", objectId);

        var edges = new List<RelatedEdge>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var related = ReadObjectRef(reader, 3, 0, 1, 2, 4);
            if (ReadFlag(reader, 6))
            {
                related = related with
                {
                    Note = Combine(related.Note, "Ambiguous dependency - catalog metadata may be incomplete")
                };
            }

            edges.Add(new RelatedEdge(
                related,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                ReadFlag(reader, 7)));
        }

        return edges;
    }

    /// <summary>
    /// Rows whose <c>referenced_id</c> is null: cross-server, cross-database, or a name that
    /// resolves at run time. They become terminal nodes with a note instead of vanishing.
    /// </summary>
    private async Task<List<RelatedEdge>> LoadUnresolvedDependenciesAsync(
        SqlConnection conn, GraphState state, ObjectRef source, CancellationToken ct)
    {
        const string sql = """
            SELECT
                d.referenced_server_name,
                d.referenced_database_name,
                d.referenced_schema_name,
                d.referenced_entity_name,
                d.is_caller_dependent
            FROM sys.sql_expression_dependencies d
            WHERE d.referencing_id = @objectId
              AND d.referencing_class = 1
              AND d.referenced_id IS NULL
              AND d.referenced_entity_name IS NOT NULL
            """;

        var edges = new List<RelatedEdge>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@objectId", source.ObjectId);

        DbDataReader reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(ct);
        }
        catch (DbException ex)
        {
            // The name columns on this view have proven unreliable across SQL Server builds.
            // Degrade loudly: the rest of the graph stays valid, the user is told what is missing.
            logger.LogWarning(ex, "Cross-database dependency detection failed for {Object}", source.Key);
            state.AddWarning(
                $"{Quote(source.Schema, source.Name)}: cross-database dependency detection is unavailable on this server ({ex.Message})");
            return edges;
        }

        using (reader)
        {
            while (await reader.ReadAsync(ct))
            {
                var server = reader.IsDBNull(0) ? null : reader.GetString(0);
                var database = reader.IsDBNull(1) ? null : reader.GetString(1);
                var refSchema = reader.IsDBNull(2) ? null : reader.GetString(2);
                var refName = reader.GetString(3);
                var callerDependent = ReadFlag(reader, 4);

                if (server is null && database is null)
                {
                    // Deferred name resolution (temp tables, EXEC on a caller-supplied name) or a
                    // genuinely missing object. Neither is a catalog node we can place.
                    var reason = callerDependent
                        ? "resolved at run time (caller-dependent) - not traversable"
                        : "not found in this database";
                    state.AddWarning($"{Quote(source.Schema, source.Name)} references {FormatName(null, null, refSchema, refName)}: {reason}");
                    continue;
                }

                var displayName = FormatName(server, database, refSchema, refName);

                var external = new ObjectRef(
                    MakeExternalKey(server, database, refSchema, refName),
                    refSchema ?? "dbo",
                    refName,
                    TypeCode: "",
                    ObjectId: 0,
                    Note: $"External reference to {displayName} - not traversed");

                state.AddWarning(
                    $"{Quote(source.Schema, source.Name)} references {displayName}: outside this database, dependencies below it are unknown");

                edges.Add(new RelatedEdge(external, ColumnName: null, IsSchemabound: false));
            }
        }

        return edges;
    }

    private async Task<List<RelatedEdge>> LoadSynonymBaseAsync(
        SqlConnection conn, GraphState state, ObjectRef synonym, CancellationToken ct)
    {
        const string sql = "SELECT base_object_name FROM sys.synonyms WHERE object_id = @objectId";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@objectId", synonym.ObjectId);

        var baseName = (string?)await cmd.ExecuteScalarAsync(ct);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            state.AddWarning($"{Quote(synonym.Schema, synonym.Name)}: synonym base object could not be read");
            return [];
        }

        var parts = SplitQualifiedName(baseName);
        var edges = new List<RelatedEdge>();

        if (parts.Count >= 3)
        {
            // [server].[db].[schema].[name] or [db].[schema].[name]: nothing local to traverse.
            var external = new ObjectRef(
                baseName.ToUpperInvariant(),
                parts[^2],
                parts[^1],
                TypeCode: "",
                ObjectId: 0,
                Note: $"Synonym target {baseName} lives outside this database - not traversed");

            state.AddWarning($"{Quote(synonym.Schema, synonym.Name)}: synonym target {baseName} is outside this database, dependencies below it are unknown");
            edges.Add(new RelatedEdge(external, ColumnName: null, IsSchemabound: false));
            return edges;
        }

        var targetSchema = parts.Count == 2 ? parts[0] : "dbo";
        var targetName = parts[^1];

        var resolved = await LoadObjectAsync(conn, targetSchema, targetName, ct);
        if (resolved is null)
        {
            var missing = new ObjectRef(
                MakeKey(targetSchema, targetName), targetSchema, targetName, TypeCode: "", ObjectId: 0,
                Note: "Synonym target not found in sys.objects");
            state.AddWarning($"{Quote(synonym.Schema, synonym.Name)}: synonym target {Quote(targetSchema, targetName)} does not exist");
            edges.Add(new RelatedEdge(missing, ColumnName: null, IsSchemabound: false));
            return edges;
        }

        edges.Add(new RelatedEdge(resolved, ColumnName: null, IsSchemabound: false));
        return edges;
    }

    /// <summary>Triggers attached to a table. A trigger always depends on its parent, never the reverse.</summary>
    private static async Task<List<RelatedEdge>> LoadTriggerEdgesAsync(
        SqlConnection conn, int tableObjectId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                ISNULL(SCHEMA_NAME(o.schema_id), 'dbo') AS trigger_schema,
                o.name AS trigger_name,
                o.type AS type_code,
                o.object_id,
                CASE WHEN m.object_id IS NOT NULL AND m.definition IS NULL THEN 1 ELSE 0 END AS is_encrypted,
                tr.is_disabled
            FROM sys.triggers tr
            JOIN sys.objects o ON o.object_id = tr.object_id
            LEFT JOIN sys.sql_modules m ON m.object_id = tr.object_id
            WHERE tr.parent_id = @objectId
              AND tr.parent_class = 1
              AND tr.is_ms_shipped = 0
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@objectId", tableObjectId);

        var edges = new List<RelatedEdge>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var trigger = ReadObjectRef(reader, 3, 0, 1, 2, 4);
            if (ReadFlag(reader, 5))
                trigger = trigger with { Note = Combine(trigger.Note, "Trigger is disabled") };

            edges.Add(new RelatedEdge(trigger, ColumnName: null, IsSchemabound: false));
        }

        return edges;
    }

    /// <summary>
    /// Foreign keys, always modelled as child -> parent. <paramref name="childSide"/> asks for the
    /// children of the given parent table; otherwise the parents of the given child table.
    /// </summary>
    private static async Task<List<RelatedEdge>> LoadForeignKeyEdgesAsync(
        SqlConnection conn, int tableObjectId, bool childSide, CancellationToken ct)
    {
        const string parentsSql = """
            SELECT DISTINCT
                ISNULL(SCHEMA_NAME(rt.schema_id), 'dbo') AS related_schema,
                rt.name AS related_name,
                rt.type AS type_code,
                rt.object_id
            FROM sys.foreign_keys fk
            JOIN sys.objects rt ON rt.object_id = fk.referenced_object_id
            WHERE fk.parent_object_id = @objectId
            """;

        const string childrenSql = """
            SELECT DISTINCT
                ISNULL(SCHEMA_NAME(pt.schema_id), 'dbo') AS related_schema,
                pt.name AS related_name,
                pt.type AS type_code,
                pt.object_id
            FROM sys.foreign_keys fk
            JOIN sys.objects pt ON pt.object_id = fk.parent_object_id
            WHERE fk.referenced_object_id = @objectId
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = childSide ? childrenSql : parentsSql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@objectId", tableObjectId);

        var edges = new List<RelatedEdge>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var related = new ObjectRef(
                MakeKey(reader.GetString(0), reader.GetString(1)),
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2).Trim(),
                reader.GetInt32(3));

            edges.Add(new RelatedEdge(related, ColumnName: null, IsSchemabound: false));
        }

        return edges;
    }

    #endregion

    #region Graph assembly

    /// <summary>An object the traversal has placed, or wants to place, in the graph.</summary>
    private sealed record ObjectRef(
        string Key,
        string Schema,
        string Name,
        string TypeCode,
        int ObjectId,
        string? Note = null)
    {
        /// <summary>False for cross-database, cross-server and missing objects: they have no local catalog rows to follow.</summary>
        public bool IsResolvable => ObjectId != 0;
    }

    private sealed record RelatedEdge(ObjectRef Target, string? ColumnName, bool IsSchemabound);

    private readonly record struct LinkKey(string FromKey, string ToKey, DependencyDirection Direction);

    private sealed class LinkAggregate
    {
        /// <summary>True once an object-level row was seen; column-level rows are then redundant noise.</summary>
        public bool HasObjectLevel { get; set; }

        public HashSet<string> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsSchemabound { get; set; }
    }

    private sealed class NodeBuilder
    {
        public required string Key { get; init; }
        public required string Schema { get; set; }
        public required string Name { get; set; }
        public string TypeCode { get; set; } = "";
        public int ObjectId { get; set; }
        public bool IsRoot { get; set; }
        public int Level { get; set; }
        public List<string> Notes { get; } = [];
    }

    private sealed class GraphState
    {
        private readonly Dictionary<string, NodeBuilder> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<LinkKey, LinkAggregate> _links = new();
        private readonly List<string> _warnings = [];
        private readonly HashSet<string> _seenWarnings = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _rootKeys = [];

        public void AddNode(ObjectRef reference, int level, bool isRoot = false)
        {
            if (!_nodes.TryGetValue(reference.Key, out var node))
            {
                node = new NodeBuilder
                {
                    Key = reference.Key,
                    Schema = reference.Schema,
                    Name = reference.Name,
                    TypeCode = reference.TypeCode,
                    ObjectId = reference.ObjectId,
                    Level = level
                };
                _nodes[reference.Key] = node;
            }
            else if (node.ObjectId == 0 && reference.ObjectId != 0)
            {
                // First seen as an unresolved edge target, now identified properly.
                node.Schema = reference.Schema;
                node.Name = reference.Name;
                node.TypeCode = reference.TypeCode;
                node.ObjectId = reference.ObjectId;
            }

            if (reference.Note is not null && !node.Notes.Contains(reference.Note, StringComparer.OrdinalIgnoreCase))
                node.Notes.Add(reference.Note);

            if (isRoot)
            {
                node.IsRoot = true;
                node.Level = 0;
                if (!_rootKeys.Contains(node.Key, StringComparer.Ordinal))
                    _rootKeys.Add(node.Key);
            }
            else if (!node.IsRoot && Math.Abs(level) < Math.Abs(node.Level))
            {
                // Reachable both ways: keep the shorter hop count, whichever side it came from.
                node.Level = level;
            }
        }

        public void AddLink(string fromKey, string toKey, DependencyDirection direction, string? columnName, bool isSchemabound)
        {
            var key = new LinkKey(fromKey, toKey, direction);
            if (!_links.TryGetValue(key, out var aggregate))
            {
                aggregate = new LinkAggregate();
                _links[key] = aggregate;
            }

            if (columnName is null)
                aggregate.HasObjectLevel = true;
            else
                aggregate.Columns.Add(columnName);

            aggregate.IsSchemabound |= isSchemabound;
        }

        public void AddWarning(string message)
        {
            if (_seenWarnings.Add(message))
                _warnings.Add(message);
        }

        public ObjectDependencyGraph Build()
        {
            var nodes = _nodes.Values
                .OrderBy(n => n.Level)
                .ThenBy(n => n.Key, StringComparer.Ordinal)
                .Select(n => new DependencyNode
                {
                    Key = n.Key,
                    Schema = n.Schema,
                    Name = n.Name,
                    Kind = MapObjectType(n.TypeCode),
                    IsRoot = n.IsRoot,
                    Level = n.Level,
                    Note = n.Notes.Count > 0 ? string.Join("; ", n.Notes) : null
                })
                .ToList();

            var links = new List<DependencyLink>();
            foreach (var (key, aggregate) in _links
                         .OrderBy(kv => kv.Key.FromKey, StringComparer.Ordinal)
                         .ThenBy(kv => kv.Key.ToKey, StringComparer.Ordinal)
                         .ThenBy(kv => kv.Key.Direction))
            {
                if (aggregate.HasObjectLevel || aggregate.Columns.Count == 0)
                {
                    links.Add(new DependencyLink
                    {
                        FromKey = key.FromKey,
                        ToKey = key.ToKey,
                        Direction = key.Direction,
                        IsSchemabound = aggregate.IsSchemabound
                    });
                    continue;
                }

                // Only column-level rows were recorded, so the columns are the whole story.
                foreach (var column in aggregate.Columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
                {
                    links.Add(new DependencyLink
                    {
                        FromKey = key.FromKey,
                        ToKey = key.ToKey,
                        Direction = key.Direction,
                        ColumnName = column,
                        IsSchemabound = aggregate.IsSchemabound
                    });
                }
            }

            var baseTables = _nodes.Values
                .Where(n => IsUserTable(n.TypeCode))
                .OrderBy(n => n.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(n => (n.Schema, Table: n.Name))
                .ToList();

            return new ObjectDependencyGraph
            {
                Nodes = nodes,
                Links = links,
                RootKeys = _rootKeys.ToList(),
                Warnings = _warnings.ToList(),
                BaseTables = baseTables
            };
        }
    }

    #endregion

    #region Helpers

    private static SqlObjectKind MapObjectType(string typeCode) => typeCode.Trim() switch
    {
        "U" => SqlObjectKind.Table,
        "V" => SqlObjectKind.View,
        "P" => SqlObjectKind.StoredProcedure,
        "PC" => SqlObjectKind.StoredProcedure,
        "FN" => SqlObjectKind.ScalarFunction,
        "FS" => SqlObjectKind.ScalarFunction,
        "TF" => SqlObjectKind.TableValuedFunction,
        "FT" => SqlObjectKind.TableValuedFunction,
        "IF" => SqlObjectKind.InlineTableValuedFunction,
        "TR" => SqlObjectKind.Trigger,
        "TA" => SqlObjectKind.Trigger,
        "SN" => SqlObjectKind.Synonym,
        "SO" => SqlObjectKind.Sequence,
        "TT" => SqlObjectKind.TableType,
        "ET" => SqlObjectKind.Table,
        _ => SqlObjectKind.Unknown
    };

    /// <summary>Base tables are strictly <c>sys.objects.type = 'U'</c> -- external tables are not copyable.</summary>
    private static bool IsUserTable(string typeCode) =>
        string.Equals(typeCode.Trim(), "U", StringComparison.OrdinalIgnoreCase);

    private static ObjectRef ReadObjectRef(
        DbDataReader reader, int objectIdOrdinal, int schemaOrdinal, int nameOrdinal, int typeOrdinal, int encryptedOrdinal)
    {
        var schema = reader.GetString(schemaOrdinal);
        var name = reader.GetString(nameOrdinal);
        var note = ReadFlag(reader, encryptedOrdinal) ? "Encrypted module - definition not available" : null;

        return new ObjectRef(
            MakeKey(schema, name),
            schema,
            name,
            reader.GetString(typeOrdinal).Trim(),
            reader.GetInt32(objectIdOrdinal),
            note);
    }

    private static string MakeKey(string schema, string name) =>
        $"{schema}.{name}".ToUpperInvariant();

    /// <summary>
    /// Keys for objects in another database or on another server keep their qualifier, so a
    /// three-or-four-part key can never collide with a local two-part <c>SCHEMA.NAME</c>.
    /// </summary>
    private static string MakeExternalKey(string? server, string? database, string? schema, string name)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(server)) parts.Add(server);
        if (!string.IsNullOrEmpty(database)) parts.Add(database);
        parts.Add(string.IsNullOrEmpty(schema) ? "dbo" : schema);
        parts.Add(name);
        return string.Join('.', parts).ToUpperInvariant();
    }

    /// <summary>Renders a possibly-qualified name as bracket-quoted parts, for warning text.</summary>
    private static string FormatName(string? server, string? database, string? schema, string name)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(server)) parts.Add(server);
        if (!string.IsNullOrEmpty(database)) parts.Add(database);
        if (!string.IsNullOrEmpty(schema)) parts.Add(schema);
        parts.Add(name);
        return string.Join('.', parts.Select(p => $"[{p.Replace("]", "]]")}]"));
    }

    private static string Quote(string schema, string name) =>
        $"[{schema.Replace("]", "]]")}].[{name.Replace("]", "]]")}]";

    /// <summary>
    /// Splits a stored multi-part name (<c>[db].[schema].[obj]</c>, <c>schema.obj</c>, …) on the
    /// dots that sit outside brackets, so a name containing a dot survives intact.
    /// </summary>
    private static List<string> SplitQualifiedName(string qualifiedName)
    {
        var parts = new List<string>(4);
        var current = new StringBuilder();
        var inBrackets = false;

        for (var i = 0; i < qualifiedName.Length; i++)
        {
            var c = qualifiedName[i];
            if (inBrackets)
            {
                if (c != ']')
                {
                    current.Append(c);
                }
                else if (i + 1 < qualifiedName.Length && qualifiedName[i + 1] == ']')
                {
                    current.Append(']');
                    i++;
                }
                else
                {
                    inBrackets = false;
                }
            }
            else if (c == '[')
            {
                inBrackets = true;
            }
            else if (c == '.')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString().Trim());
        return parts;
    }

    private static string Combine(string? existing, string addition) =>
        string.IsNullOrEmpty(existing) ? addition : $"{existing}; {addition}";

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Reads a catalog flag that may surface as <see cref="bool"/> (<c>bit</c>),
    /// <see cref="byte"/> (<c>tinyint</c>) or <see cref="int"/> depending on expression typing.
    /// </summary>
    private static bool ReadFlag(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return false;

        return reader.GetValue(ordinal) switch
        {
            bool b => b,
            int i => i != 0,
            byte b => b != 0,
            short s => s != 0,
            long l => l != 0,
            var other => Convert.ToInt64(other) != 0
        };
    }

    #endregion
}
