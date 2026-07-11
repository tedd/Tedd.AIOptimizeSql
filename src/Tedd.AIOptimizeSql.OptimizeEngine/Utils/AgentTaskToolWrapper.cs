using System.ComponentModel;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Identifies which run an agent task list belongs to: a database analysis or a hypothesis.
/// </summary>
public readonly record struct AgentTaskScope(DatabaseAnalysisId? AnalysisId, HypothesisId? HypothesisId)
{
    public static AgentTaskScope ForAnalysis(DatabaseAnalysisId id) => new(id, null);
    public static AgentTaskScope ForHypothesis(HypothesisId id) => new(null, id);
}

/// <summary>
/// Task list tools exposed to AI agents via <c>AIFunctionFactory.Create</c>.
/// The agent plans its work as tasks, keeps statuses updated while working, and
/// may add or cancel tasks as it learns more. The list is stored in the
/// application database and shown live to the user; the continuation loop keeps
/// re-invoking the agent until no Pending/InProgress tasks remain (or the
/// configured limit is reached).
/// </summary>
public sealed class AgentTaskToolWrapper(
    AgentTaskScope scope,
    IServiceScopeFactory scopeFactory,
    ILogger logger)
{
    [Description("Adds a task to your work plan. Create your plan with this at the start of the run, and add new tasks later when you discover additional work. Returns the task id.")]
    public async Task<string> AddTask(
        [Description("Short imperative task title, e.g. 'Review top 5 stored procedures'")] string title,
        [Description("Optional details about what the task involves")] string? notes = null)
    {
        logger.LogDebug("AI tool: AddTask '{Title}'", title);

        if (string.IsNullOrWhiteSpace(title))
            return "ERROR: title is required.";

        try
        {
            using var serviceScope = scopeFactory.CreateScope();
            var db = serviceScope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;
            var task = new AgentTask
            {
                Id = AgentTaskId.Transient,
                DatabaseAnalysisId = scope.AnalysisId,
                HypothesisId = scope.HypothesisId,
                Title = title.Length > 1024 ? title[..1024] : title,
                Notes = notes,
                Status = AgentTaskStatus.Pending,
                CreatedAt = now,
                ModifiedAt = now,
            };
            db.AgentTasks.Add(task);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await TouchParentAsync(db);

            return $"Task {(int)(object)task.Id} added: {task.Title}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI tool: AddTask failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Updates a task in your work plan. Set status to in_progress when you start it, completed when done, or cancelled if it is no longer relevant. You can also revise the title or notes.")]
    public async Task<string> UpdateTask(
        [Description("The task id returned by AddTask or ListTasks")] int taskId,
        [Description("New status: pending, in_progress, completed, or cancelled")] string? status = null,
        [Description("New title (optional)")] string? title = null,
        [Description("New notes, e.g. what was found or why the task was cancelled (optional)")] string? notes = null)
    {
        logger.LogDebug("AI tool: UpdateTask {TaskId} -> {Status}", taskId, status);

        AgentTaskStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            parsedStatus = ParseStatus(status);
            if (parsedStatus is null)
                return $"ERROR: unknown status '{status}'. Use pending, in_progress, completed, or cancelled.";
        }

        try
        {
            using var serviceScope = scopeFactory.CreateScope();
            var db = serviceScope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var id = (AgentTaskId)taskId;

            var task = await db.AgentTasks.AsTracking()
                .FirstOrDefaultAsync(t => t.Id == id && t.DatabaseAnalysisId == scope.AnalysisId && t.HypothesisId == scope.HypothesisId);
            if (task is null)
                return $"ERROR: task {taskId} not found in this run's plan.";

            if (parsedStatus is { } newStatus)
                task.Status = newStatus;
            if (!string.IsNullOrWhiteSpace(title))
                task.Title = title.Length > 1024 ? title[..1024] : title;
            if (notes is not null)
                task.Notes = notes;
            task.ModifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await TouchParentAsync(db);
            return $"Task {taskId} updated" + (parsedStatus is not null ? $" (status: {parsedStatus})." : ".");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI tool: UpdateTask failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Lists all tasks in your current work plan with their ids and statuses.")]
    public async Task<string> ListTasks()
    {
        logger.LogDebug("AI tool: ListTasks");
        try
        {
            using var serviceScope = scopeFactory.CreateScope();
            var db = serviceScope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var tasks = await LoadTasksAsync(db, scope);

            if (tasks.Count == 0)
                return "(no tasks yet — use AddTask to create your plan)";

            return FormatTaskList(tasks);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI tool: ListTasks failed");
            return $"ERROR: {ex.Message}";
        }
    }

    /// <summary>Loads all tasks for a scope, in creation order.</summary>
    internal static Task<List<AgentTask>> LoadTasksAsync(
        AIOptimizeDbContext db, AgentTaskScope scope, CancellationToken ct = default) =>
        db.AgentTasks.AsNoTracking()
            .Where(t => t.DatabaseAnalysisId == scope.AnalysisId && t.HypothesisId == scope.HypothesisId)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

    internal static string FormatTaskList(IReadOnlyList<AgentTask> tasks)
    {
        var sb = new StringBuilder();
        foreach (var t in tasks)
        {
            var marker = t.Status switch
            {
                AgentTaskStatus.Completed => "[x]",
                AgentTaskStatus.InProgress => "[~]",
                AgentTaskStatus.Cancelled => "[-]",
                _ => "[ ]",
            };
            sb.AppendLine($"{marker} #{(int)(object)t.Id} ({t.Status}): {t.Title}");
            if (!string.IsNullOrWhiteSpace(t.Notes))
                sb.AppendLine($"      {t.Notes.ReplaceLineEndings(" ")}");
        }
        return sb.ToString().TrimEnd();
    }

    internal static AgentTaskStatus? ParseStatus(string status) =>
        status.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_") switch
        {
            "pending" or "todo" or "open" => AgentTaskStatus.Pending,
            "in_progress" or "inprogress" or "started" or "active" or "working" => AgentTaskStatus.InProgress,
            "completed" or "complete" or "done" or "finished" => AgentTaskStatus.Completed,
            "cancelled" or "canceled" or "removed" or "skipped" or "obsolete" => AgentTaskStatus.Cancelled,
            _ => null,
        };

    private async Task TouchParentAsync(AIOptimizeDbContext db)
    {
        var now = DateTime.UtcNow;
        if (scope.AnalysisId is { } analysisId)
        {
            await db.DatabaseAnalyses
                .Where(a => a.Id == analysisId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ModifiedAt, now));
        }
        else if (scope.HypothesisId is { } hypothesisId)
        {
            await ModifiedAtStamping.StampHypothesisAndParentIterationAsync(db, hypothesisId);
        }
    }
}
