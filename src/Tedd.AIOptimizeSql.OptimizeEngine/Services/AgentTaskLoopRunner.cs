using System.Diagnostics;
using System.Text;

using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Drives an AI agent through its task list: runs the agent, then keeps asking
/// it to continue (in the same session, so context is preserved) while its plan
/// has Pending/InProgress tasks or its final answer is not yet acceptable —
/// up to <see cref="OptimizeEngineSettings.MaxAgentContinuations"/> runs.
/// </summary>
public sealed class AgentTaskLoopRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<OptimizeEngineSettings> settings,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentTaskLoopRunner>();

    /// <summary>Outcome of a full task-loop run.</summary>
    public sealed record LoopResult(string? LastResponse, int RunsUsed, int OpenTaskCount, long ElapsedMs);

    /// <summary>
    /// Runs <paramref name="agent"/> with <paramref name="initialPrompt"/>, continuing
    /// in the same session until the task list is solved and
    /// <paramref name="isResponseAcceptable"/> approves the latest response, or the
    /// continuation limit is reached, or <paramref name="shouldAbort"/> returns true.
    /// </summary>
    /// <param name="conversation">
    /// Optional usage ledger. Every run in the loop is one request against the model, so the
    /// whole loop — initial prompt plus continuations — is recorded as a single conversation.
    /// </param>
    public async Task<LoopResult> RunAsync(
        AIAgent agent,
        string initialPrompt,
        AgentTaskScope scope,
        Func<string?, bool>? isResponseAcceptable = null,
        Func<CancellationToken, Task<bool>>? shouldAbort = null,
        Func<string, CancellationToken, Task>? log = null,
        AiConversationHandle? conversation = null,
        CancellationToken cancellationToken = default)
    {
        var maxRuns = Math.Clamp(settings.Value.MaxAgentContinuations, 1, 100);
        isResponseAcceptable ??= _ => true;

        var session = await agent.CreateSessionAsync(cancellationToken);

        string? lastResponse = null;
        var openTasks = new List<AgentTask>();
        var sw = Stopwatch.StartNew();
        var run = 0;

        while (run < maxRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run++;

            var prompt = run == 1
                ? initialPrompt
                : BuildContinuationPrompt(openTasks, run, maxRuns, isResponseAcceptable(lastResponse));

            if (run > 1 && log is not null)
                await log($"Continuation {run}/{maxRuns}: {openTasks.Count} open task(s) remain — asking agent to continue.", cancellationToken);

            var result = await agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
            lastResponse = result?.ToString();
            conversation?.Record(result?.Usage);

            openTasks = await GetOpenTasksAsync(scope, cancellationToken);
            var acceptable = isResponseAcceptable(lastResponse);

            _logger.LogInformation(
                "Agent run {Run}/{Max} finished: {Open} open tasks, response acceptable: {Acceptable}",
                run, maxRuns, openTasks.Count, acceptable);

            if (!ShouldContinue(openTasks.Count, acceptable, run, maxRuns))
                break;

            if (shouldAbort is not null && await shouldAbort(cancellationToken))
            {
                if (log is not null)
                    await log($"Task loop aborted after run {run}/{maxRuns} (stop requested).", cancellationToken);
                break;
            }
        }

        sw.Stop();

        if (openTasks.Count > 0 && log is not null)
            await log(
                $"Task loop ended after {run}/{maxRuns} run(s) with {openTasks.Count} unfinished task(s):\n{AgentTaskToolWrapper.FormatTaskList(openTasks)}",
                cancellationToken);

        return new LoopResult(lastResponse, run, openTasks.Count, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Continue while unfinished tasks remain or the latest response is not acceptable,
    /// as long as more runs are allowed.
    /// </summary>
    internal static bool ShouldContinue(int openTaskCount, bool responseAcceptable, int runsUsed, int maxRuns) =>
        runsUsed < maxRuns && (openTaskCount > 0 || !responseAcceptable);

    internal static string BuildContinuationPrompt(
        IReadOnlyList<AgentTask> openTasks, int run, int maxRuns, bool lastResponseAcceptable)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Continue your work (run {run} of at most {maxRuns}).");
        sb.AppendLine();

        if (openTasks.Count > 0)
        {
            sb.AppendLine("Your task list still has unfinished items:");
            sb.AppendLine();
            sb.AppendLine(AgentTaskToolWrapper.FormatTaskList(openTasks));
            sb.AppendLine();
            sb.AppendLine("Work through the remaining tasks now. Mark each task in_progress when you start it and completed when you finish it (UpdateTask). Cancel tasks that are no longer relevant, and add new tasks if you have discovered additional work.");
        }
        else
        {
            sb.AppendLine("Your task list is complete.");
        }

        if (!lastResponseAcceptable)
        {
            sb.AppendLine();
            sb.AppendLine("Your previous response did not match the required response format. When all tasks are done, reply with the required final response format exactly as specified in your instructions.");
        }

        if (run == maxRuns)
        {
            sb.AppendLine();
            sb.AppendLine("This is the FINAL run. Wrap up: complete or cancel the remaining tasks and produce your final response now.");
        }

        return sb.ToString();
    }

    private async Task<List<AgentTask>> GetOpenTasksAsync(AgentTaskScope scope, CancellationToken ct)
    {
        using var serviceScope = scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var all = await AgentTaskToolWrapper.LoadTasksAsync(db, scope, ct);
        return all.Where(t => t.Status is AgentTaskStatus.Pending or AgentTaskStatus.InProgress).ToList();
    }
}
