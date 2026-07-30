using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Executes query-window SQL against a target SQL Server the way an admin tool does:
/// GO-separated batches on one connection, partial output preserved when the server
/// raises an error, and server errors reported on the result instead of thrown.
/// </summary>
public sealed class AdHocQueryService(ILogger<AdHocQueryService> logger) : IAdHocQueryService
{
    /// <summary>Column name SQL Server uses for showplan result sets, unchanged since 2005.</summary>
    private const string ShowplanColumnName = "Microsoft SQL Server 2005 XML Showplan";

    private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const string DateTimeOffsetFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    /// <summary>Bytes of a binary value rendered before the display string is elided.</summary>
    private const int MaxBinaryDisplayBytes = 64;

    private const int ConnectionTestTimeout = 15;

    /// <inheritdoc />
    public async Task<AdHocQueryResult> ExecuteAsync(
        string connectionString, AdHocQueryRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (request.ReadOnlyOnly)
        {
            var guard = ReadOnlySqlGuard.Check(request.Sql);
            if (!guard.IsAllowed)
            {
                logger.LogInformation("Ad-hoc SQL rejected by the analyze-only guard: {Reason}", guard.Reason);
                return new AdHocQueryResult
                {
                    Success = false,
                    BlockedByReadOnlyGuard = true,
                    ErrorMessage = guard.Reason,
                    RowsAffected = -1,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                };
            }
        }

        var batches = MsSqlExecutor.SplitOnGo(request.Sql);
        if (batches.Count == 0)
        {
            return new AdHocQueryResult
            {
                Success = true,
                RowsAffected = -1,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }

        var batchLineOffsets = ComputeBatchLineOffsets(request.Sql, batches);
        var maxRows = request.MaxRows > 0 ? request.MaxRows : int.MaxValue;

        // Estimated wins when both are asked for: the server cannot produce an actual plan
        // for statements it never runs.
        var planOption = request.EstimatedPlanOnly ? "SHOWPLAN_XML"
            : request.IncludeActualPlan ? "STATISTICS XML"
            : null;

        var resultSets = new List<AdHocResultSet>();
        var planXml = new List<string>();
        var messages = new List<string>();
        var messageLock = new object();

        var rowsAffected = 0;
        var anyRowsAffected = false;
        var success = true;
        string? errorMessage = null;
        int? errorLineNumber = null;

        // Which batch the reader is on, so a server line number can be mapped back onto the
        // submitted text when the batch throws.
        var currentBatch = -1;

        // The provider raises InfoMessage from whichever thread drains the TDS stream, which
        // need not be the thread resuming the reader loop.
        void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            lock (messageLock)
                messages.Add(e.Message);
        }

        await using (var conn = new SqlConnection(connectionString))
        {
            conn.InfoMessage += OnInfoMessage;
            try
            {
                await conn.OpenAsync(ct);

                if (planOption is not null)
                {
                    using var setOn = new SqlCommand($"SET {planOption} ON;", conn)
                    {
                        CommandTimeout = request.CommandTimeoutSeconds
                    };
                    await setOn.ExecuteNonQueryAsync(ct);
                }

                for (currentBatch = 0; currentBatch < batches.Count; currentBatch++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var cmd = new SqlCommand(batches[currentBatch], conn)
                    {
                        CommandTimeout = request.CommandTimeoutSeconds
                    };
                    using var reader = await cmd.ExecuteReaderAsync(ct);

                    do
                    {
                        if (reader.FieldCount > 0)
                        {
                            if (IsShowplanResult(reader, request.EstimatedPlanOnly))
                                await ReadShowplanAsync(reader, planXml, ct);
                            else
                                resultSets.Add(await ReadResultSetAsync(reader, currentBatch, maxRows, ct));
                        }
                    }
                    while (await reader.NextResultAsync(ct));

                    // RecordsAffected is only complete once the reader is closed.
                    await reader.CloseAsync();
                    if (reader.RecordsAffected > 0)
                    {
                        rowsAffected += reader.RecordsAffected;
                        anyRowsAffected = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                success = false;
                errorMessage = "Cancelled.";
                logger.LogDebug("Ad-hoc query cancelled after {Elapsed} ms", stopwatch.ElapsedMilliseconds);
            }
            catch (SqlException ex)
            {
                success = false;
                if (ct.IsCancellationRequested)
                {
                    // A token-driven abort surfaces as a SqlException, not as an OperationCanceledException.
                    errorMessage = "Cancelled.";
                    logger.LogDebug("Ad-hoc query cancelled after {Elapsed} ms", stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    errorMessage = FormatSqlException(ex);
                    errorLineNumber = ResolveErrorLine(ex, batchLineOffsets, currentBatch);
                    logger.LogInformation(
                        "Ad-hoc query failed: Msg {Number}, Level {Class}, State {State}: {Message}",
                        ex.Number, ex.Class, ex.State, ex.Message);
                }
            }
            catch (Exception ex)
            {
                success = false;
                errorMessage = ex.Message;
                logger.LogError(ex, "Ad-hoc query failed outside SQL execution");
            }
            finally
            {
                conn.InfoMessage -= OnInfoMessage;
                if (planOption is not null && conn.State == ConnectionState.Open)
                    await TryDisablePlanAsync(conn, planOption, request.CommandTimeoutSeconds);
            }
        }

        stopwatch.Stop();

        return new AdHocQueryResult
        {
            Success = success,
            ResultSets = resultSets,
            Messages = messages,
            PlanXml = planXml,
            RowsAffected = anyRowsAffected ? rowsAffected : -1,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            ErrorMessage = errorMessage,
            ErrorLineNumber = errorLineNumber
        };
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string? ServerName, string? DatabaseName, string? Error)> TestConnectionAsync(
        string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            string? serverName;
            string? databaseName;

            using (var cmd = new SqlCommand("SELECT @@SERVERNAME, DB_NAME();", conn)
            {
                CommandTimeout = ConnectionTestTimeout
            })
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    return (false, null, null, "The server returned no row for @@SERVERNAME / DB_NAME().");

                serverName = reader.IsDBNull(0) ? null : reader.GetString(0);
                databaseName = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            await conn.CloseAsync();
            return (true, serverName, databaseName, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, null, "Cancelled.");
        }
        catch (SqlException ex)
        {
            logger.LogInformation("Connection test failed: {Message}", ex.Message);
            return (false, null, null, FormatSqlException(ex));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Connection test failed");
            return (false, null, null, ex.Message);
        }
    }

    #region Reading

    /// <summary>
    /// A showplan result set is a single XML column with a fixed name. Under
    /// <c>SHOWPLAN_XML</c> nothing else can come back, so any single column is a plan there.
    /// </summary>
    private static bool IsShowplanResult(SqlDataReader reader, bool estimatedPlanOnly) =>
        reader.FieldCount == 1
        && (estimatedPlanOnly || string.Equals(reader.GetName(0), ShowplanColumnName, StringComparison.Ordinal));

    private static async Task ReadShowplanAsync(SqlDataReader reader, List<string> planXml, CancellationToken ct)
    {
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0))
                continue;

            var xml = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(xml))
                planXml.Add(xml);
        }
    }

    private static async Task<AdHocResultSet> ReadResultSetAsync(
        SqlDataReader reader, int batchIndex, int maxRows, CancellationToken ct)
    {
        var fieldCount = reader.FieldCount;

        var columns = new List<AdHocColumn>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            columns.Add(new AdHocColumn
            {
                Name = reader.GetName(i),
                ClrTypeName = reader.GetFieldType(i)?.Name ?? "Object"
            });
        }

        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;

        while (await reader.ReadAsync(ct))
        {
            // One read past the cap tells us whether anything was actually left behind.
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            var row = new string?[fieldCount];
            for (var i = 0; i < fieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : FormatValue(reader.GetValue(i));

            rows.Add(row);
        }

        return new AdHocResultSet
        {
            Columns = columns,
            Rows = rows,
            Truncated = truncated,
            BatchIndex = batchIndex
        };
    }

    /// <summary>
    /// Renders a value for the grid. SQL NULL is the caller's responsibility -- everything
    /// arriving here is non-null, so a <c>null</c> return never happens.
    /// </summary>
    private static string FormatValue(object value) => value switch
    {
        byte[] bytes => FormatBinary(bytes),
        DateTime dateTime => dateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static string FormatBinary(byte[] bytes)
    {
        var shown = Math.Min(bytes.Length, MaxBinaryDisplayBytes);
        var hex = Convert.ToHexString(bytes, 0, shown);
        return bytes.Length > shown ? $"0x{hex}..." : $"0x{hex}";
    }

    #endregion

    #region Errors

    /// <summary>
    /// Formats a server error the way SSMS's message pane reads, one header line plus the
    /// message text per error in the collection.
    /// </summary>
    private static string FormatSqlException(SqlException ex)
    {
        var sb = new StringBuilder();

        foreach (SqlError error in ex.Errors)
        {
            if (sb.Length > 0)
                sb.AppendLine();

            sb.Append($"Msg {error.Number}, Level {error.Class}, State {error.State}");
            if (!string.IsNullOrEmpty(error.Procedure))
                sb.Append($", Procedure {error.Procedure}");
            if (error.LineNumber > 0)
                sb.Append($", Line {error.LineNumber}");

            sb.AppendLine();
            sb.Append(error.Message);
        }

        return sb.Length > 0 ? sb.ToString() : ex.Message;
    }

    /// <summary>
    /// The server counts lines from the start of the failing batch; the editor needs a line
    /// in the text the user submitted, so the batch's own offset is added back. The message
    /// text keeps the server's numbering because that is what SSMS shows.
    /// </summary>
    private static int? ResolveErrorLine(SqlException ex, IReadOnlyList<int> batchLineOffsets, int batchIndex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (error.LineNumber <= 0)
                continue;

            var offset = batchIndex >= 0 && batchIndex < batchLineOffsets.Count
                ? batchLineOffsets[batchIndex]
                : 0;
            return error.LineNumber + offset;
        }

        return null;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Lines preceding each batch in the original text. <c>SplitOnGo</c> only trims, so every
    /// batch is still a verbatim substring and can be located by a forward scan.
    /// </summary>
    private static IReadOnlyList<int> ComputeBatchLineOffsets(string sql, IReadOnlyList<string> batches)
    {
        var offsets = new int[batches.Count];
        var cursor = 0;
        var lines = 0;

        for (var i = 0; i < batches.Count; i++)
        {
            var start = sql.IndexOf(batches[i], cursor, StringComparison.Ordinal);
            if (start < 0)
                continue; // leaves this batch at offset 0, i.e. the raw server line number

            lines += CountNewlines(sql, cursor, start);
            offsets[i] = lines;

            var end = start + batches[i].Length;
            lines += CountNewlines(sql, start, end);
            cursor = end;
        }

        return offsets;
    }

    private static int CountNewlines(string text, int start, int end) =>
        end > start ? text.AsSpan(start, end - start).Count('\n') : 0;

    /// <summary>
    /// Best-effort <c>SET ... OFF</c>. Failing here must not mask the real error, and the
    /// pooled connection is reset by <c>sp_reset_connection</c> before anyone reuses it.
    /// </summary>
    private async Task TryDisablePlanAsync(SqlConnection conn, string planOption, int commandTimeoutSeconds)
    {
        try
        {
            using var cmd = new SqlCommand($"SET {planOption} OFF;", conn)
            {
                CommandTimeout = commandTimeoutSeconds
            };
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not turn {PlanOption} off before closing the connection", planOption);
        }
    }

    #endregion
}
