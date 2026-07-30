using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.WebUI.Components.Pages.SqlBrowser;

/// <summary>
/// Deterministic, dependency-free "Sugiyama-lite" left-to-right layered layout for an
/// <see cref="ObjectDependencyGraph"/>. Pure C# so it can be unit tested without Blazor.
/// </summary>
public static class DependencyGraphLayout
{
    public const double BoxWidth = 190;
    public const double BoxHeight = 46;
    public const double ColumnGap = 140;
    public const double RowGap = 64;

    /// <summary>Outer padding so boxes/edges never touch the SVG viewport edge.</summary>
    public const double Padding = 24;

    public static DependencyGraphLayoutResult Layout(ObjectDependencyGraph? graph)
    {
        if (graph is null || graph.Nodes.Count == 0)
            return new DependencyGraphLayoutResult(
                Array.Empty<DependencyNodeLayout>(),
                Array.Empty<DependencyEdgeLayout>(),
                Array.Empty<DependencyColumnLayout>(),
                Padding * 2,
                Padding * 2);

        // De-duplicate defensively by Key (contract guarantees uniqueness, but layout must not
        // crash or double-render if the backend ever produces a duplicate).
        var nodesByKey = new Dictionary<string, DependencyNode>();
        foreach (var n in graph.Nodes)
            nodesByKey[n.Key] = n;

        // Group into columns by Level.
        var levels = nodesByKey.Values.Select(n => n.Level).Distinct().OrderBy(l => l).ToList();
        var columnOrder = new Dictionary<int, List<string>>();
        foreach (var level in levels)
        {
            columnOrder[level] = nodesByKey.Values
                .Where(n => n.Level == level)
                .Select(n => n.Key)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
        }

        // Adjacency (undirected) restricted to keys that actually exist as nodes, used only for
        // ordering-within-column heuristics (crossing minimization) — never for traversal, so
        // cycles cannot cause infinite loops here.
        var neighbors = new Dictionary<string, List<string>>();
        foreach (var link in graph.Links)
        {
            if (!nodesByKey.ContainsKey(link.FromKey) || !nodesByKey.ContainsKey(link.ToKey))
                continue; // dangling reference — skip, don't fail layout.
            if (link.FromKey == link.ToKey)
                continue; // self-loop, irrelevant to column ordering.

            AddNeighbor(neighbors, link.FromKey, link.ToKey);
            AddNeighbor(neighbors, link.ToKey, link.FromKey);
        }

        // Barycenter ordering passes to reduce edge crossings.
        var positionInColumn = new Dictionary<string, int>();
        void RebuildPositions()
        {
            foreach (var level in levels)
            {
                var list = columnOrder[level];
                for (var i = 0; i < list.Count; i++)
                    positionInColumn[list[i]] = i;
            }
        }
        RebuildPositions();

        const int passes = 3;
        for (var pass = 0; pass < passes; pass++)
        {
            // Forward: order column c by median position of its neighbors in column c-1.
            for (var c = 1; c < levels.Count; c++)
                ReorderColumn(columnOrder[levels[c]], positionInColumn, neighbors);
            RebuildPositions();

            // Backward: order column c by median position of its neighbors in column c+1.
            for (var c = levels.Count - 2; c >= 0; c--)
                ReorderColumn(columnOrder[levels[c]], positionInColumn, neighbors);
            RebuildPositions();
        }

        // Compute geometry. Columns are laid out left-to-right in level order; X only depends on
        // column index, not on the (possibly sparse/negative) level value itself.
        var columns = new List<DependencyColumnLayout>();
        var nodeLayouts = new List<DependencyNodeLayout>();
        var nodeLayoutByKey = new Dictionary<string, DependencyNodeLayout>();

        double maxColumnHeight = 0;
        foreach (var level in levels)
        {
            var count = columnOrder[level].Count;
            var thisColumnHeight = count * BoxHeight + Math.Max(0, count - 1) * RowGap;
            if (thisColumnHeight > maxColumnHeight) maxColumnHeight = thisColumnHeight;
        }

        for (var c = 0; c < levels.Count; c++)
        {
            var level = levels[c];
            var keys = columnOrder[level];
            var columnX = Padding + c * (BoxWidth + ColumnGap);
            var columnHeight = keys.Count * BoxHeight + Math.Max(0, keys.Count - 1) * RowGap;
            var yOffset = Padding + (maxColumnHeight - columnHeight) / 2.0; // vertically center column

            columns.Add(new DependencyColumnLayout(level, columnX, BoxWidth));

            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                var node = nodesByKey[key];
                var y = yOffset + i * (BoxHeight + RowGap);
                var layout = new DependencyNodeLayout(node, columnX, y, BoxWidth, BoxHeight);
                nodeLayouts.Add(layout);
                nodeLayoutByKey[key] = layout;
            }
        }

        // Edges: cubic beziers exiting/entering the appropriate side based on relative X position
        // of the two endpoints (arrowhead always points FromKey -> ToKey, i.e. referencing ->
        // referenced, per DependencyLink's own contract — independent of Direction).
        var edgeLayouts = new List<DependencyEdgeLayout>();
        foreach (var link in graph.Links)
        {
            if (!nodeLayoutByKey.TryGetValue(link.FromKey, out var from) ||
                !nodeLayoutByKey.TryGetValue(link.ToKey, out var to))
                continue; // dangling — skip (graph.Warnings should already surface why).

            var leftToRight = from.X <= to.X;

            double startX, startY, endX, endY;
            if (leftToRight)
            {
                startX = from.X + from.Width;
                startY = from.Y + from.Height / 2.0;
                endX = to.X;
                endY = to.Y + to.Height / 2.0;
            }
            else
            {
                startX = from.X;
                startY = from.Y + from.Height / 2.0;
                endX = to.X + to.Width;
                endY = to.Y + to.Height / 2.0;
            }

            var dx = Math.Max(Math.Abs(endX - startX) / 2.0, ColumnGap / 3.0);
            var c1X = leftToRight ? startX + dx : startX - dx;
            var c2X = leftToRight ? endX - dx : endX + dx;

            edgeLayouts.Add(new DependencyEdgeLayout(
                link, startX, startY, c1X, startY, c2X, endY, endX, endY));
        }

        var width = Padding * 2 + levels.Count * BoxWidth + Math.Max(0, levels.Count - 1) * ColumnGap;
        var height = Padding * 2 + maxColumnHeight;
        // Guard against a degenerate zero-height canvas (e.g. a single node column).
        if (height < BoxHeight + Padding * 2) height = BoxHeight + Padding * 2;

        return new DependencyGraphLayoutResult(nodeLayouts, edgeLayouts, columns, width, height);
    }

    private static void AddNeighbor(Dictionary<string, List<string>> neighbors, string from, string to)
    {
        if (!neighbors.TryGetValue(from, out var list))
        {
            list = new List<string>();
            neighbors[from] = list;
        }
        if (!list.Contains(to))
            list.Add(to);
    }

    private static void ReorderColumn(
        List<string> column,
        Dictionary<string, int> positionInColumn,
        Dictionary<string, List<string>> neighbors)
    {
        // Barycenter = average index (within the OTHER column) of a node's neighbors that
        // currently live in an adjacent column. Nodes with no such neighbors keep their current
        // slot (fallback value = current index) so unrelated nodes stay stable across passes.
        var keyed = new List<(string Key, double Score)>(column.Count);
        for (var i = 0; i < column.Count; i++)
        {
            var key = column[i];
            double score = i;
            if (neighbors.TryGetValue(key, out var nbrs) && nbrs.Count > 0)
            {
                var known = nbrs.Where(positionInColumn.ContainsKey).ToList();
                if (known.Count > 0)
                    score = known.Average(k => positionInColumn[k]);
            }
            keyed.Add((key, score));
        }

        keyed.Sort((a, b) =>
        {
            var cmp = a.Score.CompareTo(b.Score);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Key, b.Key);
        });

        for (var i = 0; i < keyed.Count; i++)
            column[i] = keyed[i].Key;
    }
}

/// <summary>Position and size for one node's box, top-left origin.</summary>
public sealed record DependencyNodeLayout(DependencyNode Node, double X, double Y, double Width, double Height)
{
    public double CenterY => Y + Height / 2.0;
    public double RightX => X + Width;
}

/// <summary>SVG-ready cubic bezier control points for one edge, plus the source link.</summary>
public sealed record DependencyEdgeLayout(
    DependencyLink Link,
    double StartX, double StartY,
    double Control1X, double Control1Y,
    double Control2X, double Control2Y,
    double EndX, double EndY)
{
    public string PathData =>
        $"M {StartX.ToString(System.Globalization.CultureInfo.InvariantCulture)},{StartY.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
        $"C {Control1X.ToString(System.Globalization.CultureInfo.InvariantCulture)},{Control1Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
        $"{Control2X.ToString(System.Globalization.CultureInfo.InvariantCulture)},{Control2Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
        $"{EndX.ToString(System.Globalization.CultureInfo.InvariantCulture)},{EndY.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

/// <summary>A column's level and horizontal extent, used for header placement.</summary>
public sealed record DependencyColumnLayout(int Level, double X, double Width);

/// <summary>Full computed layout for an <see cref="ObjectDependencyGraph"/>.</summary>
public sealed record DependencyGraphLayoutResult(
    IReadOnlyList<DependencyNodeLayout> Nodes,
    IReadOnlyList<DependencyEdgeLayout> Edges,
    IReadOnlyList<DependencyColumnLayout> Columns,
    double Width,
    double Height);
