using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>What a conversation is being started for.</summary>
public sealed record AiConversationStart
{
    public required AiConversationKind Kind { get; init; }

    /// <summary>The AI connection about to be used. Null means "no AI configured"; nothing is tracked.</summary>
    public AIConnection? AiConnection { get; init; }

    public DatabaseConnectionId? DatabaseConnectionId { get; init; }

    /// <summary>Short label shown in the token usage list, e.g. the analysis or experiment name.</summary>
    public string? Title { get; init; }

    public int? RelatedDatabaseAnalysisId { get; init; }
    public int? RelatedExperimentId { get; init; }
    public int? RelatedResearchIterationId { get; init; }
    public int? RelatedHypothesisId { get; init; }
}

/// <summary>
/// Opens and closes <see cref="AiConversation"/> ledger rows. The conversation itself lives in
/// the agent harness's own session (message history, tool calls, continuations); this only
/// records who talked to which model, for what, and what it cost.
/// </summary>
/// <remarks>
/// Every method swallows its own failures: a broken usage ledger must never take down the
/// analysis or experiment that was being measured.
/// </remarks>
public sealed class AiConversationTracker(IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AiConversationTracker>();

    /// <summary>
    /// Opens a conversation row. Returns a handle that is safe to use unconditionally —
    /// when there is no AI connection, or the insert fails, the handle simply records nothing.
    /// </summary>
    public async Task<AiConversationHandle> StartAsync(AiConversationStart start, CancellationToken ct)
    {
        if (start.AiConnection is null)
            return new AiConversationHandle(this, null);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;
            var row = new AiConversation
            {
                Id = AiConversationId.Transient,
                Kind = start.Kind,
                State = AiConversationState.Running,
                DatabaseConnectionId = start.DatabaseConnectionId,
                AIConnectionId = start.AiConnection.Id,
                Provider = start.AiConnection.Provider,
                Model = start.AiConnection.Model,
                Title = Truncate(start.Title, 512),
                RelatedDatabaseAnalysisId = start.RelatedDatabaseAnalysisId,
                RelatedExperimentId = start.RelatedExperimentId,
                RelatedResearchIterationId = start.RelatedResearchIterationId,
                RelatedHypothesisId = start.RelatedHypothesisId,
                StartedAt = now,
                CreatedAt = now,
                ModifiedAt = now,
            };
            db.AiConversations.Add(row);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            return new AiConversationHandle(this, row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open an AI conversation ledger row for {Kind}", start.Kind);
            return new AiConversationHandle(this, null);
        }
    }

    internal async Task FlushAsync(
        AiConversationId id,
        AiConversationState state,
        int requestCount,
        long inputTokens,
        long outputTokens,
        long totalTokens,
        long elapsedMs,
        string? lastMessage,
        bool ended,
        CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;
            await db.AiConversations
                .Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.State, state)
                    .SetProperty(c => c.RequestCount, requestCount)
                    .SetProperty(c => c.InputTokens, inputTokens)
                    .SetProperty(c => c.OutputTokens, outputTokens)
                    .SetProperty(c => c.TotalTokens, totalTokens)
                    .SetProperty(c => c.ElapsedMs, elapsedMs)
                    .SetProperty(c => c.LastMessage, lastMessage)
                    .SetProperty(c => c.EndedAt, c => ended ? now : c.EndedAt)
                    .SetProperty(c => c.ModifiedAt, now), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update AI conversation ledger row {ConversationId}", id);
        }
    }

    internal static string? Truncate(string? value, int maxChars) =>
        value is null || value.Length <= maxChars ? value : value[..maxChars];
}

/// <summary>
/// Accumulates the token usage of one conversation and writes it back. Not thread-safe:
/// one handle belongs to one sequential agent conversation.
/// </summary>
public sealed class AiConversationHandle
{
    private readonly AiConversationTracker _tracker;
    private readonly AiConversationId? _id;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    internal AiConversationHandle(AiConversationTracker tracker, AiConversationId? id)
    {
        _tracker = tracker;
        _id = id;
    }

    /// <summary>True when nothing is being recorded (no AI connection, or the row could not be opened).</summary>
    public bool IsNoOp => _id is null;

    public int RequestCount { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long TotalTokens { get; private set; }

    /// <summary>
    /// Adds one agent request's usage. Counts the request even when the provider reported no
    /// usage at all, so a model that hides its token counts still shows up as activity.
    /// </summary>
    public void Record(UsageDetails? usage)
    {
        if (_id is null)
            return;

        RequestCount++;
        if (usage is null)
            return;

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;
        InputTokens += input;
        OutputTokens += output;

        // Providers that bill reasoning or cached tokens separately report a total larger than
        // input + output, so prefer their number and only synthesize one when it is missing.
        TotalTokens += usage.TotalTokenCount ?? (input + output);
    }

    public Task CompleteAsync(CancellationToken ct = default) =>
        FinishAsync(AiConversationState.Completed, lastMessage: null, ct);

    public Task FailAsync(string message, CancellationToken ct = default) =>
        FinishAsync(AiConversationState.Failed, message, ct);

    private Task FinishAsync(AiConversationState state, string? lastMessage, CancellationToken ct)
    {
        if (_id is null)
            return Task.CompletedTask;

        _stopwatch.Stop();
        return _tracker.FlushAsync(
            _id.Value, state, RequestCount, InputTokens, OutputTokens, TotalTokens,
            _stopwatch.ElapsedMilliseconds, AiConversationTracker.Truncate(lastMessage, 4000),
            ended: true, ct);
    }
}
