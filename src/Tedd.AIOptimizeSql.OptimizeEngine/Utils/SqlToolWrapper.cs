using System.ComponentModel;
using System.Data.Common;
using System.Text;

using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.OptimizeEngine;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Wraps <see cref="IDatabaseExecutor"/> operations as methods that can be
/// exposed to an AI agent via <c>AIFunctionFactory.Create</c>.
/// Each method returns a string result truncated to <see cref="_maxResponseBytes"/>.
/// </summary>
public sealed class SqlToolWrapper : IDisposable
{
    private readonly IDatabaseExecutor _executor;
    private readonly DbConnection _connection;
    private readonly int _maxResponseBytes;
    private readonly ILogger _logger;
    private readonly bool _readOnly;

    public SqlToolWrapper(IDatabaseExecutor executor, DbConnection connection, int maxResponseBytes, ILogger logger, bool readOnly = false)
    {
        _executor = executor;
        _connection = connection;
        _maxResponseBytes = maxResponseBytes;
        _logger = logger;
        _readOnly = readOnly;
    }

    [Description("Executes a SQL query and returns the result rows as a text table. Use this to inspect data, schemas, execution plans, or any SELECT-based query.")]
    public string ExecuteSqlQuery([Description("The SQL query to execute")] string sql)
    {
        _logger.LogDebug("AI tool: ExecuteSqlQuery called with: {Sql}", sql);
        if (_readOnly)
        {
            var guard = ReadOnlySqlGuard.Check(sql);
            if (!guard.IsAllowed)
                return $"BLOCKED (analyze-only mode): {guard.Reason}";
        }
        try
        {
            var rows = _executor.ExecuteQuery(_connection, sql);
            if (rows.Count == 0)
                return "(no rows returned)";

            var sb = new StringBuilder();
            var columns = rows[0].Keys.ToList();
            sb.AppendLine(string.Join("\t", columns));
            sb.AppendLine(new string('-', columns.Count * 16));

            foreach (var row in rows)
            {
                if (sb.Length >= _maxResponseBytes)
                {
                    sb.AppendLine($"\n... truncated at {_maxResponseBytes} bytes ({rows.Count} total rows) ...");
                    break;
                }
                sb.AppendLine(string.Join("\t", columns.Select(c => row.GetValueOrDefault(c, "NULL"))));
            }

            return Truncate(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: ExecuteSqlQuery failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Executes a SQL statement that does not return rows (DDL, DML such as CREATE INDEX, ALTER TABLE, UPDATE, INSERT, DELETE). Returns a confirmation message.")]
    public string ExecuteSqlNonQuery([Description("The SQL statement to execute (DDL/DML)")] string sql)
    {
        _logger.LogDebug("AI tool: ExecuteSqlNonQuery called with: {Sql}", sql);
        if (_readOnly)
        {
            return "BLOCKED (analyze-only mode): this connection is analyze-only. DDL/DML is never executed here. " +
                   "Use ExecuteSqlQuery for read-only inspection or GetExecutionPlan to evaluate a change hypothetically, " +
                   "and put proposed changes in your findings/recommendations instead.";
        }
        try
        {
            _executor.ExecuteNonQuery(_connection, sql);
            return "Statement executed successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: ExecuteSqlNonQuery failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Gets the estimated execution plan for a SQL query in XML format without executing it. Use this to analyze query performance, identify bottlenecks, or evaluate how a proposed query rewrite would behave. The statement is only compiled, never executed.")]
    public string GetExecutionPlan([Description("The SQL query to get the execution plan for")] string sql)
    {
        _logger.LogDebug("AI tool: GetExecutionPlan called with: {Sql}", sql);
        if (_readOnly)
        {
            // SET SHOWPLAN_XML ON compiles without executing, so even DML/DDL is
            // safe to plan — as long as the batch cannot toggle SHOWPLAN back off
            // or smuggle in extra GO-separated batches.
            if (sql.Contains("SHOWPLAN", StringComparison.OrdinalIgnoreCase))
                return "BLOCKED (analyze-only mode): the SQL must not reference SHOWPLAN options; they are managed by this tool.";
            if (System.Text.RegularExpressions.Regex.IsMatch(sql, @"^\s*GO\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline))
                return "BLOCKED (analyze-only mode): multi-batch scripts (GO) are not allowed in the execution plan tool.";
        }
        try
        {
            _executor.ExecuteNonQuery(_connection, "SET SHOWPLAN_XML ON");
            try
            {
                var plan = _executor.ExecuteScalar(_connection, sql);
                return Truncate(plan);
            }
            finally
            {
                _executor.ExecuteNonQuery(_connection, "SET SHOWPLAN_XML OFF");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: GetExecutionPlan failed");
            return $"ERROR: {ex.Message}";
        }
    }

    private string Truncate(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= _maxResponseBytes)
            return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var truncated = Encoding.UTF8.GetString(bytes, 0, _maxResponseBytes);
        return truncated + $"\n... truncated at {_maxResponseBytes} bytes ...";
    }

    public void Dispose() => _connection.Dispose();
}
