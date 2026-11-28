using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>One transcript entry. Long text is clipped, with Show more.</summary>
public sealed partial class EntryViewModel : ObservableObject
{
    /// <summary>Text longer than this shows clipped until Show more.</summary>
    public const int ClipAt = 1500;

    public EntryViewModel(ChatEntry entry)
    {
        Entry = entry;
        Kind = KindOf(entry);
        Text = entry.Text;
        Steps = StepsOf(entry);
        Files = FilesOf(entry);
    }

    private static List<StepViewModel> StepsOf(ChatEntry entry) => [.. (entry.Steps ?? []).Select(s => new StepViewModel(s))];

    /// <summary>Files the agent sent show as <c>files</c>; the PC sends them as a notice naming them, for a phone that can't.</summary>
    internal static string KindOf(ChatEntry entry) => entry.Files is { Files.Count: > 0 } ? "files" : entry.Kind;

    private static List<SentFileViewModel> FilesOf(ChatEntry entry) =>
        [.. (entry.Files?.Files ?? []).Select((f, i) => new SentFileViewModel(f, entry.Index, i))];

    /// <summary>The files the agent sent, under <see cref="Caption"/>: images as pictures, other files as chips.</summary>
    public IReadOnlyList<SentFileViewModel> Files { get; private set; }

    public bool IsFiles => Kind == "files";

    public string Caption => Entry.Files?.Caption ?? "";

    public bool HasCaption => Caption.Length > 0;

    private EntryViewModel(ChatEntry entry, bool local) : this(entry) => IsLocal = local;

    /// <summary>An entry made on the phone from the live stream; the next page replaces it.</summary>
    public static EntryViewModel Local(int index, string kind, string text) => new(new ChatEntry(index, kind, text), local: true);

    public ChatEntry Entry { get; private set; }

    public int Index => Entry.Index;

    public bool IsLocal { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUser), nameof(IsAssistant), nameof(IsActivity), nameof(IsThinking), nameof(IsNotice), nameof(IsError), nameof(IsCard), nameof(IsClipped),
        nameof(IsFiles))]
    public partial string Kind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownText), nameof(IsClipped))]
    public partial string Text { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownText), nameof(IsClipped))]
    public partial bool IsExpanded { get; set; }

    public bool IsUser => Kind == "user";

    public bool IsAssistant => Kind == "assistant";

    public bool IsActivity => Kind == "activity";

    public bool IsThinking => Kind == "thinking";

    /// <summary>What an activity did, in order: each command or file with its one line, and the thinking between them.</summary>
    public IReadOnlyList<StepViewModel> Steps { get; private set; } = [];

    /// <summary>Steps realized for the UI: empty while collapsed so closed activities allocate no child controls.</summary>
    public IReadOnlyList<StepViewModel> VisibleSteps => _stepsLoaded ? Steps : [];

    private bool _stepsLoaded;

    public bool HasSteps => Steps.Count > 0;

    /// <summary>An activity shows its steps, or a thought its text, once tapped open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoldGlyph), nameof(ThinkingText), nameof(ShowsThought))]
    public partial bool IsOpen { get; set; }

    partial void OnIsOpenChanged(bool value)
    {
        if (value && !_stepsLoaded)
        {
            _stepsLoaded = true;
            OnPropertyChanged(nameof(VisibleSteps));
        }
    }

    /// <summary>The PC writes thinking out in full: a thought shows whole, with no "Thought" line to open it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsThought))]
    public partial bool InFull { get; set; }

    /// <summary>A thought's text is showing: opened, or written out in full.</summary>
    public bool ShowsThought => IsOpen || InFull;

    public string FoldGlyph => IsOpen ? "▾" : "▸";

    /// <summary>A thought: its first line folded, all of it open.</summary>
    public string ThinkingText => IsOpen ? Text : FirstLine(Text);

    [RelayCommand]
    private void ToggleOpen() => IsOpen = !IsOpen;

    private static string FirstLine(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim('*', ' ');
        return line.Length < text.Trim().Length ? line + " …" : line;
    }

    public bool IsNotice => Kind == "notice";

    public bool IsError => Kind == "error";

    public bool IsCard => Kind is "permission" or "question";

    /// <summary>Who sent a prompt that didn't come from the PC itself: a phone, another PC, or another chat.</summary>
    public string? From => Entry.From;

    public bool HasFrom => !string.IsNullOrEmpty(Entry.From);

    public string FromText => "from " + Entry.From;

    /// <summary>Long text shows clipped until Show more; a thought folds by itself instead.</summary>
    public bool IsClipped => !IsThinking && !IsFiles && !IsExpanded && Text.Length > ClipAt;

    public string ShownText => IsClipped ? Text[..ClipAt].TrimEnd() + "…" : Text;

    /// <summary>A card as it stands in the transcript: its title, and how it was answered.</summary>
    public string CardText => Entry.Card is { } card
        ? (card.Title ?? (Kind == "question" ? "Question" : "Permission")) + (card.Answer is { Length: > 0 } answer ? " — " + answer : card.Pending ? " — waiting" : "")
        : Text;

    [RelayCommand]
    private void ShowMore() => IsExpanded = true;

    public void Update(ChatEntry entry)
    {
        var cardChanged = entry.Card != Entry.Card;
        var filesChanged = !(entry.Files?.Files ?? []).SequenceEqual(Files.Select(f => f.File)) || entry.Files?.Caption != Entry.Files?.Caption;
        if (KindOf(entry) != Kind)
        {
            // Another kind of item now holds this place (a thought taken into an activity group): it starts folded.
            IsOpen = false;
            IsExpanded = false;
        }

        Entry = entry;
        IsLocal = false;
        Kind = KindOf(entry);
        Text = entry.Text;
        if (filesChanged)
        {
            // Other files hold this place now: their pictures are fetched afresh.
            DisposeFiles();
            Files = FilesOf(entry);
            OnPropertyChanged(nameof(Files));
            OnPropertyChanged(nameof(Caption));
            OnPropertyChanged(nameof(HasCaption));
        }

        if (!(entry.Steps ?? []).SequenceEqual(Steps.Select(s => s.Step)))
        {
            Steps = StepsOf(entry);
            OnPropertyChanged(nameof(Steps));
            if (_stepsLoaded)
            {
                OnPropertyChanged(nameof(VisibleSteps));
            }

            OnPropertyChanged(nameof(HasSteps));
        }

        OnPropertyChanged(nameof(ThinkingText));
        OnPropertyChanged(nameof(From));
        OnPropertyChanged(nameof(HasFrom));
        OnPropertyChanged(nameof(FromText));
        if (cardChanged)
        {
            OnPropertyChanged(nameof(CardText));
        }
    }

    /// <summary>More of the turn streaming now.</summary>
    public void AppendLive(string text) => Text += text;

    /// <summary>
    /// The files as of the transcript's <paramref name="generation"/>: files listed under another generation (the PC reloaded or
    /// rewound the chat since) are made anew, so no picture or saved copy of another file stays in their place.
    /// </summary>
    internal void RenewFiles(int generation)
    {
        if (Files.Any(f => f.Generation is { } had && had != generation))
        {
            DisposeFiles();
            Files = FilesOf(Entry);
            OnPropertyChanged(nameof(Files));
        }

        foreach (var file in Files)
        {
            file.Generation = generation;
        }
    }

    /// <summary>Gone from the chat, or the chat closed: its pictures are freed.</summary>
    public void DisposeFiles()
    {
        foreach (var file in Files)
        {
            file.Dispose();
        }
    }
}

/// <summary>One step of an activity: a tool call with its one line, or a thought.</summary>
public sealed class StepViewModel(ChatStep step)
{
    public ChatStep Step { get; } = step;

    public bool IsTool => Step.Kind == "tool";

    public bool IsThought => Step.Kind == "thinking";

    public string Title => Step.Title;

    public string Detail => Step.Detail ?? "";

    public bool HasDetail => !string.IsNullOrEmpty(Step.Detail);

    public bool Failed => Step.Failed;
}
