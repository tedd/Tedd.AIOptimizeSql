using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Services;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.Tests;

public class AgentTaskLoopRunnerTests
{
    [Theory]
    // Open tasks remain and runs left → continue
    [InlineData(3, true, 1, 20, true)]
    // Everything done and response acceptable → stop
    [InlineData(0, true, 1, 20, false)]
    // No tasks but response not acceptable (e.g. malformed JSON) → continue
    [InlineData(0, false, 1, 20, true)]
    // Limit reached → stop regardless of open tasks
    [InlineData(5, false, 20, 20, false)]
    // Exactly at last allowed run → stop
    [InlineData(1, true, 20, 20, false)]
    // Single-run configuration behaves like the old one-shot mode
    [InlineData(4, true, 1, 1, false)]
    public void ShouldContinue_EvaluatesTaskListAndLimit(
        int openTasks, bool responseAcceptable, int runsUsed, int maxRuns, bool expected)
    {
        Assert.Equal(expected, AgentTaskLoopRunner.ShouldContinue(openTasks, responseAcceptable, runsUsed, maxRuns));
    }

    [Fact]
    public void BuildContinuationPrompt_ListsOpenTasks()
    {
        var tasks = new List<AgentTask>
        {
            new() { Id = (AgentTaskId)1, Title = "Review procedures", Status = AgentTaskStatus.Pending },
            new() { Id = (AgentTaskId)2, Title = "Check fragmentation", Status = AgentTaskStatus.InProgress, Notes = "halfway" },
        };

        var prompt = AgentTaskLoopRunner.BuildContinuationPrompt(tasks, run: 2, maxRuns: 20, lastResponseAcceptable: true);

        Assert.Contains("run 2 of at most 20", prompt);
        Assert.Contains("Review procedures", prompt);
        Assert.Contains("Check fragmentation", prompt);
        Assert.Contains("halfway", prompt);
        Assert.Contains("unfinished", prompt);
        Assert.DoesNotContain("required final response format", prompt);
    }

    [Fact]
    public void BuildContinuationPrompt_AsksForCorrectFormatWhenResponseUnacceptable()
    {
        var prompt = AgentTaskLoopRunner.BuildContinuationPrompt(
            Array.Empty<AgentTask>(), run: 3, maxRuns: 20, lastResponseAcceptable: false);

        Assert.Contains("task list is complete", prompt);
        Assert.Contains("did not match the required response format", prompt);
    }

    [Fact]
    public void BuildContinuationPrompt_AnnouncesFinalRun()
    {
        var tasks = new List<AgentTask>
        {
            new() { Id = (AgentTaskId)1, Title = "Last thing", Status = AgentTaskStatus.Pending },
        };

        var prompt = AgentTaskLoopRunner.BuildContinuationPrompt(tasks, run: 20, maxRuns: 20, lastResponseAcceptable: true);

        Assert.Contains("FINAL run", prompt);
    }
}

public class AgentTaskToolWrapperTests
{
    [Theory]
    [InlineData("pending", AgentTaskStatus.Pending)]
    [InlineData("todo", AgentTaskStatus.Pending)]
    [InlineData("in_progress", AgentTaskStatus.InProgress)]
    [InlineData("In Progress", AgentTaskStatus.InProgress)]
    [InlineData("in-progress", AgentTaskStatus.InProgress)]
    [InlineData("STARTED", AgentTaskStatus.InProgress)]
    [InlineData("completed", AgentTaskStatus.Completed)]
    [InlineData("Done", AgentTaskStatus.Completed)]
    [InlineData("finished", AgentTaskStatus.Completed)]
    [InlineData("cancelled", AgentTaskStatus.Cancelled)]
    [InlineData("canceled", AgentTaskStatus.Cancelled)]
    [InlineData("removed", AgentTaskStatus.Cancelled)]
    [InlineData("skipped", AgentTaskStatus.Cancelled)]
    public void ParseStatus_AcceptsCommonAiVariants(string input, AgentTaskStatus expected)
    {
        Assert.Equal(expected, AgentTaskToolWrapper.ParseStatus(input));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("almost done")]
    public void ParseStatus_RejectsUnknownValues(string input)
    {
        Assert.Null(AgentTaskToolWrapper.ParseStatus(input));
    }

    [Fact]
    public void FormatTaskList_ShowsStatusMarkersAndNotes()
    {
        var tasks = new List<AgentTask>
        {
            new() { Id = (AgentTaskId)1, Title = "Done thing", Status = AgentTaskStatus.Completed },
            new() { Id = (AgentTaskId)2, Title = "Active thing", Status = AgentTaskStatus.InProgress },
            new() { Id = (AgentTaskId)3, Title = "Waiting thing", Status = AgentTaskStatus.Pending, Notes = "multi\nline note" },
            new() { Id = (AgentTaskId)4, Title = "Dropped thing", Status = AgentTaskStatus.Cancelled },
        };

        var text = AgentTaskToolWrapper.FormatTaskList(tasks);

        Assert.Contains("[x] #1 (Completed): Done thing", text);
        Assert.Contains("[~] #2 (InProgress): Active thing", text);
        Assert.Contains("[ ] #3 (Pending): Waiting thing", text);
        Assert.Contains("[-] #4 (Cancelled): Dropped thing", text);
        Assert.Contains("multi line note", text); // line endings collapsed
    }
}
