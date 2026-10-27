using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Infrastructure;

namespace Nomad.App.ViewModels;

/// <summary>
/// A subagent (an Agent / Task tool call) of the session: what it was asked, how long it has run, what it did last,
/// and its own conversation, built from the CLI's events that carry its tool-use id as their parent.
/// </summary>
public sealed partial class SubagentViewModel : ObservableObject
{
    private readonly Dictionary<(string MessageId, int Index), AssistantTextItem> _texts = [];
    private readonly Dictionary<string, ToolCallItem> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<(string MessageId, int Index), string> _thoughts = [];
    private readonly string? _cwd;

    public SubagentViewModel(string toolUseId, string? cwd)
    {
        ToolUseId = toolUseId;
        _cwd = cwd;
        StartedUtc = DateTime.UtcNow;
    }

    public string ToolUseId { get; }

    public DateTime StartedUtc { get; }

    public DateTime? EndedUtc { get; private set; }

    [ObservableProperty]
    public partial string Description { get; set; } = "Subagent";

    /// <summary>"Explore", "general-purpose"; empty when not given.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAgentType))]
    public partial string AgentType { get; set; } = "";

    public bool HasAgentType => AgentType.Length > 0;

    /// <summary>"Sonnet 5.5": the model it runs on (from its first reply; the Agent call's model until then).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelName), nameof(ChipText))]
    public partial string ModelName { get; set; } = "";

    public bool HasModelName => ModelName.Length > 0;

    /// <summary>The row's chip: the model, or the agent type until the model is known.</summary>
    public string ChipText => HasModelName ? ModelName : AgentType;

    public bool HasChip => ChipText.Length > 0;

    partial void OnAgentTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ChipText));
        OnPropertyChanged(nameof(HasChip));
        OnPropertyChanged(nameof(LiveText));
    }

    partial void OnModelNameChanged(string value)
    {
        OnPropertyChanged(nameof(HasChip));
        OnPropertyChanged(nameof(LiveText));
    }

    /// <summary>The chat's row for it while it runs: "Opus 5.5: Reading MainViewModel.cs…", its latest thought.</summary>
    public string LiveText => $"{(HasChip ? ChipText : "Subagent")}: {LastStatus}";

    partial void OnLastStatusChanged(string value) => OnPropertyChanged(nameof(LiveText));

    /// <summary>Its replies name the model they came from.</summary>
    public void SetModel(string? model)
    {
        if (!string.IsNullOrEmpty(model))
        {
            ModelName = Catalog.ModelName(model);
        }
    }

    /// <summary>The prompt it was given.</summary>
    [ObservableProperty]
    public partial string Prompt { get; set; } = "";

    /// <summary>Started with <c>run_in_background</c>: it may outlive the turn.</summary>
    public bool IsBackground { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    public partial bool IsRunning { get; set; } = true;

    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    public bool IsDone => !IsRunning && !IsFailed;

    /// <summary>What it is doing or did last: "Reading MainViewModel.cs", its latest words, "Done".</summary>
    [ObservableProperty]
    public partial string LastStatus { get; set; } = "Starting";

    [ObservableProperty]
    public partial string DurationText { get; set; } = "0s";

    /// <summary>Its conversation: <see cref="AssistantTextItem"/>s and <see cref="ToolCallItem"/>s in order.</summary>
    public ObservableCollection<object> Entries { get; } = [];

    /// <summary>The final answer it returned to the main agent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string ResultText { get; set; } = "";

    public bool HasResult => ResultText.Length > 0;

    public int ToolCount => _calls.Count;

    public void SetInput(JsonElement input)
    {
        Description = ToolText.Str(input, "description") is { Length: > 0 } d ? d : "Subagent";
        AgentType = ToolText.Str(input, "subagent_type") ?? "";
        if (ToolText.Str(input, "model") is { Length: > 0 } model && ModelName.Length == 0)
        {
            ModelName = Catalog.ModelName(model);
        }
        Prompt = ToolText.Str(input, "prompt") ?? "";
        IsBackground = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("run_in_background", out var bg) && bg.ValueKind == JsonValueKind.True;
    }

    public void AppendText(string messageId, int index, string delta)
    {
        if (!_texts.TryGetValue((messageId, index), out var item))
        {
            item = new AssistantTextItem(messageId, index, "", streaming: true);
            _texts[(messageId, index)] = item;
            Entries.Add(item);
        }

        item.Text += delta;
        if (ToolText.FirstLine(item.Text) is { Length: > 0 } line)
        {
            LastStatus = line;
        }
    }

    public void CompleteText(string messageId, int index, string? text)
    {
        if (!_texts.TryGetValue((messageId, index), out var item))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            item = new AssistantTextItem(messageId, index, text, streaming: false);
            _texts[(messageId, index)] = item;
            Entries.Add(item);
        }
        else if (text is not null)
        {
            item.Text = text;
        }

        item.IsStreaming = false;
        if (ToolText.FirstLine(item.Text) is { Length: > 0 } line)
        {
            LastStatus = line;
        }
    }

    /// <summary>It thinks: the newest line of the thought is what it is at.</summary>
    public void AppendThinking(string messageId, int index, string delta)
    {
        var text = _thoughts.TryGetValue((messageId, index), out var so) ? so + delta : delta;
        _thoughts[(messageId, index)] = text;
        if (LastLine(text) is { Length: > 0 } line)
        {
            LastStatus = line;
        }
    }

    public void CompleteThinking(string messageId, int index, string? text)
    {
        _thoughts.Remove((messageId, index));
        if (text is not null && LastLine(text) is { Length: > 0 } line)
        {
            LastStatus = line;
        }
    }

    private static string LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim().Trim('*').Trim()).LastOrDefault(l => l.Length > 0) ?? "";

    public void AddCall(string toolUseId, string name, JsonElement input)
    {
        if (!_calls.TryGetValue(toolUseId, out var call))
        {
            call = new ToolCallItem(toolUseId, name, _cwd);
            _calls[toolUseId] = call;
            Entries.Add(call);
        }

        call.SetInput(input);
        LastStatus = ToolText.RunningAction(name, input);
    }

    public void CompleteCall(string toolUseId, bool isError, string text)
    {
        if (_calls.TryGetValue(toolUseId, out var call))
        {
            call.Status = isError ? ToolStatus.Failed : ToolStatus.Done;
            call.ResultText = text.Length > 20_000 ? text[..20_000] + "\n…" : text;
        }
    }

    /// <summary>Its Agent call returned at once: it runs on in the background and ends with a task notification.</summary>
    public void MarkBackground()
    {
        IsBackground = true;
        if (Entries.Count == 0)
        {
            LastStatus = "Running in the background";
        }
    }

    /// <summary>Its Agent call returned, its task notification came, or the turn or process ended without either.</summary>
    public void Finish(bool failed, string? result)
    {
        if (!IsRunning)
        {
            return;
        }

        EndedUtc = DateTime.UtcNow;
        IsFailed = failed;
        IsRunning = false;

        // Without a result of its own (a background one): its last words.
        ResultText = result?.Trim() is { Length: > 0 } given ? given : Entries.OfType<AssistantTextItem>().LastOrDefault()?.Text.Trim() ?? "";
        LastStatus = failed ? "Failed" : "Done";
        foreach (var call in _calls.Values.Where(c => c.IsRunning))
        {
            call.Status = ToolStatus.Done;
        }

        foreach (var text in _texts.Values)
        {
            text.IsStreaming = false;
        }

        Tick();
    }

    public void Tick()
    {
        var elapsed = (EndedUtc ?? DateTime.UtcNow) - StartedUtc;
        DurationText = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }
}
