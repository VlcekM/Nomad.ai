using Nomad.Core.Agents;
using Nomad.Core.Hub;
using Nomad.Hub.Contracts;

namespace Nomad.App.ViewModels;

// Nomad Hub's side of the main view model (docs/hub.md): what the sync engine's events do to the sidebar and the open
// sessions. The rest of MainViewModel only calls the Hub* hooks below.
public sealed partial class MainViewModel
{
    private HubViewModel? _hub;

    public HubViewModel Hub => _hub ??= new HubViewModel(this);

    /// <summary>A session's backend, from its sidebar row (Claude Code when it isn't listed).</summary>
    public string BackendOf(string sessionId) =>
        _all.FirstOrDefault(s => s.SessionId == sessionId)?.Backend ?? AgentBackends.ClaudeCode;

    private void HubActivity(SessionViewModel session) => Hub.SetBusy(session.SessionId, session.IsTurnRunning && !session.IsRelayViewer);

    private void HubTurnCompleted(SessionViewModel session)
    {
        Hub.SetBusy(session.SessionId, false);
        if (session.IsProcessRunning && !session.IsRelayViewer)
        {
            Hub.OnAliveChanged(session.Backend, session.SessionId, running: true); // A new session has its id only now.
        }

        Hub.Wake();
    }

    private void HubAliveChanged(SessionViewModel session)
    {
        // Watching another PC's session holds nothing here.
        if (!session.IsRelayViewer)
        {
            Hub.OnAliveChanged(session.Backend, session.SessionId, session.IsProcessRunning);
        }
    }

    /// <summary>The hub is up and relays: sessions opened before that watch the ones another PC runs now.</summary>
    public void OnHubRelayReady()
    {
        foreach (var session in _open.Values.Where(s => !s.IsProcessRunning).ToList())
        {
            _ = session.WatchIfLiveAsync();
        }
    }

    /// <summary>A prompt, answer or Stop from a PC watching a session this one streams.</summary>
    public void OnHubRelayInput(string sessionId, string input, string from)
    {
        if (_open.TryGetValue(sessionId, out var session) && RelayCodec.DecodeInput(input) is { } decoded)
        {
            _ = session.ApplyRelayInputAsync(decoded, from);
        }
    }

    /// <summary>A session came from the hub: listed (in this PC's folder for it), and an open idle one shows the new turns.</summary>
    public void OnHubSessionPulled(PulledSession pulled)
    {
        if (!string.Equals(pulled.Folder.TrimEnd('\\', '/'), pulled.OriginalFolder?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(pulled.Folder))
        {
            Settings.MovedSessionFolders[pulled.SessionId] = pulled.Folder;
            SaveSettings();
        }

        if (_open.TryGetValue(pulled.SessionId, out var session) && session.CanReloadFromStore)
        {
            _ = session.ReloadFromStoreAsync();
        }

        Hub.QueueRefresh();
    }

    /// <summary>Deleted on another PC: deleted here too, unless it is open with unsaved edits or running.</summary>
    public async Task OnHubSessionDeletedAsync(string sessionId)
    {
        if (_all.FirstOrDefault(s => s.SessionId == sessionId) is not { CanManage: true } item
            || (_open.TryGetValue(sessionId, out var session) && (session.IsTurnRunning || session.Files.EditedFiles.Any())))
        {
            return;
        }

        await FoldAwayAsync(item);
        await DeleteFoldedAsync(item);
        ApplyFilter();
    }

    public void OnHubStateChanged(string sessionId, string key, bool on)
    {
        if (_all.FirstOrDefault(s => s.SessionId == sessionId) is { } item)
        {
            if (key == HubNames.StateArchived)
            {
                item.IsArchived = on;
            }
            else if (key == HubNames.StatePinned)
            {
                item.IsPinned = on;
            }
        }

        SaveSettings();
        ApplyFilter();
    }

    /// <summary>Another PC took over a session this one was running (after its hold lapsed): this process stops.</summary>
    public async Task OnHubLeaseLostAsync(string sessionId)
    {
        if (_open.TryGetValue(sessionId, out var session) && session.IsProcessRunning)
        {
            await session.ShutdownAsync();
            session.ShowHubNotice("This session was continued on another PC, so it stopped here. The next prompt asks for it back.");
            OnAliveChanged(session);
        }
    }

    /// <summary>Another PC asks for a session this one runs: once its turn is over, the process ends and the lease goes back.</summary>
    public async Task OnHubHandoffRequestedAsync(HandoffRequest request)
    {
        if (!_open.TryGetValue(request.SessionId, out var session))
        {
            await Hub.Sync!.ReleaseLeaseAsync(request.Backend, request.SessionId);
            return;
        }

        for (var i = 0; i < 600 && session.IsTurnRunning; i++)
        {
            await Task.Delay(1000);
        }

        if (session.IsTurnRunning)
        {
            return;
        }

        await session.ShutdownAsync();
        session.ShowHubNotice($"Continued on {request.RequestedByName}.");
        OnAliveChanged(session);
    }
}
