using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Infrastructure;
using Nomad.Core.Agents;

namespace Nomad.App.ViewModels;

/// <summary>Base of everything shown in the transcript column.</summary>
public abstract class TranscriptItem : ObservableObject;

/// <summary>A prompt bubble, with its seen mark, time and Copy / Rewind / Fork on hover.</summary>
public sealed partial class UserMessageItem(string text, string? uuid, DateTime sentUtc, bool seen) : TranscriptItem
{
    public string Text { get; } = text;

    /// <summary>The transcript record's uuid (null for very old transcripts).</summary>
    public string? Uuid { get; } = uuid;

    /// <summary>The record before it, when known from history; live messages look it up on demand.</summary>
    public string? ParentUuid { get; init; }

    public DateTime SentUtc { get; } = sentUtc;

    /// <summary>Typed in Remote Control (claude.ai or the phone), not in Nomad.</summary>
    public bool IsRemote { get; init; }

    /// <summary>Claude Code picked the message up (✓✓); until then it is only sent (✓).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeenToolTip))]
    public partial bool IsSeen { get; set; } = seen;

    public string SeenToolTip => IsSeen ? "Seen by Claude Code" : "Sent";

    /// <summary>"just now", "5 minutes ago"; refreshed by the session's clock.</summary>
    [ObservableProperty]
    public partial string TimeText { get; set; } = Format.Ago(sentUtc);

    public bool CanBranch => Uuid is not null;

    /// <summary>Thumbnails of the images sent with it (live prompts only).</summary>
    public IReadOnlyList<Avalonia.Media.Imaging.Bitmap> Images { get; init; } = [];

    public bool HasImages => Images.Count > 0;

    /// <summary>Files attached to it, shown as chips (their @-mentions are left out of <see cref="Text"/>).</summary>
    public IReadOnlyList<Attachment> Files { get; init; } = [];

    public bool HasFiles => Files.Count > 0;

    public bool HasText => Text.Length > 0;

    public void Tick() => TimeText = Format.Ago(SentUtc);
}

/// <summary>A prompt waiting for the running turn to end; its text can be edited in place until it goes out.</summary>
public sealed partial class QueuedPrompt(string text, IReadOnlyList<Attachment> attachments) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(HasAttachments))]
    public partial string Text { get; set; } = text;

    /// <summary>Its row shows a text box with <see cref="EditText"/> instead of the label.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    public IReadOnlyList<Attachment> Attachments { get; } = attachments;

    /// <summary>"Fix the tests", "2 images" or "an image and a file".</summary>
    public string Label => Text.Length > 0 ? Text.ReplaceLineEndings(" ") : AttachmentsText;

    public bool HasAttachments => Attachments.Count > 0 && Text.Length > 0;

    public string AttachmentsText
    {
        get
        {
            var images = Attachments.Count(a => a.IsImage);
            var files = Attachments.Count - images;
            return string.Join(" and ", new[]
            {
                images > 0 ? Format.Count(images, "image", "images", "an") : null,
                files > 0 ? Format.Count(files, "file", "files", "a") : null,
            }.OfType<string>());
        }
    }
}

/// <summary>A row of the "/" menu.</summary>
public sealed partial class CommandOption(Nomad.Core.Protocol.SlashCommand command) : ObservableObject
{
    public string Name { get; } = command.Name;

    public string Display { get; } = "/" + command.Name;

    public string? ArgumentHint { get; } = string.IsNullOrWhiteSpace(command.ArgumentHint) ? null : command.ArgumentHint;

    public bool HasArgumentHint => ArgumentHint is not null;

    public string? Description { get; } = command.Description;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
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

    /// <summary>The last text of a finished turn: the response, with a Copy button below it.</summary>
    [ObservableProperty]
    public partial bool EndsResponse { get; set; }
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
    [NotifyPropertyChangedFor(nameof(HasText), nameof(DisplayText), nameof(FirstLine))]
    public partial string Text { get; set; }

    /// <summary>The text without the trailing blank lines summarized thinking ends with.</summary>
    public string DisplayText => Text.Trim();

    /// <summary>Its first line, beside "Thought" in an expanded activity group.</summary>
    public string FirstLine => DisplayText.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim('*', ' ');

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

    /// <summary>The call's input, once known (it picks the working mark's activity).</summary>
    public JsonElement Input { get; private set; }

    public void SetInput(JsonElement input)
    {
        Input = input.Clone();
        Summary = ToolText.Summary(Name, input, Cwd);
        InputText = ToolText.PrettyJson(input);
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// The activity between two text blocks, collapsed into one line: tool calls, and the thinking between them (as the
/// desktop app folds it). Expanded, it lists both in order.
/// </summary>
public sealed partial class ToolGroupItem : TranscriptItem
{
    public ObservableCollection<ToolCallItem> Calls { get; } = [];

    /// <summary>The calls and thinking blocks, in order.</summary>
    public ObservableCollection<object> Entries { get; } = [];

    public void AddCall(ToolCallItem call)
    {
        Calls.Add(call);
        Entries.Add(call);
    }

    public void AddThinking(ThinkingItem thinking)
    {
        Entries.Add(thinking);
        OnPropertyChanged(nameof(IsThinking));
    }

    public void RemoveThinking(ThinkingItem thinking)
    {
        Entries.Remove(thinking);
        OnPropertyChanged(nameof(IsThinking));
    }

    /// <summary>Its newest entry is thinking that still streams (the working row says "Thinking").</summary>
    public bool IsThinking => Entries.LastOrDefault() is ThinkingItem { IsStreaming: true };

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>The working row below says the same as <see cref="Summary"/>: this row fades out, keeping its place.</summary>
    [ObservableProperty]
    public partial bool IsEchoed { get; set; }

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
        IsEchoed &= IsRunning;
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
public partial class PermissionItem : TranscriptItem
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

/// <summary>
/// Files Claude sent with SendUserFile: images shown in the transcript (a click opens them large), other files as chips
/// that open on a click, under the call's caption.
/// </summary>
public sealed class SentFilesItem : TranscriptItem
{
    public SentFilesItem(JsonElement input, string? cwd)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("caption", out var caption) &&
            caption.ValueKind == JsonValueKind.String)
        {
            Caption = caption.GetString() ?? "";
        }

        var images = new List<Avalonia.Media.Imaging.Bitmap>();
        var files = new List<Attachment>();
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("files", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.GetString() is not { Length: > 0 } path)
                {
                    continue;
                }

                if (!Path.IsPathRooted(path) && cwd is not null)
                {
                    path = Path.GetFullPath(Path.Combine(cwd, path));
                }

                if (PromptImages.FromPath(path) is { Preview: { } preview })
                {
                    images.Add(preview);
                }
                else
                {
                    files.Add(Attachment.ForFile(path));
                }
            }
        }

        Images = images;
        Files = files;
    }

    public string Caption { get; } = "";

    public bool HasCaption => Caption.Length > 0;

    public IReadOnlyList<Avalonia.Media.Imaging.Bitmap> Images { get; }

    public bool HasImages => Images.Count > 0;

    public IReadOnlyList<Attachment> Files { get; }

    public bool HasFiles => Files.Count > 0;

    public bool IsEmpty => !HasImages && !HasFiles;
}

/// <summary>
/// An artifact Claude published (the Artifact tool): a card with the file's name and description that opens the
/// claude.ai page in the browser on a click.
/// </summary>
public sealed partial class ArtifactItem : TranscriptItem
{
    private ArtifactItem(string url, string? path, string? description, string? version)
    {
        Url = url;
        Path = path;
        Title = path is { Length: > 0 } ? System.IO.Path.GetFileNameWithoutExtension(path) : "Artifact";
        Subtitle = description is { Length: > 0 } ? description : "claude.ai artifact";
        Version = version;
    }

    public string Url { get; }

    public string? Path { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string? Version { get; }

    public string ToolTip => Version is null ? Url : $"{Url}\nVersion {Version}";

    public bool HasPath => Path is not null;

    /// <summary>The card for a successful publish ("Published … at https://claude.ai/artifact/…"), or null.</summary>
    public static ArtifactItem? From(JsonElement input, string? cwd, string result)
    {
        if (ArtifactUrl().Match(result) is not { Success: true } url)
        {
            return null;
        }

        string? Str(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var path = Str("file_path");
        if (path is { Length: > 0 } && !System.IO.Path.IsPathRooted(path) && cwd is not null)
        {
            path = System.IO.Path.GetFullPath(System.IO.Path.Combine(cwd, path));
        }

        var version = VersionNumber().Match(result) is { Success: true } v ? v.Groups[1].Value : null;
        return new ArtifactItem(url.Value, path is { Length: > 0 } ? path : null, Str("description")?.Trim(), version);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"https://claude\.ai/artifact/[A-Za-z0-9_-]+")]
    private static partial System.Text.RegularExpressions.Regex ArtifactUrl();

    [System.Text.RegularExpressions.GeneratedRegex(@"\(Version (\d+)")]
    private static partial System.Text.RegularExpressions.Regex VersionNumber();
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

/// <summary>
/// An AskUserQuestion request: one to four questions, each with options and a free-text answer.
/// Submitting allows the tool with the answers added to its input, keyed by question text, which is
/// how the CLI's own question dialog hands them back; skipping denies it.
/// </summary>
public sealed partial class QuestionItem : PermissionItem
{
    public QuestionItem(PermissionRequest request, string? cwd, Action<PermissionItem, PermissionChoice> answer)
        : base(request, cwd, answer)
    {
        foreach (var q in request.Input.GetProperty("questions").EnumerateArray())
        {
            if (ToolText.Str(q, "question") is not { Length: > 0 } text)
            {
                continue;
            }

            var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
            var entry = new QuestionEntry(text, ToolText.Str(q, "header"), multi, Update);
            if (q.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in options.EnumerateArray())
                {
                    if (ToolText.Str(o, "label") is { Length: > 0 } label)
                    {
                        entry.Options.Add(new QuestionOption(entry, label, ToolText.Str(o, "description")));
                    }
                }
            }

            Questions.Add(entry);
        }
    }

    /// <summary>True when the input carries at least one question this card can show.</summary>
    public static bool CanShow(PermissionRequest request) =>
        request.ToolName == "AskUserQuestion" &&
        request.Input.ValueKind == JsonValueKind.Object &&
        request.Input.TryGetProperty("questions", out var questions) &&
        questions.ValueKind == JsonValueKind.Array &&
        questions.EnumerateArray().Any(q => ToolText.Str(q, "question") is { Length: > 0 });

    public ObservableCollection<QuestionEntry> Questions { get; } = [];

    [ObservableProperty]
    public partial bool CanSubmit { get; set; }

    /// <summary>"Blue" or "Blue; Tea" for the one-line record once answered.</summary>
    public string AnswerSummary => string.Join("; ", Questions.Select(q => q.Answer));

    /// <summary>Chooses the first option of every unanswered question (dev hook <c>--dev-answer</c>).</summary>
    public void PickDefaults()
    {
        foreach (var q in Questions.Where(q => !q.IsAnswered && q.Options.Count > 0))
        {
            q.Pick(q.Options[0]);
        }
    }

    /// <summary>The tool input with an <c>answers</c> object added: question text → answer.</summary>
    public JsonElement BuildUpdatedInput()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var property in Request.Input.EnumerateObject())
            {
                if (property.Name != "answers")
                {
                    property.WriteTo(w);
                }
            }

            w.WriteStartObject("answers");
            foreach (var q in Questions)
            {
                w.WriteString(q.Question, q.Answer);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private void Update() => CanSubmit = Questions.Count > 0 && Questions.All(q => q.IsAnswered);
}

/// <summary>One question: options with radio behaviour (checkboxes when multiSelect) plus free text.</summary>
public sealed partial class QuestionEntry : ObservableObject
{
    private readonly Action _changed;

    public QuestionEntry(string question, string? header, bool multiSelect, Action changed)
    {
        Question = question;
        Header = header ?? "";
        MultiSelect = multiSelect;
        _changed = changed;
    }

    public string Question { get; }

    public string Header { get; }

    public bool MultiSelect { get; }

    public ObservableCollection<QuestionOption> Options { get; } = [];

    /// <summary>The free-text answer ("Other"); typing it replaces the chosen option unless multiSelect.</summary>
    [ObservableProperty]
    public partial string OtherText { get; set; } = "";

    /// <summary>The chosen label(s) joined by ", ", plus any typed text.</summary>
    public string Answer
    {
        get
        {
            var parts = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            if (!string.IsNullOrWhiteSpace(OtherText))
            {
                parts.Add(OtherText.Trim());
            }

            return string.Join(", ", parts);
        }
    }

    public bool IsAnswered => Answer.Length > 0;

    public void Pick(QuestionOption option)
    {
        if (MultiSelect)
        {
            option.IsSelected = !option.IsSelected;
        }
        else
        {
            foreach (var o in Options)
            {
                o.IsSelected = ReferenceEquals(o, option);
            }

            OtherText = "";
        }

        _changed();
    }

    partial void OnOtherTextChanged(string value)
    {
        if (!MultiSelect && !string.IsNullOrWhiteSpace(value))
        {
            foreach (var o in Options)
            {
                o.IsSelected = false;
            }
        }

        _changed();
    }
}

public sealed partial class QuestionOption(QuestionEntry owner, string label, string? description) : ObservableObject
{
    public string Label { get; } = label;

    public string? Description { get; } = description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick() => owner.Pick(this);
}
