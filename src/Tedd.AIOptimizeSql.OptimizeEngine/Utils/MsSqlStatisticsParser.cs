using System.Text.RegularExpressions;

using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Parses SET STATISTICS TIME / IO output from SQL Server
/// <see cref="Microsoft.Data.SqlClient.SqlConnection"/> info messages.
/// </summary>
internal static class MsSqlStatisticsParser
{
    private static readonly Regex ExecutionTimeRegex = new(
        @"SQL Server Execution Times:\s*CPU time = (\d+) ms,\s*elapsed time = (\d+) ms",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ParseCompileTimeRegex = new(
        @"SQL Server parse and compile time:\s*CPU time = (\d+) ms,\s*elapsed time = (\d+) ms",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IoStatsRegex = new(
        @"Table '([^']+)'\.?\s*" +
        @"Scan count (\d+),\s*" +
        @"logical reads (\d+),\s*" +
        @"physical reads (\d+),\s*" +
        @"page server reads (\d+),\s*" +
        @"read-ahead reads (\d+),\s*" +
        @"page server read-ahead reads (\d+),\s*" +
        @"lob logical reads (\d+),\s*" +
        @"lob physical reads (\d+),\s*" +
        @"lob page server reads (\d+),\s*" +
        @"lob read-ahead reads (\d+),\s*" +
        @"lob page server read-ahead reads (\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RowsAffectedRegex = new(
        @"\((\d+) rows? affected\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses all recognised statistics patterns in <paramref name="message"/>
    /// and accumulates values into <paramref name="result"/>.
    /// </summary>
    public static void AccumulateFromMessage(string message, SqlExecutionResult result)
    {
        AccumulateExecutionTimes(message, result);
        AccumulateParseCompileTimes(message, result);
        AccumulateIoStats(message, result);
        AccumulateRowsAffected(message, result);
    }

    internal static void AccumulateExecutionTimes(string message, SqlExecutionResult result)
    {
        foreach (Match match in ExecutionTimeRegex.Matches(message))
        {
            result.ExecutionCpuTimeMs += int.Parse(match.Groups[1].Value);
            result.ExecutionElapsedTimeMs += int.Parse(match.Groups[2].Value);
        }
    }

    internal static void AccumulateParseCompileTimes(string message, SqlExecutionResult result)
    {
        foreach (Match match in ParseCompileTimeRegex.Matches(message))
        {
            result.ParseAndCompileCpuTimeMs += int.Parse(match.Groups[1].Value);
            result.ParseAndCompileElapsedTimeMs += int.Parse(match.Groups[2].Value);
        }
    }

    internal static void AccumulateIoStats(string message, SqlExecutionResult result)
    {
        foreach (Match match in IoStatsRegex.Matches(message))
        {
            var tableIo = new TableIoStatistics
            {
                TableName = match.Groups[1].Value,
                ScanCount = int.Parse(match.Groups[2].Value),
                LogicalReads = int.Parse(match.Groups[3].Value),
                PhysicalReads = int.Parse(match.Groups[4].Value),
                PageServerReads = int.Parse(match.Groups[5].Value),
                ReadAheadReads = int.Parse(match.Groups[6].Value),
                PageServerReadAheadReads = int.Parse(match.Groups[7].Value),
                LobLogicalReads = int.Parse(match.Groups[8].Value),
                LobPhysicalReads = int.Parse(match.Groups[9].Value),
                LobPageServerReads = int.Parse(match.Groups[10].Value),
                LobReadAheadReads = int.Parse(match.Groups[11].Value),
                LobPageServerReadAheadReads = int.Parse(match.Groups[12].Value),
            };
            result.TableIoStats.Add(tableIo);

            result.TotalScanCount += tableIo.ScanCount;
            result.TotalLogicalReads += tableIo.LogicalReads;
            result.TotalPhysicalReads += tableIo.PhysicalReads;
            result.TotalPageServerReads += tableIo.PageServerReads;
            result.TotalReadAheadReads += tableIo.ReadAheadReads;
            result.TotalPageServerReadAheadReads += tableIo.PageServerReadAheadReads;
            result.TotalLobLogicalReads += tableIo.LobLogicalReads;
            result.TotalLobPhysicalReads += tableIo.LobPhysicalReads;
            result.TotalLobPageServerReads += tableIo.LobPageServerReads;
            result.TotalLobReadAheadReads += tableIo.LobReadAheadReads;
            result.TotalLobPageServerReadAheadReads += tableIo.LobPageServerReadAheadReads;
        }
    }

    internal static void AccumulateRowsAffected(string message, SqlExecutionResult result)
    {
        foreach (Match match in RowsAffectedRegex.Matches(message))
        {
            result.RowsAffected.Add(int.Parse(match.Groups[1].Value));
        }
    }
}
