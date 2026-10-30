using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Core.Agents;
using Nomad.Core.Codex;
using Nomad.Core.Hub;
using Nomad.Core.OpenCode;
using Nomad.Hub.Contracts;

namespace Nomad.App.ViewModels;

/// <summary>A PC on the hub, as Settings › Hub lists it.</summary>
public sealed record HubDeviceRow(DeviceDto Device)
{
    public string Name => Device.IsThis ? Device.Name + " (this PC)" : Device.Name;

    public string Seen => Device.Online ? "Online" : "Last seen " + Device.LastSeenUtc.ToLocalTime().ToString("g");

    public bool CanRevoke => !Device.IsThis;
}

/// <summary>
/// Nomad Hub on this PC (docs/hub.md): enrolling, the sync engine's lifetime, and its events turned into what the
/// sidebar and the open sessions do. Settings › Hub binds to it.
/// </summary>
public sealed partial class HubViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ConcurrentDictionary<string, bool> _busy = new(StringComparer.Ordinal);
    private HubSync? _sync;
    private HashSet<string> _archived = [];
    private HashSet<string> _pinned = [];
    private bool _refreshQueued;

    public HubViewModel(MainViewModel main)
    {
        _main = main;
        Url = main.Settings.HubUrl ?? "";
        DeviceName = main.Settings.HubDeviceName ?? Environment.MachineName;
    }

    public HubSync? Sync => _sync;

    [ObservableProperty]
    public partial string Url { get; set; }

    [ObservableProperty]
    public partial string Token { get; set; } = "";

    [ObservableProperty]
    public partial string DeviceName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; set; }

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(AddDeviceCommand), nameof(SyncNowCommand))]
    public partial bool IsWorking { get; set; }

    /// <summary>A one-time token for another PC, after Add device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewToken))]
    public partial string? NewToken { get; set; }

    public bool HasNewToken => !string.IsNullOrEmpty(NewToken);

    public bool IsEnrolled => _main.Settings.HubDeviceId is not null && !string.IsNullOrEmpty(_main.Settings.HubSecret);

    public bool IsNotEnrolled => !IsEnrolled;

    public ObservableCollection<HubDeviceRow> Devices { get; } = [];

    /// <summary>Whether the hub relays live sessions (its <c>Hub:RelayEnabled</c>); asked once connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RelayOffOnHub))]
    public partial bool HubRelays { get; set; }

    private bool _hubAsked;

    public bool RelayOffOnHub => _hubAsked && !HubRelays;

    /// <summary>This PC streams its running sessions (Settings › Hub).</summary>
    public bool StreamLive => _main.Settings.HubRelay;

    [RelayCommand]
    private void ToggleStreamLive()
    {
        _main.Settings.HubRelay = !_main.Settings.HubRelay;
        _main.SaveSettings();
        OnPropertyChanged(nameof(StreamLive));
    }

    // ------------------------------------------------------------------ lifetime

    /// <summary>Starts syncing if this PC is enrolled (not in scripted dev runs, so test sessions stay off the hub).</summary>
    public void Start()
    {
        if (_sync is not null || !IsEnrolled || (_main.Dev.IsActive && !_main.Dev.Hub) || HubClient.Unprotect(_main.Settings.HubSecret) is not { } secret
            || !Uri.TryCreate(_main.Settings.HubUrl, UriKind.Absolute, out var url))
        {
            return;
        }

        var backends = new List<ISessionFiles> { new ClaudeCodeFiles(_main.Store) };
        if (CodexServer.HasData)
        {
            backends.Add(new CodexFiles(CodexServer.CodexHome));
        }

        if (OpenCodeServer.HasData)
        {
            backends.Add(new OpenCodeFiles(() => OpenCodeServer.ResolveExecutable(OpenCodeServer.CliPath)));
        }

        var statePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_main.SettingsPath))!, "hub-sync.json");
        _sync = new HubSync(new HubClient(url, secret), backends, statePath, (_, id) => _busy.ContainsKey(id));
        _sync.Status += s => Dispatcher.UIThread.Post(() => Status = s);
        _sync.ConnectionChanged += up => Dispatcher.UIThread.Post(() =>
        {
            IsConnected = up;
            if (up && !_hubAsked)
            {
                _ = AskHubAsync();
            }
        });
        _sync.Client.RelayInput += (backend, id, input, from) => Dispatcher.UIThread.Post(() => _main.OnHubRelayInput(id, input, from));
        _sync.SessionPulled += p => Dispatcher.UIThread.Post(() => _main.OnHubSessionPulled(p));
        _sync.SessionDeleted += (backend, id) => Dispatcher.UIThread.Post(() => _ = _main.OnHubSessionDeletedAsync(id));
        _sync.StateChanged += s => Dispatcher.UIThread.Post(() => ApplyState(s));
        _sync.LeaseLost += key => Dispatcher.UIThread.Post(() => _ = _main.OnHubLeaseLostAsync(key.SessionId));
        _sync.HandoffRequested += r => Dispatcher.UIThread.Post(() => _ = _main.OnHubHandoffRequestedAsync(r));
        _main.LocalTokens.HubTotals = UsageSinceAsync;
        _sync.Start();

        // The flags as they are now are sent once, on the first sync; after that only their changes.
        _archived = _sync.IsFirstSync ? [] : [.. _main.Settings.ArchivedSessions];
        _pinned = _sync.IsFirstSync ? [] : [.. _main.Settings.PinnedSessions];
        if (_sync.IsFirstSync)
        {
            OnSettingsSaved();
        }
    }

    public async Task StopAsync()
    {
        if (_sync is { } sync)
        {
            _sync = null;
            IsConnected = false;
            _main.LocalTokens.HubTotals = null;
            await sync.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ what the app tells the hub

    public void SetBusy(string? sessionId, bool busy)
    {
        if (sessionId is null)
        {
            return;
        }

        if (busy)
        {
            _busy[sessionId] = true;
        }
        else
        {
            _busy.TryRemove(sessionId, out _);
        }
    }

    public void Wake() => _sync?.Wake();

    /// <summary>The settings were saved: sends the archived and pinned flags that changed since the last time.</summary>
    public void OnSettingsSaved()
    {
        if (_sync is not { } sync)
        {
            return;
        }

        Send(_archived, _main.Settings.ArchivedSessions, HubNames.StateArchived);
        Send(_pinned, _main.Settings.PinnedSessions, HubNames.StatePinned);

        void Send(HashSet<string> before, List<string> now, string key)
        {
            var current = now.ToHashSet(StringComparer.Ordinal);
            foreach (var id in current.Except(before).ToList())
            {
                _ = sync.SetStateAsync(_main.BackendOf(id), id, key, "1");
            }

            foreach (var id in before.Except(current).ToList())
            {
                _ = sync.SetStateAsync(_main.BackendOf(id), id, key, null);
            }

            before.Clear();
            before.UnionWith(current);
        }
    }

    private void ApplyState(SessionStateDto state)
    {
        var (list, known) = state.Key switch
        {
            HubNames.StateArchived => (_main.Settings.ArchivedSessions, _archived),
            HubNames.StatePinned => (_main.Settings.PinnedSessions, _pinned),
            _ => (null, null),
        };
        if (list is null || known is null)
        {
            return;
        }

        var on = state.Value is not null;
        if (on == list.Contains(state.SessionId))
        {
            return;
        }

        if (on)
        {
            list.Add(state.SessionId);
            known.Add(state.SessionId);
        }
        else
        {
            list.Remove(state.SessionId);
            known.Remove(state.SessionId);
        }

        _main.OnHubStateChanged(state.SessionId, state.Key, on);
    }

    private async Task AskHubAsync()
    {
        if (_sync is not { } sync)
        {
            return;
        }

        try
        {
            HubRelays = (await sync.Client.InfoAsync()).RelayEnabled;
            _hubAsked = true;
            OnPropertyChanged(nameof(RelayOffOnHub));
            if (HubRelays)
            {
                _main.OnHubRelayReady();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    // ------------------------------------------------------------------ the live relay (docs/hub.md §8)

    /// <summary>Streams a session this PC runs, while this PC's switch and the hub allow; null without a hub.</summary>
    public RelayPublisher? Publish(string backend, Func<string?> sessionId, IAgentBackend source) =>
        _sync is { } sync ? new RelayPublisher(sync.Client, backend, sessionId, source, () => HubRelays && _main.Settings.HubRelay) : null;

    /// <summary>The session as another PC runs it, to watch, when that PC streams it now; null otherwise.</summary>
    public async Task<RelayBackend?> WatchAsync(string backend, string sessionId, CancellationToken ct = default)
    {
        if (_sync is not { } sync || !HubRelays || !sync.Client.IsConnected || sync.Holds(backend, sessionId))
        {
            return null;
        }

        try
        {
            if ((await sync.Client.WatchAsync(backend, sessionId, ct)).Live)
            {
                return new RelayBackend(sync.Client, backend, sessionId);
            }

            await sync.Client.UnwatchAsync(backend, sessionId, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or Microsoft.AspNetCore.SignalR.HubException)
        {
        }

        return null;
    }

    /// <summary>
    /// Before this PC runs a session: takes its lease. When another PC runs it, returns that PC's stream to watch, or
    /// throws (shown in the transcript) after asking that PC to hand it over; then fetches the newest version so the CLI
    /// resumes from it.
    /// </summary>
    public async Task<RelayBackend?> EnsureHostAsync(string backend, string sessionId)
    {
        if (_sync is not { } sync || sync.Holds(backend, sessionId))
        {
            return null;
        }

        // Never long: a hub out of reach (a laptop away from home) counts as no hub, and the session runs.
        using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lease = await sync.TakeLeaseAsync(backend, sessionId, quick.Token);
        if (lease is { Granted: false })
        {
            if (await WatchAsync(backend, sessionId, quick.Token) is { } relay)
            {
                return relay;
            }

            var asked = await sync.Client.RequestHandoffAsync(backend, sessionId, quick.Token).ConfigureAwait(true);
            throw new InvalidOperationException(asked
                ? $"This session is running on {lease.HolderName}. Nomad asked it to hand the session over; send again in a moment."
                : $"This session is running on {lease.HolderName}, which is offline. It can be continued here once its hold lapses (two minutes after it was last seen).");
        }

        if (lease is not null)
        {
            try
            {
                using var fetch = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await sync.PullOneAsync(backend, sessionId, fetch.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
            }
        }

        return null;
    }

    /// <summary>A session's process started or ended here: holds its lease while it runs, hands it back after.</summary>
    public void OnAliveChanged(string backend, string? sessionId, bool running)
    {
        if (_sync is not { } sync || sessionId is null)
        {
            return;
        }

        if (running && !sync.Holds(backend, sessionId))
        {
            _ = sync.TakeLeaseAsync(backend, sessionId);
        }
        else if (!running)
        {
            _ = sync.ReleaseLeaseAsync(backend, sessionId);
        }
    }

    public void OnDeleted(string backend, string sessionId) => _ = _sync?.DeleteAsync(backend, sessionId);

    /// <summary>Lists the sessions again after fetches, once per burst.</summary>
    public void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _refreshQueued = false;
            _ = _main.RefreshSessionsAsync();
        }, TimeSpan.FromMilliseconds(600));
    }

    // ------------------------------------------------------------------ Settings › Hub

    private bool CanAct() => !IsWorking;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ConnectAsync()
    {
        if (!Uri.TryCreate(Url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            Status = "The hub's address, such as https://hub.example.com.";
            return;
        }

        IsWorking = true;
        try
        {
            var name = string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();
            var enrolled = await HubClient.EnrollAsync(url, Token.Trim(), name);
            _main.Settings.HubUrl = url.ToString();
            _main.Settings.HubDeviceId = enrolled.DeviceId;
            _main.Settings.HubDeviceName = name;
            _main.Settings.HubSecret = HubClient.Protect(enrolled.Secret);
            _main.SaveSettings();
            Token = "";
            Status = $"Connected to Nomad Hub {enrolled.HubVersion}. The first sync uploads every session here.";
            OnPropertyChanged(nameof(IsEnrolled));
            OnPropertyChanged(nameof(IsNotEnrolled));
            Start();
            await RefreshDevicesAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await StopAsync();
        _main.Settings.HubDeviceId = null;
        _main.Settings.HubSecret = null;
        _main.SaveSettings();
        Devices.Clear();
        NewToken = null;
        Status = "This PC no longer syncs. Its sessions stay here and on the hub.";
        OnPropertyChanged(nameof(IsEnrolled));
        OnPropertyChanged(nameof(IsNotEnrolled));
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task AddDeviceAsync()
    {
        if (_sync is not { } sync)
        {
            return;
        }

        IsWorking = true;
        try
        {
            var token = await sync.Client.NewEnrollTokenAsync();
            NewToken = token.Token;
            Status = $"Enter this on the other PC (Settings › Hub) before {token.ExpiresUtc.ToLocalTime():g}. It works once.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SyncNowAsync()
    {
        if (_sync is not { } sync)
        {
            return;
        }

        IsWorking = true;
        try
        {
            Status = "Syncing…";
            await sync.SyncNowAsync();
            Status = $"Synced {DateTime.Now:t}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Status = "Sync failed: " + ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task RevokeAsync(HubDeviceRow row)
    {
        if (_sync is not { } sync)
        {
            return;
        }

        try
        {
            await sync.Client.RevokeDeviceAsync(row.Device.Id);
            Devices.Remove(row);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Status = ex.Message;
        }
    }

    public async Task RefreshDevicesAsync()
    {
        if (_sync is not { } sync)
        {
            return;
        }

        try
        {
            var devices = await sync.Client.DevicesAsync();
            Devices.Clear();
            foreach (var d in devices)
            {
                Devices.Add(new HubDeviceRow(d));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Status = "Hub unreachable: " + ex.Message;
        }
    }

    /// <summary>Token use of every PC on the hub since a time, by backend: (input incl. cache, output).</summary>
    public async Task<IReadOnlyDictionary<string, (long Input, long Output)>?> UsageSinceAsync(DateTime fromUtc)
    {
        if (_sync is not { } sync)
        {
            return null;
        }

        try
        {
            var report = await sync.Client.UsageAsync(fromUtc, DateTime.UtcNow.AddMinutes(1));
            return report.Rows.GroupBy(r => r.Backend).ToDictionary(g => g.Key,
                g => (g.Sum(r => r.Input + r.CacheRead + r.CacheWrite), g.Sum(r => r.Output)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

}
