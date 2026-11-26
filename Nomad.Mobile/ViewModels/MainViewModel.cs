using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.Hub.Contracts;
using Nomad.Mobile.Hub;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// The app: which screen shows (Connect, Chats, a Chat, Settings), this phone's enrollment, and its one hub connection. When the
/// connection comes back (a drop, or the app returning from the background) the board and the open chat are fetched again, as
/// docs/mobile.md §5 says: nothing is kept on the phone, so nothing it shows can go stale.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISecretStore _store;
    private readonly IHubFactory _hubs;

    public MainViewModel(ISecretStore store, IHubFactory hubs)
    {
        _store = store;
        _hubs = hubs;
        Chats = new ChatsViewModel(this);
        Connect = new ConnectViewModel(this);
        Page = Connect;
    }

    public ChatsViewModel Chats { get; }

    public ConnectViewModel Connect { get; }

    /// <summary>The screen showing: a <see cref="ConnectViewModel"/>, <see cref="ChatsViewModel"/>, <see cref="ChatViewModel"/> or <see cref="SettingsViewModel"/>.</summary>
    [ObservableProperty]
    public partial object Page { get; set; }

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    public Enrollment? Enrollment { get; private set; }

    public IHubConnection? Connection { get; private set; }

    /// <summary>The open chat, if a chat screen shows.</summary>
    public ChatViewModel? OpenChat => Page as ChatViewModel;

    /// <summary>Signs in with the saved enrollment, or shows Connect when there is none.</summary>
    public void Start()
    {
        if (Enrollment.Load(_store) is { } saved)
        {
            _ = UseAsync(saved);
        }
        else
        {
            Page = Connect;
        }
    }

    /// <summary>A <c>nomad://enroll</c> link from the camera app (or pasted): Connect, filled in.</summary>
    public void HandleLink(string link)
    {
        if (EnrollLink.TryParse(link) is { } parsed)
        {
            Connect.Fill(parsed.Hub, parsed.Token);
            ShowPage(Connect);
        }
    }

    /// <summary>Enrolls this phone with a hub and signs in.</summary>
    public async Task EnrollAsync(Uri hub, string token, string deviceName, CancellationToken ct = default)
    {
        var response = await _hubs.EnrollAsync(hub, token, deviceName, ct);
        var enrollment = new Enrollment(hub, response.DeviceId, deviceName, response.Secret);

        // A phone that was signed in elsewhere leaves that hub; its old device stays listed there until revoked from a PC.
        await CloseConnectionAsync();
        enrollment.Save(_store);
        await UseAsync(enrollment);
    }

    private async Task UseAsync(Enrollment enrollment)
    {
        Enrollment = enrollment;
        var connection = _hubs.Open(enrollment.Hub, enrollment.Secret);
        Connection = connection;
        connection.ConnectionChanged += up => AppServices.Post(() => OnConnectionChanged(connection, up));
        connection.BoardChanged += device => AppServices.Post(() =>
        {
            if (ReferenceEquals(Connection, connection))
            {
                Chats.Apply(device);
                OpenChat?.OnBoardChanged(device);
            }
        });
        connection.RelayEvents += (backend, id, events, first) => AppServices.Post(() =>
        {
            if (ReferenceEquals(Connection, connection))
            {
                OpenChat?.OnRelayEvents(backend, id, events, first);
            }
        });
        connection.RelayEnded += (backend, id) => AppServices.Post(() =>
        {
            if (ReferenceEquals(Connection, connection))
            {
                OpenChat?.OnRelayEnded(backend, id);
            }
        });

        ShowPage(Chats);
        await ConnectNowAsync();
    }

    private async Task ConnectNowAsync()
    {
        if (Connection is not { } connection)
        {
            return;
        }

        Chats.Status = "Connecting to the hub…";
        try
        {
            await connection.ConnectAsync();
            if (ReferenceEquals(Connection, connection) && connection.IsConnected && !IsConnected)
            {
                OnConnectionChanged(connection, true);
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(Connection, connection))
            {
                Chats.Status = "Couldn't reach the hub: " + ex.Message;
            }
        }
    }

    private void OnConnectionChanged(IHubConnection connection, bool up)
    {
        if (!ReferenceEquals(Connection, connection) || IsConnected == up)
        {
            return;
        }

        IsConnected = up;
        if (up)
        {
            // Everything is fetched again: the board, and the open chat's live turn and newest page.
            _ = Chats.RefreshAsync();
            if (OpenChat is { } chat)
            {
                _ = chat.ReattachAsync();
            }
        }
        else
        {
            Chats.Status = "Reconnecting to the hub…";
        }
    }

    /// <summary>The app came back from the background. A connection Android closed meanwhile is opened again.</summary>
    public void OnResumed()
    {
        if (Connection is { IsConnected: false })
        {
            IsConnected = false;
            _ = ConnectNowAsync();
        }
    }

    public void OpenChatRow(ChatRowViewModel row)
    {
        if (Connection is null)
        {
            return;
        }

        var chat = new ChatViewModel(this, row);
        ShowPage(chat);
        _ = chat.AttachAsync();
    }

    public void OpenSettings() => ShowPage(new SettingsViewModel(this));

    public void ShowConnect()
    {
        Connect.Reset();
        ShowPage(Connect);
    }

    /// <summary>The back button: a chat or Settings goes back to the chats. False when there is nowhere to go back to.</summary>
    public bool GoBack()
    {
        if (Page is ChatViewModel or SettingsViewModel || (Page is ConnectViewModel && Connection is not null))
        {
            ShowPage(Chats);
            return true;
        }

        return false;
    }

    private void ShowPage(object page)
    {
        if (ReferenceEquals(Page, page))
        {
            return;
        }

        (Page as ChatViewModel)?.Close();
        Page = page;
        OnPropertyChanged(nameof(OpenChat));
    }

    /// <summary>Signs out: revokes this phone on the hub (best effort), forgets the enrollment and shows Connect.</summary>
    public async Task<string?> SignOutAsync()
    {
        string? problem = null;
        if (Connection is { } connection && Enrollment is { } enrollment)
        {
            try
            {
                await connection.RevokeDeviceAsync(enrollment.DeviceId);
            }
            catch (Exception ex)
            {
                problem = $"The hub couldn't be told ({ex.Message}). Revoke \"{enrollment.DeviceName}\" in Settings › Hub on a PC.";
            }
        }

        await CloseConnectionAsync();
        Enrollment.Clear(_store);
        Enrollment = null;
        Chats.Clear();
        Connect.Reset();
        Connect.Error = problem;
        ShowPage(Connect);
        return problem;
    }

    private async Task CloseConnectionAsync()
    {
        if (Connection is { } old)
        {
            Connection = null;
            IsConnected = false;
            try
            {
                await old.DisposeAsync();
            }
            catch
            {
                // Closing a connection that already failed has nothing left to report.
            }
        }
    }

    /// <summary>The PCs on the board, for Settings.</summary>
    public IReadOnlyList<BoardDevice> Devices => Chats.Devices;
}
