using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Infrastructure;
using Nomad.Core.Agents;

namespace Nomad.App.ViewModels;

/// <summary>
/// A Workflow call's run, as a card in the transcript: its name, its phases with their agents (each one's state,
/// model, last tool, tokens and result), its totals and, once it ends, its result. Live from the CLI's task_progress
/// lines; for a reopened chat, from the run's file the CLI keeps.
/// </summary>
public sealed partial class WorkflowRunItem : TranscriptItem
{
    private readonly Dictionary<int, WorkflowAgentItem> _agents = [];

    public WorkflowRunItem(string toolUseId)
    {
        ToolUseId = toolUseId;
        StartedUtc = DateTime.UtcNow;
    }

    public string ToolUseId { get; }

    public DateTime StartedUtc { get; private set; }

    /// <summary>The background task the CLI runs it as ("wlwm2d2y3").</summary>
    public string? TaskId { get; set; }

    /// <summary>The run's own id ("wf_ce4055d3-a4f").</summary>
    public string? RunId { get; private set; }

    /// <summary>Where the CLI keeps the finished run (its result).</summary>
    public string? RunFile { get; private set; }

    public string? ScriptFile { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "Workflow";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial string Description { get; set; } = "";

    public bool HasDescription => Description.Length > 0;

    public ObservableCollection<WorkflowPhaseItem> Phases { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(StatusText), nameof(IsEnded))]
    public partial bool IsRunning { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(StatusText))]
    public partial bool IsFailed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(StatusText))]
    public partial bool IsStopped { get; private set; }

    public bool IsDone => !IsRunning && !IsFailed && !IsStopped;

    public bool IsEnded => !IsRunning;

    public string StatusText => IsRunning ? "Running" : IsFailed ? "Failed" : IsStopped ? "Stopped" : "Done";

    /// <summary>"3 agents · 72k tokens · 3 tool calls · 0:04".</summary>
    [ObservableProperty]
    public partial string TotalsText { get; private set; } = "";

    /// <summary>"2 of 3 agents done" while it runs.</summary>
    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "Starting";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; private set; }

    public bool HasResult => Result is { Length: > 0 };

    [ObservableProperty]
    public partial bool IsResultOpen { get; set; }

    /// <summary>The phases and agents: open while it runs; a reopened chat's finished run shows only its header.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    private long _tokens;
    private int _toolUses;
    private long _durationMs;

    /// <summary>The script's meta: its name, description and phases, shown before the first progress.</summary>
    public void SetScript(string? script, string? name = null, string? description = null)
    {
        var meta = WorkflowScript.ReadMeta(script);
        if ((name ?? meta.Name) is { Length: > 0 } n)
        {
            Name = n;
        }

        if ((description ?? meta.Description) is { Length: > 0 } d)
        {
            Description = d;
        }

        for (var i = 0; i < meta.Phases.Count; i++)
        {
            PhaseAt(i + 1).Title = meta.Phases[i];
        }
    }

    /// <summary>The call's input: a script, or a saved workflow's name.</summary>
    public void SetInput(System.Text.Json.JsonElement input)
    {
        if (input.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return;
        }

        string? Str(string key) => input.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        SetScript(Str("script"), Str("name"));
    }

    public void SetLaunch(WorkflowLaunch launch)
    {
        TaskId ??= launch.TaskId;
        RunId = launch.RunId;
        RunFile = launch.RunFile;
        ScriptFile = launch.ScriptFile;
    }

    /// <summary>A progress line: its totals, and its phases and agents when it carries them.</summary>
    public void Apply(WorkflowProgress? progress, long tokens, int toolUses, long durationMs)
    {
        _tokens = Math.Max(_tokens, tokens);
        _toolUses = Math.Max(_toolUses, toolUses);
        _durationMs = Math.Max(_durationMs, durationMs);
        if (progress is not null)
        {
            Apply(progress);
        }

        Refresh();
    }

    private void Apply(WorkflowProgress progress)
    {
        foreach (var phase in progress.Phases)
        {
            PhaseAt(phase.Index).Title = phase.Title;
        }

        foreach (var state in progress.Agents)
        {
            if (!_agents.TryGetValue(state.Index, out var agent))
            {
                agent = new WorkflowAgentItem(state.Index);
                _agents[state.Index] = agent;
                PhaseAt(Math.Max(1, state.PhaseIndex)).Add(agent);
            }

            agent.Apply(state);
        }

        foreach (var phase in Phases)
        {
            phase.Refresh();
        }
    }

    private WorkflowPhaseItem PhaseAt(int index)
    {
        var at = 0;
        for (; at < Phases.Count; at++)
        {
            if (Phases[at].Index == index)
            {
                return Phases[at];
            }

            if (Phases[at].Index > index)
            {
                break;
            }
        }

        var phase = new WorkflowPhaseItem(index);
        Phases.Insert(at, phase);
        return phase;
    }

    /// <summary>The run ended: completed, failed or killed (stopped).</summary>
    public void Finish(string? status, bool final = false)
    {
        if (!IsRunning && !final)
        {
            return;
        }

        IsFailed = status is "failed" or "error";
        IsStopped = status is "killed" or "stopped" or "cancelled";
        IsRunning = false;
        foreach (var agent in _agents.Values.Where(a => a.IsRunning || a.IsQueued))
        {
            agent.Stop();
        }

        foreach (var phase in Phases)
        {
            phase.Refresh();
        }

        Refresh();
    }

    /// <summary>The finished run from its file: its result, and its agents as they ended.</summary>
    public void Apply(WorkflowRunRecord run)
    {
        if (run.Name is { Length: > 0 } name)
        {
            Name = name;
        }

        if (run.Summary is { Length: > 0 } summary && !HasDescription)
        {
            Description = summary;
        }

        for (var i = 0; i < run.PhaseTitles.Count; i++)
        {
            PhaseAt(i + 1).Title = run.PhaseTitles[i];
        }

        _tokens = Math.Max(_tokens, run.TotalTokens);
        _toolUses = Math.Max(_toolUses, run.TotalToolCalls);
        if (run.DurationMs > 0)
        {
            _durationMs = run.DurationMs;
        }

        Apply(run.Progress);
        Result = run.Result;
        Finish(run.Status ?? "completed", final: true);
        Refresh();
    }

    /// <summary>Loads the run's file, if the CLI wrote it; false when there is none (yet).</summary>
    public async Task<bool> LoadRunFileAsync()
    {
        if (RunFile is not { } file)
        {
            return false;
        }

        var run = await Task.Run(() => File.Exists(file) ? WorkflowRunRecord.Load(file) : null);
        if (run is null)
        {
            return false;
        }

        Apply(run);
        return true;
    }

    /// <summary>Its elapsed time ticks while it runs (the CLI's own duration comes only with progress).</summary>
    public void Tick() => Refresh();

    private void Refresh()
    {
        var agents = _agents.Values;
        var elapsed = IsRunning ? Math.Max(_durationMs, (long)(DateTime.UtcNow - StartedUtc).TotalMilliseconds) : _durationMs;
        var parts = new List<string> { Format.Count(agents.Count, "agent", "agents", "an") };
        if (_tokens > 0)
        {
            parts.Add(Format.Tokens(_tokens) + " tokens");
        }

        if (_toolUses > 0)
        {
            parts.Add(ToolCalls(_toolUses));
        }

        if (elapsed > 0)
        {
            parts.Add(Format.Duration(elapsed));
        }

        TotalsText = string.Join(" · ", parts);
        var done = agents.Count(a => a.IsDone || a.IsFailed || a.IsStopped);
        ProgressText = !IsRunning ? StatusText
            : agents.Count == 0 ? "Starting"
            : $"{done} of {agents.Count} agents done";
    }

    internal static string ToolCalls(int n) => n == 1 ? "1 tool call" : $"{n} tool calls";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleResult() => IsResultOpen = !IsResultOpen;
}

/// <summary>A phase of a workflow run, with the agents that ran in it.</summary>
public sealed partial class WorkflowPhaseItem(int index) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    public partial string Title { get; set; } = $"Phase {index}";

    public ObservableCollection<WorkflowAgentItem> Agents { get; } = [];

    public bool HasAgents => Agents.Count > 0;

    /// <summary>"2/2", or "" before an agent is in it.</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool IsDone { get; private set; }

    [ObservableProperty]
    public partial bool IsPending { get; private set; } = true;

    public void Add(WorkflowAgentItem agent)
    {
        var at = 0;
        while (at < Agents.Count && Agents[at].Index < agent.Index)
        {
            at++;
        }

        Agents.Insert(at, agent);
        OnPropertyChanged(nameof(HasAgents));
    }

    public void Refresh()
    {
        var ended = Agents.Count(a => !a.IsRunning && !a.IsQueued);
        CountText = Agents.Count == 0 ? "" : $"{Agents.Count(a => a.IsDone)}/{Agents.Count}";
        IsRunning = Agents.Any(a => a.IsRunning);
        IsDone = Agents.Count > 0 && ended == Agents.Count;
        IsPending = !IsRunning && !IsDone;
    }
}

/// <summary>An agent of a workflow run: its label, state, model, what it does now, its tokens and its result.</summary>
public sealed partial class WorkflowAgentItem(int index) : ObservableObject
{
    public int Index { get; } = index;

    /// <summary>Not shown yet: its row plays its entrance the first time.</summary>
    public bool IsFresh { get; set; } = true;

    [ObservableProperty]
    public partial string Label { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    public partial string ModelName { get; private set; } = "";

    public bool HasModel => ModelName.Length > 0;

    [ObservableProperty]
    public partial bool IsQueued { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool IsDone { get; private set; }

    [ObservableProperty]
    public partial bool IsFailed { get; private set; }

    /// <summary>The run was stopped (or ended) before it was done.</summary>
    [ObservableProperty]
    public partial bool IsStopped { get; private set; }

    /// <summary>"Read: Program.cs": its latest tool and what it was about (its answer, once it gives one); "Queued" before it starts.</summary>
    [ObservableProperty]
    public partial string ActivityText { get; private set; } = "Queued";

    /// <summary>"24k tokens · 1 tool call · 0:01", and its attempt when retried.</summary>
    [ObservableProperty]
    public partial string StatsText { get; private set; } = "";

    [ObservableProperty]
    public partial string Prompt { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string Result { get; private set; } = "";

    public bool HasResult => Result.Length > 0;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public void Apply(WorkflowAgentState state)
    {
        Label = state.Label;
        if (state.Model is { Length: > 0 } model)
        {
            ModelName = Catalog.ModelName(model);
        }

        IsStopped = false;
        IsQueued = state.IsQueued && !state.IsDone && !state.IsFailed;
        IsDone = state.IsDone;
        IsFailed = state.IsFailed;
        IsRunning = !IsQueued && !IsDone && !IsFailed;

        // Its structured answer is its result: shown as it is, without the tool's name.
        var tool = state.LastToolName is "StructuredOutput" && state.LastToolSummary is { Length: > 0 } answer ? FirstLine(answer)
            : state.LastToolName is { Length: > 0 } name
            ? state.LastToolSummary is { Length: > 0 } about ? $"{name}: {FirstLine(about)}" : name
            : null;
        ActivityText = IsQueued ? "Queued"
            : tool ?? (IsRunning ? "Starting" : IsFailed ? "Failed" : state.ResultPreview is { Length: > 0 } result ? FirstLine(result) : "Done");

        var parts = new List<string>();
        if (state.Tokens > 0)
        {
            parts.Add(Format.Tokens(state.Tokens) + " tokens");
        }

        if (state.ToolCalls > 0)
        {
            parts.Add(WorkflowRunItem.ToolCalls(state.ToolCalls));
        }

        if (state.DurationMs > 0)
        {
            parts.Add(Format.Duration(state.DurationMs));
        }

        if (state.Attempt > 1)
        {
            parts.Add($"attempt {state.Attempt}");
        }

        StatsText = string.Join(" · ", parts);
        Prompt = state.PromptPreview ?? "";
        Result = state.ResultPreview ?? "";
    }

    /// <summary>The run ended before it did.</summary>
    public void Stop()
    {
        IsRunning = false;
        IsQueued = false;
        IsStopped = true;
        ActivityText = "Stopped";
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
