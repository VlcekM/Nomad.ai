using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// A chat's settings over its transcript: its model, effort and permission mode, as its menus on the PC offer them. A pick is
/// sent to the PC, which applies it as if picked there; what shows is always what the PC then says.
/// </summary>
public sealed partial class ChatSettingsViewModel : ObservableObject
{
    private readonly ChatViewModel _chat;

    public ChatSettingsViewModel(ChatViewModel chat)
    {
        _chat = chat;
        Model = new SettingViewModel("model", "Model");
        Effort = new SettingViewModel("effort", "Effort");
        Mode = new SettingViewModel("mode", "Mode");
        foreach (var setting in Settings)
        {
            setting.Picked += OnPicked;
        }
    }

    public SettingViewModel Model { get; }

    public SettingViewModel Effort { get; }

    public SettingViewModel Mode { get; }

    public IReadOnlyList<SettingViewModel> Settings => [Model, Effort, Mode];

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    /// <summary>The PC lets this chat be renamed, pinned and archived from here (an older PC doesn't say).</summary>
    [ObservableProperty]
    public partial bool HasChatActions { get; set; }

    /// <summary>The chat's name as the PC's list shows it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    public partial string? Title { get; set; }

    /// <summary>The name as being edited here.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    public partial string NewTitle { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    /// <summary>Archive was tapped once: the next tap archives.</summary>
    [ObservableProperty]
    public partial bool ConfirmingArchive { get; set; }

    public string ArchiveText => ConfirmingArchive ? "Tap again to archive" : "Archive";

    partial void OnConfirmingArchiveChanged(bool value) => OnPropertyChanged(nameof(ArchiveText));

    /// <summary>What went wrong, or what the PC said about the last change.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool StatusIsProblem { get; set; }

    public async Task LoadAsync()
    {
        if (_chat.Connection is not { } connection)
        {
            Show("Not connected to the hub.", problem: true);
            return;
        }

        IsLoading = true;
        try
        {
            Apply(await connection.ChatSettingsAsync(_chat.DeviceId, _chat.Backend, _chat.SessionId));
        }
        catch (Exception ex)
        {
            Show("Couldn't get the chat's settings: " + ChatViewModel.Problem(ex), problem: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal void Apply(ChatSettings settings)
    {
        Model.Set(settings.Models, settings.Model);
        Effort.Set(settings.Efforts, settings.Effort);
        Mode.Set(settings.Modes, settings.Mode);
        HasChatActions = settings.Title is not null && settings.Pinned is not null;
        if (settings.Title is { } title && title != Title)
        {
            Title = title;
            NewTitle = title;
        }

        IsPinned = settings.Pinned ?? false;
    }

    private bool CanRename => NewTitle.Trim().Length > 0 && NewTitle.Trim() != Title;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        var result = await _chat.SendInputAsync(new ChatInput("rename", Text: NewTitle.Trim(), Id: Guid.NewGuid().ToString("N")));
        if (result is not null)
        {
            Show(result.Message, problem: !result.Ok);
            if (result.Ok)
            {
                _chat.Title = NewTitle.Trim();
            }
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task TogglePinAsync()
    {
        var result = await _chat.SendInputAsync(new ChatInput("pin", Choice: IsPinned ? "off" : "on", Id: Guid.NewGuid().ToString("N")));
        if (result is not null)
        {
            Show(result.Message, problem: !result.Ok);
        }

        await LoadAsync();
    }

    /// <summary>Archive asks once more first; archived, the chat closes, as it leaves the PC's open chats.</summary>
    [RelayCommand]
    private async Task ArchiveAsync()
    {
        if (!ConfirmingArchive)
        {
            ConfirmingArchive = true;
            return;
        }

        ConfirmingArchive = false;
        var result = await _chat.SendInputAsync(new ChatInput("archive", Id: Guid.NewGuid().ToString("N")));
        if (result is { Ok: true })
        {
            _chat.CloseSettings();
            _chat.LeaveAfterArchive();
            return;
        }

        if (result is not null)
        {
            Show(result.Message, problem: true);
        }
    }

    private async void OnPicked(SettingViewModel setting, ChoiceViewModel choice)
    {
        var result = await _chat.SendInputAsync(new ChatInput(setting.Kind, Choice: choice.Id, Id: Guid.NewGuid().ToString("N")));
        if (result is not null)
        {
            Show(result.Message, problem: !result.Ok);
        }

        // What the PC has now, whether or not it took the change (a model switch changes the efforts on offer, say).
        await LoadAsync();
    }

    private void Show(string text, bool problem)
    {
        Status = text;
        StatusIsProblem = problem;
    }

    [RelayCommand]
    private void Close() => _chat.CloseSettings();
}
