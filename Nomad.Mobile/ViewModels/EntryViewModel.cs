using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>One transcript entry. Long text is clipped, with Show more.</summary>
public sealed partial class EntryViewModel : ObservableObject, IDisposable
{
    /// <summary>Text longer than this shows clipped until Show more.</summary>
    public const int ClipAt = 1500;

    private IReadOnlyList<string> _imageData = [];
    private IReadOnlyList<string> _fileNames = [];

    public EntryViewModel(ChatEntry entry)
    {
        Entry = entry;
        Kind = entry.Kind;
        Text = entry.Text;
        Steps = StepsOf(entry);
        _imageData = entry.Images ?? [];
        Images = DecodeImages(_imageData);
        _fileNames = entry.Files ?? [];
        Files = FilesOf(_fileNames);
    }

    private static List<StepViewModel> StepsOf(ChatEntry entry) => [.. (entry.Steps ?? []).Select(s => new StepViewModel(s))];

    private static List<EntryFileViewModel> FilesOf(IReadOnlyList<string> files) => [.. files.Select(f => new EntryFileViewModel(f))];

    private static List<Bitmap> DecodeImages(IReadOnlyList<string> encoded)
    {
        var bitmaps = new List<Bitmap>(encoded.Count);
        foreach (var item in encoded)
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(item));
                bitmaps.Add(new Bitmap(stream));
            }
            catch (Exception)
            {
                // Corrupt or unreadable image data.
            }
        }

        return bitmaps;
    }

    private EntryViewModel(ChatEntry entry, bool local) : this(entry) => IsLocal = local;

    /// <summary>An entry made on the phone from the live stream; the next page replaces it.</summary>
    public static EntryViewModel Local(int index, string kind, string text) => new(new ChatEntry(index, kind, text), local: true);

    public ChatEntry Entry { get; private set; }

    public int Index => Entry.Index;

    public bool IsLocal { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUser), nameof(IsAssistant), nameof(IsActivity), nameof(IsThinking), nameof(IsNotice), nameof(IsError), nameof(IsCard), nameof(IsClipped))]
    public partial string Kind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownText), nameof(HasText), nameof(IsClipped))]
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

    public bool HasSteps => Steps.Count > 0;

    /// <summary>An activity shows its steps, or a thought its text, once tapped open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoldGlyph), nameof(ThinkingText), nameof(ShowsThought))]
    public partial bool IsOpen { get; set; }

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

    /// <summary>Thumbnails of images attached to a user prompt.</summary>
    public IReadOnlyList<Bitmap> Images { get; private set; } = [];

    public bool HasImages => Images.Count > 0;

    /// <summary>Other files attached to a user prompt, shown as chips.</summary>
    public IReadOnlyList<EntryFileViewModel> Files { get; private set; } = [];

    public bool HasFiles => Files.Count > 0;

    public bool HasText => Text.Length > 0;

    /// <summary>Long text shows clipped until Show more; a thought folds by itself instead.</summary>
    public bool IsClipped => !IsThinking && !IsExpanded && Text.Length > ClipAt;

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
        if (entry.Kind != Kind)
        {
            // Another kind of item now holds this place (a thought taken into an activity group): it starts folded.
            IsOpen = false;
            IsExpanded = false;
        }

        Entry = entry;
        IsLocal = false;
        Kind = entry.Kind;
        Text = entry.Text;
        if (!(entry.Steps ?? []).SequenceEqual(Steps.Select(s => s.Step)))
        {
            Steps = StepsOf(entry);
            OnPropertyChanged(nameof(Steps));
            OnPropertyChanged(nameof(HasSteps));
        }

        var incomingImages = entry.Images ?? [];
        if (!incomingImages.SequenceEqual(_imageData))
        {
            DisposeImages();
            _imageData = incomingImages;
            Images = DecodeImages(_imageData);
            OnPropertyChanged(nameof(Images));
            OnPropertyChanged(nameof(HasImages));
        }

        var incomingFiles = entry.Files ?? [];
        if (!incomingFiles.SequenceEqual(_fileNames))
        {
            _fileNames = incomingFiles;
            Files = FilesOf(_fileNames);
            OnPropertyChanged(nameof(Files));
            OnPropertyChanged(nameof(HasFiles));
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

    public void Dispose() => DisposeImages();

    private void DisposeImages()
    {
        foreach (var bitmap in Images)
        {
            bitmap.Dispose();
        }

        Images = [];
    }
}

/// <summary>One non-image file attached to a user prompt, shown as a chip in the bubble.</summary>
public sealed class EntryFileViewModel(string name)
{
    public string Name { get; } = name;

    public string Kind => Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "File";
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
