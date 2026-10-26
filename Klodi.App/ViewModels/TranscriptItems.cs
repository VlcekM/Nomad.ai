using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klodi.App.Infrastructure;
using Klodi.Core.Agents;

namespace Klodi.App.ViewModels;

/// <summary>Base of everything shown in the transcript column.</summary>
public abstract class TranscriptItem : ObservableObject;

public sealed class UserMessageItem(string text) : TranscriptItem
{
    public string Text { get; } = text;
}

/// <summary>Assistant prose, rendered as markdown; grows while streaming.</summary>
public sealed partial class AssistantTextItem : TranscriptItem
{
    public AssistantTextItem(string messageId, int blockIndex, string text, bool streaming)
    {
        MessageId = messageId;
        BlockIndex = blockIndex;
        Text = text;
        IsStreaming = streaming;
    }

    public string MessageId { get; }

    public int BlockIndex { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool IsStreaming { get; set; }
}

/// <summary>One collapsed "Thinking" line that expands to the thinking text.</summary>
public sealed partial class ThinkingItem : TranscriptItem
{
    public ThinkingItem(string messageId, int blockIndex, string text, bool streaming)
    {
        MessageId = messageId;
        BlockIndex = blockIndex;
        Text = text;
        IsStreaming = streaming;
    }

    public string MessageId { get; }

    public int BlockIndex { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(DisplayText))]
    public partial string Text { get; set; }

    /// <summary>The text without the trailing blank lines summarized thinking ends with.</summary>
    public string DisplayText => Text.Trim();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial bool IsStreaming { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public string Label => IsStreaming ? "Thinking" : "Thought process";

    [RelayCommand]
    private void Toggle()
    {
        if (HasText)
        {
            IsExpanded = !IsExpanded;
        }
    }
}

public enum ToolStatus
{
    Running,
    Done,
    Failed,
}

/// <summary>One tool call inside a group.</summary>
public sealed partial class ToolCallItem : ObservableObject
{
    public ToolCallItem(string toolUseId, string name, string? cwd)
    {
        ToolUseId = toolUseId;
        Name = name;
        Cwd = cwd;
        IconKey = ToolText.IconKey(name);
        SummaryIsCode = ToolText.SummaryIsCode(name);
    }

    public string ToolUseId { get; }

    public string Name { get; }

    public string? Cwd { get; }

    public string IconKey { get; }

    public bool SummaryIsCode { get; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string ResultText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    public partial ToolStatus Status { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public int Added { get; set; }

    public int Removed { get; set; }

    public bool IsRunning => Status == ToolStatus.Running;

    public bool IsDone => Status == ToolStatus.Done;

    public bool IsFailed => Status == ToolStatus.Failed;

    public bool HasResult => !string.IsNullOrEmpty(ResultText);

    public void SetInput(JsonElement input)
    {
        Summary = ToolText.Summary(Name, input, Cwd);
        InputText = ToolText.PrettyJson(input);
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>Consecutive tool calls between two text blocks, collapsed into one line.</summary>
public sealed partial class ToolGroupItem : TranscriptItem
{
    public ObservableCollection<ToolCallItem> Calls { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    [NotifyPropertyChangedFor(nameof(AddedText))]
    [NotifyPropertyChangedFor(nameof(HasAdded))]
    public partial int Added { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    [NotifyPropertyChangedFor(nameof(RemovedText))]
    [NotifyPropertyChangedFor(nameof(HasRemoved))]
    public partial int Removed { get; set; }

    public bool HasDiff => Added > 0 || Removed > 0;

    public bool HasAdded => Added > 0;

    public bool HasRemoved => Removed > 0;

    public string AddedText => $"+{Added}";

    public string RemovedText => $"−{Removed}";

    public ToolCallItem? Find(string toolUseId) => Calls.FirstOrDefault(c => c.ToolUseId == toolUseId);

    public void Refresh()
    {
        var running = Calls.LastOrDefault(c => c.Status == ToolStatus.Running);
        IsRunning = running is not null;
        Summary = running is not null
            ? ToolText.RunningLabel(running.Name)
            : ToolText.GroupSummary(Calls.Select(c => (c.Name, c.Status == ToolStatus.Failed)).ToList());
        Added = Calls.Sum(c => c.Added);
        Removed = Calls.Sum(c => c.Removed);
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>A line of a mini diff in the permission card.</summary>
public sealed record DiffLine(string Text, bool IsAdded);

/// <summary>A tool call waiting for Allow / Allow for session / Deny.</summary>
public sealed partial class PermissionItem : TranscriptItem
{
    private readonly Action<PermissionItem, PermissionChoice> _answer;

    public PermissionItem(PermissionRequest request, string? cwd, Action<PermissionItem, PermissionChoice> answer)
    {
        Request = request;
        _answer = answer;
        var input = request.Input;
        Title = request.ToolName switch
        {
            "Bash" or "PowerShell" => "Claude wants to run a command",
            "Edit" or "MultiEdit" or "NotebookEdit" => "Claude wants to edit a file",
            "Write" => "Claude wants to write a file",
            "Read" => "Claude wants to read a file",
            "WebFetch" => "Claude wants to fetch a page",
            "WebSearch" => "Claude wants to search the web",
            _ => $"Claude wants to use {request.DisplayName ?? request.ToolName}",
        };

        Detail = request.ToolName is "Bash" or "PowerShell"
            ? ToolText.Str(input, "command") ?? ""
            : ToolText.Summary(request.ToolName, input, cwd);
        DetailIsCode = request.ToolName is "Bash" or "PowerShell" or "Grep" or "Glob";
        Description = request.ToolName is "Bash" or "PowerShell" ? ToolText.Str(input, "description") : null;

        if (request.ToolName == "Edit")
        {
            foreach (var line in (ToolText.Str(input, "old_string") ?? "").TrimEnd('\n', '\r').Split('\n').Take(12))
            {
                DiffLines.Add(new DiffLine("- " + line, false));
            }

            foreach (var line in (ToolText.Str(input, "new_string") ?? "").TrimEnd('\n', '\r').Split('\n').Take(12))
            {
                DiffLines.Add(new DiffLine("+ " + line, true));
            }
        }
        else if (request.ToolName == "Write")
        {
            foreach (var line in (ToolText.Str(input, "content") ?? "").TrimEnd('\n', '\r').Split('\n').Take(12))
            {
                DiffLines.Add(new DiffLine("+ " + line, true));
            }
        }

        CanAllowForSession = !request.SuppressAllowForSession;
    }

    public PermissionRequest Request { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool DetailIsCode { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public ObservableCollection<DiffLine> DiffLines { get; } = [];

    public bool HasDiff => DiffLines.Count > 0;

    public bool CanAllowForSession { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    public partial bool IsAnswered { get; set; }

    [ObservableProperty]
    public partial string AnswerText { get; set; } = "";

    public bool IsPending => !IsAnswered;

    public void MarkAnswered(string text)
    {
        AnswerText = text;
        IsAnswered = true;
    }

    [RelayCommand]
    private void Allow() => _answer(this, PermissionChoice.Allow);

    [RelayCommand]
    private void AllowForSession() => _answer(this, PermissionChoice.AllowForSession);

    [RelayCommand]
    private void Deny() => _answer(this, PermissionChoice.Deny);
}

public sealed class ErrorItem(string message) : TranscriptItem
{
    public string Message { get; } = message;
}

/// <summary>Muted one-liners: "Interrupted", "Earlier history truncated", "Allowed Bash".</summary>
public sealed class NoticeItem(string text) : TranscriptItem
{
    public string Text { get; } = text;
}
