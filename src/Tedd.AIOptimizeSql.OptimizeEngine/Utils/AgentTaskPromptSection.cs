using System.Text;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// The task-list workflow instructions shared by all agent prompts
/// (hypothesis generation and database analysis).
/// </summary>
internal static class AgentTaskPromptSection
{
    public static string Build(int maxRuns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Work plan (task list) — required workflow");
        sb.AppendLine();
        sb.AppendLine("You have task list tools: AddTask, UpdateTask, ListTasks. The task list is shown live to the user, so keep it accurate:");
        sb.AppendLine();
        sb.AppendLine("1. START by creating your plan: one AddTask call per step you intend to take.");
        sb.AppendLine("2. Before working on a step, mark it in_progress (UpdateTask). When you finish it, immediately mark it completed.");
        sb.AppendLine("3. If you learn something that changes the plan, update it: add new tasks, cancel tasks that are no longer relevant (status cancelled, with a note explaining why), or rewrite task titles.");
        sb.AppendLine("4. Do not mark a task completed unless you actually did the work.");
        sb.AppendLine($"5. If you run out of output before finishing, you will be asked to continue (up to {maxRuns} runs total) while Pending or InProgress tasks remain — the task list is your persistent memory of where you are.");
        sb.AppendLine("6. When every task is completed or cancelled, produce your final response in the required format.");
        return sb.ToString();
    }
}
