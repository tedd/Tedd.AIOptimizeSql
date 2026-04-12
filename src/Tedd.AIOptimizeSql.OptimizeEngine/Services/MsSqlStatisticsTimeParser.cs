using System.Text.RegularExpressions;

using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Parses SET STATISTICS TIME lines from SQL Server <see cref="Microsoft.Data.SqlClient.SqlConnection"/> info messages.
/// </summary>
internal static class MsSqlStatisticsTimeParser
{
    private static readonly Regex TimingRegex = new(
        @"SQL Server Execution Times:\s*CPU time = (\d+) ms,\s*elapsed time = (\d+) ms",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Adds CPU and elapsed milliseconds from all matches in <paramref name="message"/> to <paramref name="result"/>.
    /// </summary>
    public static void AccumulateFromMessage(string message, SqlTimingResult result)
    {
        foreach (Match match in TimingRegex.Matches(message))
        {
            result.CpuTimeMs += int.Parse(match.Groups[1].Value);
            result.ElapsedTimeMs += int.Parse(match.Groups[2].Value);
        }
    }
}
