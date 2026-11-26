using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// A new chat, as the new-session page on a PC starts one: which PC, which agent, which folder, the model, effort and mode
/// (each PC's own choices and defaults, fetched from it), and the first message. Started, it opens once it shows on the board.
/// </summary>
public sealed partial class NewChatViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private NewChatOptions? _options;
    private string? _startId;

    public NewChatViewModel(MainViewModel main)
    {
        _main = main;
        Pc = new SettingViewModel("pc", "PC");
        Agent = new SettingViewModel("agent", "Agent");
        Folder = new SettingViewModel("folder", "Folder");
        Model = new SettingViewModel("model", "Model");
        Effort = new SettingViewModel("effort", "Effort");
        Mode = new SettingViewModel("mode", "Mode");
        Pc.Picked += (_, _) => _ = LoadAsync();
        Agent.Picked += (_, choice) => ShowAgent(choice.Id);
        Pc.Set(main.Chats.Devices.Select(d => new ChatChoice(d.DeviceId.ToString(), d.DeviceName)), null, orFirst: true);
    }

    public SettingViewModel Pc { get; }

    public SettingViewModel Agent { get; }

    public SettingViewModel Folder { get; }

    public SettingViewModel Model { get; }

    public SettingViewModel Effort { get; }

    public SettingViewModel Mode { get; }

    /// <summary>The settings in the order they show; the PC only when there is more than one.</summary>
    public IReadOnlyList<SettingViewModel> Settings => [Pc, Agent, Folder, Model, Effort, Mode];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string Prompt { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsStarting { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool StatusIsProblem { get; set; }

    private Guid? DeviceId => Guid.TryParse(Pc.Selected?.Id, out var id) ? id : null;

    /// <summary>Fetches what a new chat on the chosen PC can be.</summary>
    public async Task LoadAsync()
    {
        _options = null;
        if (_main.Connection is not { } connection || DeviceId is not { } device)
        {
            IsLoading = false;
            Show(_main.Connection is null ? "Not connected to the hub." : "No PC is sharing its chats now.", problem: true);
            return;
        }

        IsLoading = true;
        Status = null;
        try
        {
            var options = await connection.NewChatOptionsAsync(device);
            if (DeviceId != device)
            {
                return; // Another PC was picked meanwhile.
            }

            _options = options;
            Agent.Set(options.Agents.Select(a => new ChatChoice(a.Id, a.Name)), Agent.Selected?.Id ?? options.DefaultAgent, orFirst: true);
            Folder.Set([.. options.Folders.Select(f => new ChatChoice(f, FolderName(f), f)), new ChatChoice("", "No folder")],
                Folder.Selected?.Id ?? options.Folders.FirstOrDefault() ?? "", orFirst: true);
            ShowAgent(Agent.Selected?.Id);
        }
        catch (Exception ex)
        {
            Show("Couldn't ask the PC: " + ChatViewModel.Problem(ex), problem: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>The agent's own models, efforts and modes, with that PC's defaults for a new chat picked.</summary>
    private void ShowAgent(string? id)
    {
        if (_options?.Agents.FirstOrDefault(a => a.Id == id) is not { } agent)
        {
            return;
        }

        Model.Set(agent.Settings.Models, agent.Settings.Model, orFirst: true);
        Effort.Set(agent.Settings.Efforts, agent.Settings.Effort);
        Mode.Set(agent.Settings.Modes, agent.Settings.Mode, orFirst: true);
    }

    private bool CanStart => !IsLoading && !IsStarting && _options is not null && Prompt.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_main.Connection is not { } connection || DeviceId is not { } device || Agent.Selected is not { } agent)
        {
            return;
        }

        IsStarting = true;
        Show("Starting…", problem: false);
        var before = _main.Chats.Groups.FirstOrDefault(g => g.DeviceId == device)?.Chats.Select(c => c.SessionId).ToHashSet() ?? [];
        var chat = new NewChat(agent.Id, Folder.Selected is { Id.Length: > 0 } folder ? folder.Id : null,
            Model.Selected?.Id, Effort.Selected?.Id, Mode.Selected?.Id, Prompt.Trim(), _startId ??= Guid.NewGuid().ToString("N"));
        try
        {
            var result = await connection.ChatStartAsync(device, chat);
            if (!result.Ok)
            {
                Show(result.Message, problem: true);
                return;
            }

            _startId = null;
            if (await WaitForRowAsync(device, agent.Id, result.SessionId, before) is { } row)
            {
                _main.OpenChatRow(row);
            }
            else
            {
                Show("Started. It shows in the chats once it has begun.", problem: false);
                _main.GoBack();
            }
        }
        catch (Exception ex)
        {
            Show("Couldn't reach the PC: " + ChatViewModel.Problem(ex) + " Start again to retry; it won't start twice.", problem: true);
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>
    /// The new chat's row, once the PC has put it on the board (it does every second): by its session id, or, when the PC
    /// answered before it had one, the first chat of that agent on that PC that wasn't there before.
    /// </summary>
    private async Task<ChatRowViewModel?> WaitForRowAsync(Guid device, string backend, string? sessionId, HashSet<string> before)
    {
        for (var i = 0; i < 60; i++)
        {
            var row = sessionId is not null
                ? _main.Chats.Find(device, backend, sessionId)
                : _main.Chats.Groups.FirstOrDefault(g => g.DeviceId == device)?.Chats.FirstOrDefault(c => c.Backend == backend && !before.Contains(c.SessionId));
            if (row is not null)
            {
                return row;
            }

            await Task.Delay(500);
        }

        return null;
    }

    /// <summary>A PC folder's own name ("Nomad.ai"): split by hand, as the phone's own paths use only '/'.</summary>
    private static string FolderName(string path) =>
        path.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() is { Length: > 0 } name ? name : path;

    private void Show(string? text, bool problem)
    {
        Status = text;
        StatusIsProblem = problem;
    }

    [RelayCommand]
    private void Back() => _main.GoBack();
}
