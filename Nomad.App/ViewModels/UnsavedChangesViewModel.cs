using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Nomad.App.ViewModels;

/// <summary>
/// Asks what to do with files edited in the Files view before they would be lost (quitting, deleting their session):
/// save them all, drop the edits, or stay. A dialog over the window that fades in and back out.
/// </summary>
public sealed partial class UnsavedChangesViewModel : ObservableObject
{
    private TaskCompletionSource<bool>? _answer;

    /// <summary>The edited files, as they are listed.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<FileTab> Files { get; private set; } = [];

    /// <summary>What happens to the edits otherwise ("If you quit without saving, …").</summary>
    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>A file that couldn't be written (it stays open, with the reason on its tab).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>In the visual tree: from opening until it has faded out.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>Faded in: set a frame after <see cref="IsShown"/>, so the fade runs from nothing.</summary>
    [ObservableProperty]
    public partial bool IsRaised { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand), nameof(DiscardCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>
    /// True when the edits were saved or dropped and <paramref name="action"/> ("quit", "delete this chat") can go
    /// ahead; false to stay. With no edited files it answers at once.
    /// </summary>
    public Task<bool> AskAsync(IEnumerable<FileTab> files, string action)
    {
        var edited = files.Where(f => f.IsDirty).Distinct().ToList();
        if (edited.Count == 0)
        {
            return Task.FromResult(true);
        }

        _answer?.TrySetResult(false);
        _answer = new TaskCompletionSource<bool>();
        Files = edited;
        Message = $"If you {action} without saving, {(edited.Count == 1 ? "the edits are" : "their edits are")} lost.";
        Title = edited.Count == 1 ? $"Save your changes to {edited[0].Name}?" : $"Save your changes to {edited.Count} files?";
        Error = null;
        IsOpen = true;
        IsShown = true;
        Dispatcher.UIThread.Post(() => IsRaised = IsOpen, DispatcherPriority.Loaded);
        return _answer.Task;
    }

    private bool CanAnswer() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private async Task SaveAll()
    {
        IsSaving = true;
        try
        {
            foreach (var file in Files)
            {
                if (!await file.SaveAsync())
                {
                    Error = $"Couldn't save {file.Name}: {file.Note?.Replace("Couldn't save: ", "")}";
                    return;
                }
            }
        }
        finally
        {
            IsSaving = false;
        }

        Answer(true);
    }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void Discard() => Answer(true);

    [RelayCommand]
    public void Cancel() => Answer(false);

    private void Answer(bool goOn)
    {
        IsOpen = false;
        IsRaised = false;
        DispatcherTimer.RunOnce(() => IsShown = IsOpen, TimeSpan.FromMilliseconds(220));
        var answer = _answer;
        _answer = null;
        answer?.TrySetResult(goOn);
    }
}
