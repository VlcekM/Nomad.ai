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
        Kind = entry.Kind;
        Text = entry.Text;
    }

    private EntryViewModel(ChatEntry entry, bool local) : this(entry) => IsLocal = local;

    /// <summary>An entry made on the phone from the live stream; the next page replaces it.</summary>
    public static EntryViewModel Local(int index, string kind, string text) => new(new ChatEntry(index, kind, text), local: true);

    public ChatEntry Entry { get; private set; }

    public int Index => Entry.Index;

    public bool IsLocal { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUser), nameof(IsAssistant), nameof(IsActivity), nameof(IsNotice), nameof(IsError), nameof(IsCard))]
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

    public bool IsNotice => Kind == "notice";

    public bool IsError => Kind == "error";

    public bool IsCard => Kind is "permission" or "question";

    /// <summary>Who sent a prompt that didn't come from the PC itself: a phone, another PC, or another chat.</summary>
    public string? From => Entry.From;

    public bool HasFrom => !string.IsNullOrEmpty(Entry.From);

    public string FromText => "from " + Entry.From;

    public bool IsClipped => !IsExpanded && Text.Length > ClipAt;

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
        Entry = entry;
        IsLocal = false;
        Kind = entry.Kind;
        Text = entry.Text;
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
}
