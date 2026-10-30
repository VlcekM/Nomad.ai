using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Nomad.App.ViewModels;

/// <summary>
/// The Files view's small question card, for what the tree's menu does: a name to give (new file, rename), a commit
/// message, or a yes to something that can't be undone (delete, undo changes). The answer's action reports why it
/// could not do it, which the card shows (and stays open).
/// </summary>
public sealed partial class FilePromptViewModel : ObservableObject
{
    private Func<string, Task<string?>>? _action;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>In the visual tree: from opening until it has faded out.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>Faded in: set a frame after <see cref="IsShown"/>, so the fade runs from nothing.</summary>
    [ObservableProperty]
    public partial bool IsRaised { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; private set; } = "";

    public bool HasMessage => Message.Length > 0;

    /// <summary>It asks for text (a name, a message), not only a yes.</summary>
    [ObservableProperty]
    public partial bool HasInput { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string Watermark { get; private set; } = "";

    /// <summary>How much of the text to select at first: a file's name without its extension.</summary>
    public int SelectLength { get; private set; }

    [ObservableProperty]
    public partial string ConfirmText { get; private set; } = "OK";

    /// <summary>The answer can't be undone: its button is red.</summary>
    [ObservableProperty]
    public partial bool IsDanger { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool IsBusy { get; private set; }

    partial void OnTextChanged(string value) => Error = null;

    /// <summary>Asks for a name or other text (<paramref name="text"/> to start from), then runs the action on it.</summary>
    public void AskText(string title, string text, string confirm, Func<string, Task<string?>> action, string watermark = "", int? select = null, string message = "") =>
        Show(title, message, true, text, confirm, false, action, watermark, select ?? text.Length);

    /// <summary>Asks to go ahead with something (red when it can't be undone), then runs the action.</summary>
    public void AskConfirm(string title, string message, string confirm, bool danger, Func<Task<string?>> action) =>
        Show(title, message, false, "", confirm, danger, _ => action(), "", 0);

    private void Show(string title, string message, bool input, string text, string confirm, bool danger, Func<string, Task<string?>> action, string watermark, int select)
    {
        Title = title;
        Message = message;
        HasInput = input;
        SelectLength = select;
        Text = text;
        Watermark = watermark;
        ConfirmText = confirm;
        IsDanger = danger;
        Error = null;
        IsBusy = false;
        _action = action;
        IsOpen = true;
        IsShown = true;
        Dispatcher.UIThread.Post(() => IsRaised = IsOpen, DispatcherPriority.Loaded);
    }

    private bool CanConfirm() => !IsBusy && (!HasInput || Text.Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task Confirm()
    {
        if (_action is not { } action || !CanConfirm())
        {
            return;
        }

        IsBusy = true;
        string? error;
        try
        {
            error = await action(Text.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
        }

        IsBusy = false;
        if (error is null)
        {
            Cancel();
        }
        else
        {
            Error = error;
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        IsRaised = false;
        _action = null;
        DispatcherTimer.RunOnce(() => IsShown = IsOpen, TimeSpan.FromMilliseconds(220));
    }
}
