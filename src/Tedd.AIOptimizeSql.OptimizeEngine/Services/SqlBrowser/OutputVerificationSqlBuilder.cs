using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Builds the T-SQL that fingerprints a benchmark query's output without knowing its column
/// list: the rows are materialised into a temp table and each row is hashed through
/// <c>FOR XML RAW</c>, so every column participates automatically. Deterministic, no AI.
/// </summary>
/// <remarks>
/// The strategy only works when the benchmark is one read-only SELECT (optionally with a CTE
/// list in front), because the query has to survive being wrapped in a derived table. Anything
/// else gets a placeholder script whose leading comment tells the user to supply their own
/// fingerprint query — see <see cref="CanFingerprint"/>.
/// </remarks>
public static class OutputVerificationSqlBuilder
{
    /// <summary>
    /// Words that disqualify a benchmark from automatic fingerprinting. Some are outright
    /// writes; <c>INTO</c> catches <c>SELECT ... INTO</c>, which would fight our own
    /// materialisation.
    /// </summary>
    private static readonly HashSet<string> DisqualifyingWords = new(StringComparer.Ordinal)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "INTO", "OUTPUT",
        "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP",
        "GRANT", "REVOKE", "DENY", "BACKUP", "RESTORE", "DBCC", "WAITFOR",
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE",
    };

    /// <summary>
    /// Builds the fingerprint script for <paramref name="benchmarkSql"/>, or null for
    /// <see cref="OutputVerificationMode.None"/>. The script always returns exactly one row
    /// with one column named <c>OutputHash</c>, so callers can use <c>ExecuteScalar</c>.
    /// </summary>
    public static string? Build(string benchmarkSql, OutputVerificationMode mode)
    {
        if (mode == OutputVerificationMode.None)
            return null;

        var analysis = Analyze(benchmarkSql);
        if (!analysis.Ok)
            return BuildManualPlaceholder(analysis.Reason!);

        return mode == OutputVerificationMode.OrderedHash
            ? BuildOrdered(analysis.Prefix, analysis.Inner)
            : BuildUnordered(analysis.Prefix, analysis.Inner);
    }

    /// <summary>
    /// Whether <paramref name="benchmarkSql"/> can be fingerprinted automatically. When false,
    /// <paramref name="reason"/> explains why as a sentence fragment usable in a warning
    /// ("it contains GO batch separators").
    /// </summary>
    public static bool CanFingerprint(string benchmarkSql, out string? reason)
    {
        var analysis = Analyze(benchmarkSql);
        reason = analysis.Reason;
        return analysis.Ok;
    }

    #region Benchmark classification

    // Prefix is the statement-level CTE list (empty for a plain SELECT), kept in front of the
    // generated statement because a derived table may not start with WITH; Inner is the SELECT
    // that gets wrapped.
    private sealed record Analysis(bool Ok, string? Reason, string Prefix, string Inner);

    private static Analysis Fail(string reason) => new(false, reason, "", "");

    /// <summary>
    /// Conservatively decides whether the benchmark is a single SELECT-ish statement and splits
    /// it into the part that must stay at statement level (a leading CTE list) and the part that
    /// gets wrapped. Appends <c>OFFSET 0 ROWS</c> when a trailing ORDER BY would otherwise be
    /// illegal inside the wrapping derived table.
    /// </summary>
    private static Analysis Analyze(string benchmarkSql)
    {
        if (string.IsNullOrWhiteSpace(benchmarkSql))
            return Fail("the benchmark SQL is empty");

        // A leading ';WITH' is idiomatic; trailing semicolons are noise once wrapped.
        var sql = benchmarkSql.Trim().TrimStart(';').Trim().TrimEnd(';').TrimEnd();
        if (sql.Length == 0)
            return Fail("the benchmark SQL is empty");

        if (MsSqlExecutor.SplitOnGo(sql).Count > 1)
            return Fail("it contains GO batch separators");

        var (words, hasTrailingStatements) = Scan(sql);

        if (words.Count == 0)
            return Fail("no SQL statement was found");

        if (words[0].Word is not ("SELECT" or "WITH"))
            return Fail($"it starts with '{words[0].Word}' instead of SELECT or WITH");

        foreach (var word in words)
        {
            if (DisqualifyingWords.Contains(word.Word))
                return Fail($"it contains '{word.Word}', so it is not a plain SELECT");
        }

        if (hasTrailingStatements)
            return Fail("it contains more than one statement");

        var top = words.Where(w => w.Depth == 0).ToList();

        // OPTION and FOR XML/FOR JSON are statement-level clauses that a derived table rejects.
        if (top.Any(w => w.Word == "OPTION"))
            return Fail("it ends in an OPTION (query hint) clause, which is not allowed inside a derived table");

        for (var i = 0; i < top.Count - 1; i++)
        {
            if (top[i].Word == "FOR" && top[i + 1].Word is "XML" or "JSON")
                return Fail($"it ends in a FOR {top[i + 1].Word} clause, whose output cannot be materialised as rows");
        }

        // A CTE list has to stay in front of the generated statement: every CTE body sits inside
        // parentheses, so the statement's own SELECT is the first one left at depth 0.
        var prefix = "";
        var inner = sql;
        if (words[0].Word == "WITH")
        {
            var statementSelect = top.FindIndex(w => w.Word == "SELECT");
            if (statementSelect < 0)
                return Fail("its CTE list is not followed by a SELECT");

            var splitAt = top[statementSelect].Index;
            prefix = sql[..splitAt].TrimEnd() + Environment.NewLine;
            inner = sql[splitAt..];
        }

        var orderBy = LastIndexOfPair(top, "ORDER", "BY");
        if (orderBy < 0)
            return new Analysis(true, null, prefix, inner);

        // ORDER BY is only legal inside a derived table when TOP, OFFSET or FOR XML is also
        // present. TOP and an existing OFFSET already satisfy that; otherwise append a no-op
        // OFFSET, which changes neither the rows nor their order.
        if (top.Any(w => w.Word == "TOP"))
            return new Analysis(true, null, prefix, inner);

        if (top.Skip(orderBy + 2).Any(w => w.Word == "OFFSET"))
            return new Analysis(true, null, prefix, inner);

        return new Analysis(true, null, prefix, inner + Environment.NewLine + "OFFSET 0 ROWS");
    }

    private readonly record struct SqlWord(string Word, int Depth, int Index);

    /// <summary>
    /// Walks raw SQL, collecting keyword/identifier words with the parenthesis depth and offset
    /// they appear at, and noting whether a statement follows a top-level semicolon. Comments,
    /// string literals, bracketed and double-quoted identifiers are skipped, so neither a
    /// commented-out DELETE nor a column named <c>[Order By]</c> can be mistaken for a clause.
    /// </summary>
    private static (List<SqlWord> Words, bool HasTrailingStatements) Scan(string sql)
    {
        var words = new List<SqlWord>();
        var hasTrailingStatements = false;
        var depth = 0;
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var commentDepth = 1; // T-SQL block comments nest
                i += 2;
                while (i < sql.Length && commentDepth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { commentDepth++; i += 2; continue; }
                    if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { commentDepth--; i += 2; continue; }
                    i++;
                }
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] != '\'') { i++; continue; }
                    if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                    i++;
                    break;
                }
                continue;
            }

            if (c == '[')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] != ']') { i++; continue; }
                    if (i + 1 < sql.Length && sql[i + 1] == ']') { i += 2; continue; }
                    i++;
                    break;
                }
                continue;
            }

            if (c == '"')
            {
                i++;
                while (i < sql.Length && sql[i] != '"') i++;
                if (i < sql.Length) i++;
                continue;
            }

            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth > 0) depth--; i++; continue; }

            if (c == ';')
            {
                if (depth == 0)
                {
                    for (var j = i + 1; j < sql.Length; j++)
                    {
                        if (sql[j] == ';' || char.IsWhiteSpace(sql[j]))
                            continue;
                        hasTrailingStatements = true;
                        break;
                    }
                }
                i++;
                continue;
            }

            // '@' and '#' are folded into the word so @delete and #insert cannot trip the
            // disqualifying-word scan.
            if (char.IsLetter(c) || c is '_' or '@' or '#')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '#' or '$'))
                    i++;
                words.Add(new SqlWord(sql[start..i].ToUpperInvariant(), depth, start));
                continue;
            }

            i++;
        }

        return (words, hasTrailingStatements);
    }

    private static int LastIndexOfPair(List<SqlWord> words, string first, string second)
    {
        for (var i = words.Count - 2; i >= 0; i--)
        {
            if (words[i].Word == first && words[i + 1].Word == second)
                return i;
        }
        return -1;
    }

    #endregion

    #region Script templates

    private static string BuildUnordered(string prefix, string inner) =>
        $"""
        -- Output fingerprint, order-insensitive (OutputVerificationMode.UnorderedHash).
        -- The benchmark must be a single SELECT statement: its rows are materialised into a temp
        -- table so every column takes part in the hash without naming any of them. That also means
        -- every column the benchmark returns must have a name -- SELECT COUNT(*) needs an alias.
        SET NOCOUNT ON;

        IF OBJECT_ID('tempdb..#aiopt_output') IS NOT NULL DROP TABLE #aiopt_output;

        {prefix}SELECT [aiopt_q].*
        INTO #aiopt_output
        FROM
        (
        {inner}
        ) AS [aiopt_q];

        DECLARE @OutputHash nvarchar(128);

        -- Per-row digest: (SELECT [t].* FOR XML RAW ...) is evaluated per outer row, so it
        -- serialises every column with its name and needs no column list. Caveats worth knowing:
        -- HASHBYTES accepts nvarchar(max) only from SQL Server 2016 onwards, and FOR XML refuses
        -- characters that are illegal in XML (0x0000) -- such a column needs a hand-written
        -- fingerprint instead. The leftmost 8 bytes of the SHA2_256 digest fold into a bigint that
        -- can be aggregated, bytes 9-12 give an independent int for CHECKSUM_AGG. Row count + sum
        -- + checksum aggregate is commutative, so row order does not matter, but changed values
        -- and added / removed / duplicated rows all move at least one of the three parts.
        SELECT @OutputHash = CONVERT(nvarchar(128), CONCAT(
                N'rows=', COUNT_BIG(*),
                N';sum=', CONVERT(nvarchar(48), ISNULL(SUM(CONVERT(decimal(38, 0), [h].[RowHash])), 0)),
                N';agg=', CONVERT(nvarchar(16), ISNULL(CHECKSUM_AGG([h].[RowFold]), 0))))
        FROM
        (
            SELECT
                CONVERT(bigint, SUBSTRING([d].[RowDigest], 1, 8)) AS [RowHash],
                CONVERT(int, SUBSTRING([d].[RowDigest], 9, 4)) AS [RowFold]
            FROM
            (
                SELECT HASHBYTES('SHA2_256', ISNULL((SELECT [t].* FOR XML RAW, BINARY BASE64), N'')) AS [RowDigest]
                FROM #aiopt_output AS [t]
            ) AS [d]
        ) AS [h];

        DROP TABLE #aiopt_output;

        SELECT @OutputHash AS [OutputHash];
        """;

    private static string BuildOrdered(string prefix, string inner) =>
        $"""
        -- Output fingerprint, order-sensitive (OutputVerificationMode.OrderedHash).
        -- The benchmark must be a single SELECT statement: its rows are materialised into a temp
        -- table so every column takes part in the hash without naming any of them. That also means
        -- every column the benchmark returns must have a name -- SELECT COUNT(*) needs an alias.
        SET NOCOUNT ON;

        IF OBJECT_ID('tempdb..#aiopt_output') IS NOT NULL DROP TABLE #aiopt_output;

        -- ROW_NUMBER() over a constant records the order the rows were materialised in. SQL Server
        -- does not formally guarantee that SELECT ... INTO preserves the inner ORDER BY, so the
        -- insert is forced serial with MAXDOP 1 to keep it stable; the wizard's determinism test is
        -- what actually proves this script usable on a given server.
        {prefix}SELECT
            ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [aiopt_ordinal],
            [aiopt_q].*
        INTO #aiopt_output
        FROM
        (
        {inner}
        ) AS [aiopt_q]
        OPTION (MAXDOP 1);

        DECLARE @OutputHash nvarchar(128);
        DECLARE @RowCount bigint;
        DECLARE @Digests nvarchar(max);

        SELECT @RowCount = COUNT_BIG(*) FROM #aiopt_output;

        -- Per-row digest as hex (style 2 renders varbinary without the 0x prefix), combined in
        -- ordinal order. STRING_AGG (SQL Server 2017+) truncates at 8000 bytes unless its input is
        -- already a max type, hence the CONVERT to nvarchar(max); the concatenation is hashed again
        -- so the fingerprint stays short no matter how many rows there are. HASHBYTES over
        -- nvarchar(max) needs SQL Server 2016 or newer.
        SELECT @Digests = STRING_AGG(CONVERT(nvarchar(max), [h].[RowDigestHex]), N'|')
                          WITHIN GROUP (ORDER BY [h].[Ordinal])
        FROM
        (
            SELECT
                [t].[aiopt_ordinal] AS [Ordinal],
                CONVERT(nvarchar(64), HASHBYTES('SHA2_256', ISNULL((SELECT [t].* FOR XML RAW, BINARY BASE64), N'')), 2) AS [RowDigestHex]
            FROM #aiopt_output AS [t]
        ) AS [h];

        SET @OutputHash = CONCAT(
            N'rows=', @RowCount,
            N';ordered=', CONVERT(nvarchar(64), HASHBYTES('SHA2_256', ISNULL(@Digests, N'')), 2));

        DROP TABLE #aiopt_output;

        SELECT @OutputHash AS [OutputHash];
        """;

    /// <summary>
    /// Emitted when the benchmark cannot be wrapped safely. It parses and returns the required
    /// shape, but a NULL fingerprint never matches itself, so the wizard's determinism test fails
    /// and the user is forced to write a real one instead of silently trusting nothing.
    /// </summary>
    private static string BuildManualPlaceholder(string reason) =>
        $"""
        -- No output fingerprint could be generated automatically: {SingleLine(reason)}.
        -- Replace this script with one that returns exactly one row and one column named
        -- [OutputHash]:
        --   1. materialise the rows the benchmark produces into a temp table
        --   2. hash each row -- (SELECT [t].* FOR XML RAW, BINARY BASE64) over the temp table
        --      alias covers every column without naming any of them
        --   3. combine the row hashes commutatively for UnorderedHash, or in an explicit ordinal
        --      order for OrderedHash, and hash the combination down to one value
        --   4. drop the temp table and SELECT the value AS [OutputHash]
        -- Until then a hypothesis cannot be proven to return the same rows as the baseline.
        SELECT CONVERT(nvarchar(128), NULL) AS [OutputHash];
        """;

    /// <summary>Keeps an interpolated reason inside the single-line SQL comment it is placed in.</summary>
    private static string SingleLine(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Trim();

    #endregion
}
