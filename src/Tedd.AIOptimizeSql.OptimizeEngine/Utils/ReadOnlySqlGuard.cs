using System.Text;
using System.Text.RegularExpressions;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Classifies T-SQL batches as read-only (safe to run against a production
/// database in analyze-only mode) or potentially modifying. Deliberately
/// conservative: anything not recognizably read-only is rejected.
/// </summary>
public static partial class ReadOnlySqlGuard
{
    /// <summary>Result of a read-only classification.</summary>
    public readonly record struct GuardResult(bool IsAllowed, string? Reason)
    {
        public static GuardResult Allowed() => new(true, null);
        public static GuardResult Blocked(string reason) => new(false, reason);
    }

    /// <summary>
    /// Keywords that are never allowed anywhere in analyze-only mode, checked on
    /// comment- and string-literal-stripped SQL with word boundaries.
    /// </summary>
    private static readonly string[] ForbiddenKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE",
        "CREATE", "ALTER", "DROP",
        "GRANT", "REVOKE", "DENY",
        "BACKUP", "RESTORE",
        "KILL", "SHUTDOWN", "RECONFIGURE",
        "WRITETEXT", "UPDATETEXT",
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE",
        "BULK",
        "XP_CMDSHELL", "SP_CONFIGURE", "SP_EXECUTESQL", "SP_ADDROLEMEMBER",
        "CHECKPOINT",
    ];

    /// <summary>
    /// System procedures that only read metadata and are safe to EXEC in analyze-only mode.
    /// </summary>
    private static readonly string[] AllowedExecProcedures =
    [
        "SP_HELP", "SP_HELPTEXT", "SP_HELPINDEX", "SP_HELPSTATS",
        "SP_SPACEUSED", "SP_HELPCONSTRAINT", "SP_HELPTRIGGER", "SP_WHO", "SP_WHO2",
        "SP_HELPDB", "SP_HELPFILE", "SP_HELPFILEGROUP", "SP_STATISTICS",
    ];

    /// <summary>
    /// DBCC commands that only read. Cache-clearing DBCC commands (DROPCLEANBUFFERS,
    /// FREEPROCCACHE) are intentionally excluded: they degrade production performance.
    /// </summary>
    private static readonly string[] AllowedDbccCommands =
    [
        "SHOW_STATISTICS", "SQLPERF", "USEROPTIONS", "TRACESTATUS", "INPUTBUFFER", "PROCCACHE",
    ];

    /// <summary>
    /// Statement-leading keywords that are considered read-only.
    /// </summary>
    private static readonly string[] AllowedLeadingKeywords =
    [
        "SELECT", "WITH", "DECLARE", "PRINT", "IF", "WHILE", "BEGIN", "END", "ELSE", "RETURN", "BREAK", "CONTINUE", "GO",
    ];

    /// <summary>
    /// Checks whether <paramref name="sql"/> only contains read-only statements.
    /// </summary>
    public static GuardResult Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return GuardResult.Blocked("Empty SQL.");

        var stripped = StripCommentsAndStrings(sql);

        // Forbidden keywords anywhere (word-boundary match on stripped text).
        foreach (var keyword in ForbiddenKeywords)
        {
            var m = WordMatch(stripped, keyword);
            if (m is not null)
                return GuardResult.Blocked(
                    $"Statement contains '{m}' which is not allowed in analyze-only mode. " +
                    "Only read-only statements (SELECT, DMV queries, estimated plans) are permitted.");
        }

        // SELECT ... INTO creates a table; block the INTO clause of SELECT statements.
        if (SelectIntoPattern().IsMatch(stripped))
            return GuardResult.Blocked("SELECT ... INTO creates a table and is not allowed in analyze-only mode.");

        // EXEC only for allowlisted read-only system procedures.
        foreach (Match execMatch in ExecPattern().Matches(stripped))
        {
            var procName = execMatch.Groups["proc"].Value.Trim('[', ']', '"').ToUpperInvariant();
            // Strip schema prefix (sys.sp_help → SP_HELP)
            var lastDot = procName.LastIndexOf('.');
            if (lastDot >= 0)
                procName = procName[(lastDot + 1)..].Trim('[', ']', '"');

            if (!AllowedExecProcedures.Contains(procName))
                return GuardResult.Blocked(
                    $"EXEC '{procName}' is not on the analyze-only allowlist. " +
                    $"Allowed read-only procedures: {string.Join(", ", AllowedExecProcedures.Select(p => p.ToLowerInvariant()))}.");
        }

        // DBCC only for allowlisted read-only commands.
        foreach (Match dbccMatch in DbccPattern().Matches(stripped))
        {
            var command = dbccMatch.Groups["cmd"].Value.ToUpperInvariant();
            if (!AllowedDbccCommands.Contains(command))
                return GuardResult.Blocked(
                    $"DBCC {command} is not allowed in analyze-only mode. " +
                    $"Allowed: {string.Join(", ", AllowedDbccCommands.Select(c => "DBCC " + c))}.");
        }

        // SET statements: allow harmless session options, block SHOWPLAN toggling
        // (tools manage SHOWPLAN themselves) and IDENTITY_INSERT/OFFSETS.
        foreach (Match setMatch in SetPattern().Matches(stripped))
        {
            var option = setMatch.Groups["opt"].Value.ToUpperInvariant();
            if (option.StartsWith("SHOWPLAN", StringComparison.Ordinal))
                return GuardResult.Blocked("SET SHOWPLAN_* is managed by the execution-plan tool and cannot be toggled directly.");
            if (option is "IDENTITY_INSERT" or "OFFSETS")
                return GuardResult.Blocked($"SET {option} is not allowed in analyze-only mode.");
        }

        // Every statement must start with an allowed keyword. Split on
        // semicolons and GO batch separators, inspect the first word.
        foreach (var statement in SplitStatements(stripped))
        {
            var firstWord = FirstWord(statement);
            if (firstWord.Length == 0)
                continue;

            var upper = firstWord.ToUpperInvariant();
            if (upper is "EXEC" or "EXECUTE" or "DBCC" or "SET" or "USE")
                continue; // validated above / harmless (USE only switches catalog)

            if (!AllowedLeadingKeywords.Contains(upper))
                return GuardResult.Blocked(
                    $"Statement starting with '{firstWord}' is not recognized as read-only. " +
                    "Only SELECT/CTE queries, DECLARE, PRINT, control flow, allowlisted EXEC/DBCC, and session SET options are permitted in analyze-only mode.");
        }

        return GuardResult.Allowed();
    }

    /// <summary>
    /// Removes comments (<c>--</c> and <c>/* */</c>) and collapses string
    /// literals (<c>'...'</c>) and quoted identifiers so keyword scanning cannot
    /// be fooled by text inside literals.
    /// </summary>
    internal static string StripCommentsAndStrings(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            // Line comment
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            // Block comment (nested per T-SQL rules)
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i += 2; continue; }
                    if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { depth--; i += 2; continue; }
                    i++;
                }
                sb.Append(' ');
                continue;
            }

            // String literal (with '' escaping)
            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append("''");
                continue;
            }

            // Bracketed identifier — keep content (object names matter for EXEC detection)
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string? WordMatch(string text, string keyword)
    {
        var m = Regex.Match(text, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Value : null;
    }

    private static IEnumerable<string> SplitStatements(string stripped)
    {
        var parts = Regex.Split(stripped, @"(?:;|^\s*GO\s*$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                yield return trimmed;
        }
    }

    private static string FirstWord(string statement)
    {
        var m = Regex.Match(statement, @"[A-Za-z_][A-Za-z0-9_]*");
        return m.Success ? m.Value : "";
    }

    [GeneratedRegex(@"\bSELECT\b(?:(?!\bFROM\b).)*?\bINTO\b\s+(?!#)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SelectIntoPattern();

    [GeneratedRegex(@"\bEXEC(?:UTE)?\b\s+(?<proc>[\[\]\""A-Za-z0-9_.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecPattern();

    [GeneratedRegex(@"\bDBCC\b\s+(?<cmd>[A-Za-z_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DbccPattern();

    [GeneratedRegex(@"\bSET\b\s+(?<opt>[A-Za-z_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SetPattern();
}
