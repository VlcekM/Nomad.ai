using Nomad.Core.Agents;

namespace Nomad.Core.Tests;

/// <summary>A workflow run of two phases and three agents, captured from claude 2.1.285 (see Fixtures/workflow).</summary>
public class WorkflowTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflow", name);

    private const string LaunchResult =
        "Workflow launched in background. Task ID: wlwm2d2y3\n" +
        "Summary: Two agents answer 2+2 and 3+3, then one agent adds the answers\n" +
        "Transcript dir: C:\\p\\289bbbe7\\subagents\\workflows\\wf_ce4055d3-a4f\n" +
        "Script file: C:\\p\\289bbbe7\\workflows\\scripts\\count-and-sum-wf_ce4055d3-a4f.js\n" +
        "(Edit this file with Write/Edit and re-invoke Workflow with {scriptPath: \"…\"} to iterate without resending the script.)\n" +
        "Run ID: wf_ce4055d3-a4f\n";

    [Fact]
    public void ReplayedRunRaisesStartProgressAndEnd()
    {
        var backend = new ClaudeCodeBackend();
        var events = new List<AgentEvent>();
        backend.EventReceived += events.Add;
        foreach (var line in File.ReadAllLines(FixturePath("stream.jsonl")))
        {
            backend.HandleLine(line);
        }

        var started = Assert.Single(events.OfType<WorkflowStartedEvent>());
        Assert.Equal("wlwm2d2y3", started.TaskId);
        Assert.Equal("count-and-sum", started.Name);
        Assert.Equal("toolu_01XvW5ijHd7ZYxef7UTmCkNX", started.ToolUseId);
        Assert.Contains("export const meta", started.Script);

        var progress = events.OfType<WorkflowProgressEvent>().ToList();
        Assert.Equal(7, progress.Count);
        Assert.Contains(progress, p => p.Progress is null); // Some lines carry only the totals.
        var last = progress[^1];
        Assert.Equal(72020, last.Tokens);
        Assert.Equal(["Count", "Sum"], last.Progress!.Phases.Select(p => p.Title));
        Assert.Equal(3, last.Progress.Agents.Count);
        Assert.All(last.Progress.Agents, a => Assert.True(a.IsDone));
        Assert.Equal("{\"answer\":\"Ten\"}", last.Progress.Agents[2].ResultPreview);

        Assert.Equal("completed", Assert.Single(events.OfType<TaskUpdatedEvent>()).Status);
        Assert.Equal("completed", Assert.Single(events.OfType<TaskNotificationEvent>()).Status);
    }

    [Fact]
    public void QueuedAgentHasNoIdYet()
    {
        var lines = File.ReadAllLines(FixturePath("stream.jsonl"));
        var backend = new ClaudeCodeBackend();
        WorkflowProgress? first = null;
        backend.EventReceived += e => first ??= (e as WorkflowProgressEvent)?.Progress;
        foreach (var line in lines)
        {
            backend.HandleLine(line);
        }

        Assert.NotNull(first);
        Assert.Contains(first.Agents, a => a.IsRunning || a.IsQueued);
    }

    [Fact]
    public void LaunchResultNamesTheRunAndItsFile()
    {
        var launch = WorkflowLaunch.Parse(LaunchResult)!;
        Assert.Equal("wlwm2d2y3", launch.TaskId);
        Assert.Equal("wf_ce4055d3-a4f", launch.RunId);
        Assert.Equal(Path.Combine("C:\\p\\289bbbe7", "workflows", "wf_ce4055d3-a4f.json"), launch.RunFile);
        Assert.EndsWith("count-and-sum-wf_ce4055d3-a4f.js", launch.ScriptFile);
        Assert.Null(WorkflowLaunch.Parse("Command running in background with ID: b1"));
    }

    [Fact]
    public void RunFileHoldsTheResultAndAgents()
    {
        var run = WorkflowRunRecord.Load(FixturePath("run.json"))!;
        Assert.Equal("count-and-sum", run.Name);
        Assert.Equal("completed", run.Status);
        Assert.Equal(4604, run.DurationMs);
        Assert.Equal(["Count", "Sum"], run.PhaseTitles);
        Assert.Equal(3, run.Progress.Agents.Count);
        Assert.Equal(2, run.Progress.Agents[2].PhaseIndex);
        Assert.Contains("\"sum\": \"Ten\"", run.Result);
        Assert.True(run.TotalTokens > 70000);
    }

    [Fact]
    public void ScriptMetaGivesNameAndPhases()
    {
        var (name, description, phases) = WorkflowScript.ReadMeta(
            "export const meta = {\n  name: 'count-and-sum',\n  description: \"Adds 'numbers'\",\n  phases: [{ title: 'Count' }, { title: 'Sum' }],\n}\n\nphase('Count')");
        Assert.Equal("count-and-sum", name);
        Assert.Equal("Adds 'numbers'", description);
        Assert.Equal(["Count", "Sum"], phases);

        Assert.Equal("one", WorkflowScript.ReadMeta("export const meta = { name: 'one', phases: [] }\nrun()").Name);
    }
}
