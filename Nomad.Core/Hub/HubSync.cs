using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nomad.Core.Agents;
using Nomad.Core.Sessions;
using Nomad.Hub.Contracts;

namespace Nomad.Core.Hub;

/// <summary>
/// A session the hub brought to this PC (new, or a newer version), and the folder it belongs to here. <see cref="Title"/> and
/// <see cref="LastActivityUtc"/> list it where the backend keeps no list of its own (Antigravity).
/// </summary>
public sealed record PulledSession(string Backend, string SessionId, string Folder, string? OriginalFolder, bool IsNew, string Title, DateTime LastActivityUtc);

/// <summary>Live state of the hub sync engine (for the sidebar spinner and Settings › Hub).</summary>
public sealed record SyncProgress(
    bool IsSyncing,
    string Phase,
    int Completed,
    int Total,
    string? CurrentItem = null,
    DateTime? LastSyncedUtc = null,
    string? Error = null);

/// <summary>Whether one specific chat is up to date with Nomad Hub.</summary>
public enum SessionSyncStatus
{
    None,
    Synced,
    Syncing,
    Pending,
}

/// <summary>
/// Keeps this PC's sessions and the hub's in step (docs/hub.md): uploads what changed here, fetches what changed
/// elsewhere, renews this PC's leases and sends its usage. All work runs on one background loop, one step at a time.
/// </summary>
public sealed class HubSync : IAsyncDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);
    private const int UsageBatchSize = 500;

    private readonly HubClient _client;
    private readonly IReadOnlyDictionary<string, ISessionFiles> _backends;
    private readonly string _statePath;
    private readonly Func<string, string, bool> _isBusy;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _leaseLock = new();
    private readonly HashSet<LeaseKey> _leases = [];
    private SyncStateFile _state;
    private volatile bool _pushWanted = true;
    private volatile bool _pullWanted = true;
    private Task? _loop;
    private Task? _renewals;
    private Guid _me;
    private Dictionary<string, string> _remoteHosts = [];
    private volatile SyncProgress _progress = new(false, "Idle", 0, 0);
    private volatile string? _activeSessionKey;
    private DateTime? _lastSyncedUtc;

    /// <param name="isBusy">Whether a session is mid-turn on this PC (its files are still being written): not touched until it isn't.</param>
    public HubSync(HubClient client, IEnumerable<ISessionFiles> backends, string statePath, Func<string, string, bool> isBusy)
    {
        _client = client;
        _backends = backends.ToDictionary(b => b.Backend);
        _statePath = statePath;
        _isBusy = isBusy;
        _state = Load(statePath, client.BaseUrl);
        IsFirstSync = _state.Seq == 0 && _state.Sessions.Count == 0;
        client.Changed += _ => Wake(pull: true);
        client.ConnectionChanged += up =>
        {
            if (up)
            {
                Wake(push: true, pull: true);
            }
            else if (_remoteHosts.Count > 0)
            {
                _remoteHosts = []; // Not known while offline.
                RemoteHostsChanged?.Invoke(_remoteHosts);
            }

            ConnectionChanged?.Invoke(up);
        };
        client.HandoffRequested += r => HandoffRequested?.Invoke(r);
    }

    public HubClient Client => _client;

    public Guid DeviceId => _me;

    /// <summary>This PC hasn't synced with this hub yet (no state file for it).</summary>
    public bool IsFirstSync { get; }

    /// <summary>A session came from the hub (raised on the sync loop's thread).</summary>
    public event Action<PulledSession>? SessionPulled;

    /// <summary>A session was deleted on another PC.</summary>
    public event Action<string, string>? SessionDeleted;

    /// <summary>A per-session flag changed on the hub (archived, pinned, …), this PC's own changes included.</summary>
    public event Action<SessionStateDto>? StateChanged;

    /// <summary>Another PC took a lease this PC held (it had lapsed): stop that session's process.</summary>
    public event Action<LeaseKey>? LeaseLost;

    public event Action<HandoffRequest>? HandoffRequested;

    /// <summary>The sessions other PCs run now changed: session id to that PC's name.</summary>
    public event Action<IReadOnlyDictionary<string, string>>? RemoteHostsChanged;

    public event Action<bool>? ConnectionChanged;

    /// <summary>What the loop last did, or its last error.</summary>
    public event Action<string>? Status;

    /// <summary>Raised whenever sync starts, advances through items, finishes or fails.</summary>
    public event Action<SyncProgress>? ProgressChanged;

    /// <summary>A session's sync state changed (pushed, pulled, or a round finished).</summary>
    public event Action<string, string>? SessionSynced;

    public SyncProgress CurrentProgress => _progress;

    private void ReportProgress(bool isSyncing, string phase, int completed = 0, int total = 0, string? currentItem = null, string? error = null)
    {
        var next = new SyncProgress(isSyncing, phase, completed, total, currentItem, _lastSyncedUtc, error);
        _progress = next;
        ProgressChanged?.Invoke(next);
    }

    /// <summary>Whether a specific chat is synced with the hub, currently syncing, or has pending changes.</summary>
    public (SessionSyncStatus Status, string Detail) GetSessionSyncStatus(string backend, string sessionId)
    {
        var key = Key(backend, sessionId);
        if (_activeSessionKey == key)
        {
            return (SessionSyncStatus.Syncing, "Syncing with Nomad Hub…");
        }

        SyncedSession? known;
        bool retrying;
        lock (_state)
        {
            known = _state.Sessions.GetValueOrDefault(key);
            retrying = _state.Retry.Contains(key);
        }

        if (retrying)
        {
            return (SessionSyncStatus.Pending, "Waiting to fetch latest version from Nomad Hub");
        }

        if (_isBusy(backend, sessionId))
        {
            return (SessionSyncStatus.Pending, "Turn running; will sync to Nomad Hub when finished");
        }

        string? localStamp = null;
        if (_backends.TryGetValue(backend, out var files))
        {
            try
            {
                localStamp = files.StampOf(sessionId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (known is not null)
        {
            if (localStamp is null || localStamp == known.Stamp)
            {
                return (SessionSyncStatus.Synced, $"Synced with Nomad Hub (v{known.Version})");
            }

            if (_progress.IsSyncing)
            {
                return (SessionSyncStatus.Syncing, "Local changes queued to upload to Nomad Hub…");
            }

            return (SessionSyncStatus.Pending, _client.IsConnected
                ? "Local changes waiting to sync to Nomad Hub"
                : "Local changes (hub offline; will sync when reconnected)");
        }

        if (localStamp is null)
        {
            return (SessionSyncStatus.None, "");
        }

        return _progress.IsSyncing
            ? (SessionSyncStatus.Syncing, "Uploading to Nomad Hub…")
            : (SessionSyncStatus.Pending, _client.IsConnected
                ? "Not yet synced to Nomad Hub"
                : "Not yet synced (hub offline)");
    }

    public void Start()
    {
        _loop ??= Task.Run(LoopAsync);
        _renewals ??= Task.Run(RenewLoopAsync);
    }

    /// <summary>Something changed here (a turn ended, a rename): upload soon.</summary>
    public void Wake(bool push = true, bool pull = false)
    {
        _pushWanted |= push;
        _pullWanted |= pull;
        _wake.Release();
    }

    // ------------------------------------------------------------------ leases

    /// <summary>
    /// Takes a session's lease before this PC runs it. Null when the hub can't be reached (run anyway, as without a
    /// hub); otherwise the answer, which says who holds it when it isn't granted.
    /// </summary>
    public async Task<LeaseDto?> TakeLeaseAsync(string backend, string sessionId, CancellationToken ct = default)
    {
        try
        {
            var lease = await _client.TakeLeaseAsync(backend, sessionId, ct).ConfigureAwait(false);
            if (lease.Granted)
            {
                lock (_leaseLock)
                {
                    _leases.Add(new LeaseKey(backend, sessionId));
                }
            }

            return lease;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    public async Task ReleaseLeaseAsync(string backend, string sessionId)
    {
        lock (_leaseLock)
        {
            if (!_leases.Remove(new LeaseKey(backend, sessionId)))
            {
                return;
            }
        }

        try
        {
            await _work.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                await PushOneAsync(backend, sessionId, _stop.Token).ConfigureAwait(false); // The final file first, then the lease.
            }
            finally
            {
                _work.Release();
            }

            await _client.ReleaseLeaseAsync(backend, sessionId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or CommitRefusedException)
        {
            // The lease lapses by itself in two minutes.
        }
    }

    public bool Holds(string backend, string sessionId)
    {
        lock (_leaseLock)
        {
            return _leases.Contains(new LeaseKey(backend, sessionId));
        }
    }

    private async Task RenewLoopAsync()
    {
        using var timer = new PeriodicTimer(HubNames.LeaseRenewal);
        while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
        {
            List<LeaseKey> held;
            lock (_leaseLock)
            {
                held = [.. _leases];
            }

            if (!_client.IsConnected)
            {
                continue;
            }

            await RefreshRemoteHostsAsync(_stop.Token).ConfigureAwait(false); // Leases lapse with no change to pull.
            if (held.Count == 0)
            {
                continue;
            }

            try
            {
                foreach (var key in await _client.RenewLeasesAsync(held, _stop.Token).ConfigureAwait(false))
                {
                    lock (_leaseLock)
                    {
                        _leases.Remove(key);
                    }

                    LeaseLost?.Invoke(key);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Offline: try again next time.
            }
        }
    }

    /// <summary>Asks the hub which sessions other PCs run now, and tells when that changed.</summary>
    private async Task RefreshRemoteHostsAsync(CancellationToken ct)
    {
        try
        {
            var hosts = (await _client.LeasesAsync(ct).ConfigureAwait(false))
                .GroupBy(l => l.SessionId)
                .ToDictionary(g => g.Key, g => g.First().HolderName ?? "another PC");
            if (hosts.Count != _remoteHosts.Count || hosts.Any(h => _remoteHosts.GetValueOrDefault(h.Key) != h.Value))
            {
                _remoteHosts = hosts;
                RemoteHostsChanged?.Invoke(hosts);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Offline: try again next time.
        }
    }

    // ------------------------------------------------------------------ state and deletion

    public async Task SetStateAsync(string backend, string sessionId, string key, string? value)
    {
        try
        {
            await _client.SetStateAsync(backend, sessionId, key, value, DateTime.UtcNow, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Status?.Invoke("Couldn't send a change to the hub: " + ex.Message);
        }
    }

    /// <summary>Deletes a session on the hub, so every PC deletes it.</summary>
    public async Task DeleteAsync(string backend, string sessionId)
    {
        try
        {
            await _client.DeleteSessionAsync(backend, sessionId, _stop.Token).ConfigureAwait(false);
            lock (_state)
            {
                _state.Sessions.Remove(Key(backend, sessionId));
            }

            Save();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            lock (_state)
            {
                _state.DeletedHere.Add(Key(backend, sessionId)); // Not listed again here, though the hub still has it.
            }

            Save();
            Status?.Invoke("Couldn't delete the session on the hub: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ the loop

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        try
        {
            _me = (await _client.InfoAsync(ct).ConfigureAwait(false)).DeviceId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status?.Invoke("Hub unreachable: " + ex.Message);
        }

        _ = _client.ConnectAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StepAsync(ct).ConfigureAwait(false);
                _lastSyncedUtc = DateTime.UtcNow;
                ReportProgress(false, "Up to date");
                Status?.Invoke($"Synced {DateTime.Now:t}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ReportProgress(false, "Sync failed", error: ex.Message);
                Status?.Invoke("Sync failed: " + ex.Message);
            }

            await _wake.WaitAsync(ScanInterval, ct).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            if (!_pushWanted && !_pullWanted)
            {
                _pushWanted = _pullWanted = true; // The periodic scan.
            }
        }
    }

    /// <summary>
    /// Brings one session up to the hub's version: before this PC runs it (<paramref name="held"/>: the lease is already
    /// this PC's, so nobody else writes it meanwhile), or before it watches another PC run it. Returns the hub's version
    /// (null when the hub doesn't have the session).
    /// </summary>
    public async Task<SessionDto?> PullOneAsync(string backend, string sessionId, CancellationToken ct = default, bool held = true)
    {
        if (await _client.SessionAsync(backend, sessionId, ct).ConfigureAwait(false) is not { } remote)
        {
            return null;
        }

        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (held)
            {
                lock (_leaseLock)
                {
                    _leases.Remove(new LeaseKey(backend, sessionId)); // ApplyAsync leaves alone what this PC holds.
                }
            }

            await ApplyAsync(remote, Fetch.Normal, ct).ConfigureAwait(false);
            Save();
        }
        finally
        {
            if (held)
            {
                lock (_leaseLock)
                {
                    _leases.Add(new LeaseKey(backend, sessionId));
                }
            }

            _work.Release();
        }

        return remote;
    }

    /// <summary>Fetches and uploads now (and once more if that turned up more to do), without waiting for the loop.</summary>
    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        try
        {
            _pullWanted = _pushWanted = true;
            await StepAsync(ct).ConfigureAwait(false);
            if (_pullWanted || _pushWanted)
            {
                await StepAsync(ct).ConfigureAwait(false);
            }

            _lastSyncedUtc = DateTime.UtcNow;
            ReportProgress(false, "Up to date");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportProgress(false, "Sync failed", error: ex.Message);
            throw;
        }
    }

    private async Task StepAsync(CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ReportProgress(true, "Checking hub…");
            if (_me == Guid.Empty)
            {
                _me = (await _client.InfoAsync(ct).ConfigureAwait(false)).DeviceId;
            }

            if (_pullWanted)
            {
                _pullWanted = false;
                await PullAsync(ct).ConfigureAwait(false);
            }

            if (_pushWanted)
            {
                _pushWanted = false;
                await PushAsync(ct).ConfigureAwait(false);
            }

            if (_pullWanted)
            {
                _pullWanted = false;
                await PullAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _activeSessionKey = null;
            _work.Release();
        }
    }

    private static string Key(string backend, string sessionId) => backend + "/" + sessionId;

    private SyncedSession? Known(string backend, string sessionId)
    {
        lock (_state)
        {
            return _state.Sessions.GetValueOrDefault(Key(backend, sessionId));
        }
    }

    private void Remember(string backend, string sessionId, SyncedSession synced)
    {
        lock (_state)
        {
            _state.Sessions[Key(backend, sessionId)] = synced;
        }
    }

    // ------------------------------------------------------------------ push

    private async Task PushAsync(CancellationToken ct)
    {
        var pending = new List<(ISessionFiles Backend, LocalSession Local, SyncedSession? Known)>();
        foreach (var backend in _backends.Values)
        {
            foreach (var local in backend.List(ct))
            {
                ct.ThrowIfCancellationRequested();
                var known = Known(backend.Backend, local.SessionId);
                if (known?.Stamp == local.Stamp || _isBusy(backend.Backend, local.SessionId))
                {
                    continue;
                }

                pending.Add((backend, local, known));
            }
        }

        for (var i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (backend, local, known) = pending[i];
            var key = Key(backend.Backend, local.SessionId);
            _activeSessionKey = key;
            ReportProgress(true, "Uploading", i, pending.Count, local.Title);
            SessionSynced?.Invoke(backend.Backend, local.SessionId);
            try
            {
                await PushAsync(backend, local, known, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Status?.Invoke($"Couldn't upload {local.Title}: {ex.Message}");
            }
            finally
            {
                _activeSessionKey = null;
                SessionSynced?.Invoke(backend.Backend, local.SessionId);
            }

            if ((i + 1) % 10 == 0 || i + 1 == pending.Count)
            {
                Save();
            }
        }

        if (pending.Count > 0)
        {
            ReportProgress(true, "Uploading", pending.Count, pending.Count);
        }
    }

    private async Task PushOneAsync(string backend, string sessionId, CancellationToken ct)
    {
        if (!_backends.TryGetValue(backend, out var files) || files.List(ct).FirstOrDefault(s => s.SessionId == sessionId) is not { } local)
        {
            return;
        }

        await PushAsync(files, local, Known(backend, sessionId), ct).ConfigureAwait(false);
        Save();
    }

    /// <summary>Sends a session's usage records past the ones sent before (files only grow, so the order holds); returns how many it has.</summary>
    private async Task<int> SendUsageAsync(string backend, string sessionId, string file, int sent, CancellationToken ct)
    {
        IReadOnlyList<UsageRecordDto> records = backend switch
        {
            AgentBackends.ClaudeCode => UsageExtractor.Claude(sessionId, file),
            AgentBackends.Codex => UsageExtractor.Codex(sessionId, file),
            AgentBackends.OpenCode => UsageExtractor.OpenCode(sessionId, file),
            _ => [],
        };
        var fresh = records.Skip(sent > records.Count ? 0 : sent).ToList();
        for (var i = 0; i < fresh.Count; i += UsageBatchSize)
        {
            await _client.PostUsageAsync(fresh.GetRange(i, Math.Min(UsageBatchSize, fresh.Count - i)), ct).ConfigureAwait(false);
        }

        return records.Count;
    }

    private async Task PushAsync(ISessionFiles backend, LocalSession local, SyncedSession? known, CancellationToken ct)
    {
        var payload = await backend.CollectAsync(local, ct).ConfigureAwait(false);
        try
        {
            if (payload.Count == 0)
            {
                return;
            }

            var files = new List<SessionFileDto>();
            foreach (var f in payload)
            {
                var (hash, length) = await HashAsync(f.FullPath, long.MaxValue, ct).ConfigureAwait(false);
                files.Add(new SessionFileDto(f.Path, hash, length));
            }

            var usageSent = await SendUsageAsync(backend.Backend, local.SessionId, payload[0].FullPath, known?.UsageSent ?? 0, ct).ConfigureAwait(false);
            if (known is not null && SameFiles(known.Files, files))
            {
                Remember(backend.Backend, local.SessionId, known with { Stamp = local.Stamp, UsageSent = usageSent });
                return;
            }

            await UploadAsync(payload, files, known?.Files ?? [], ct).ConfigureAwait(false);
            var project = ProjectKeys.For(local.WorkingDirectory);
            var commit = new SessionCommit(known?.Version ?? 0, local.Title, local.WorkingDirectory, local.GitBranch, local.LastActivityUtc,
                project?.Key, project?.Name, files);
            try
            {
                var committed = await CommitAsync(backend, local, commit, payload, usageSent, ct).ConfigureAwait(false);
                if (committed is not null)
                {
                    Remember(backend.Backend, local.SessionId, new SyncedSession(committed.Version, local.Stamp, files, usageSent));
                }
            }
            catch (CommitRefusedException refused) when (refused.Rejection.Reason == "deleted")
            {
                lock (_state)
                {
                    _state.Sessions.Remove(Key(backend.Backend, local.SessionId));
                }

                SessionDeleted?.Invoke(backend.Backend, local.SessionId);
            }
            catch (CommitRefusedException refused) when (refused.Rejection.Reason == "leased")
            {
                // Another PC hosts it: its next upload wins; this copy is fetched over (or kept as a fork) then.
            }
        }
        finally
        {
            backend.Release(payload);
        }
    }

    /// <summary>
    /// Commits; when the hub has moved on, retries on top of its version if this copy only adds to it, leaves it to
    /// the next fetch if this copy is behind, and keeps a Claude Code copy that went its own way as a new session.
    /// </summary>
    private async Task<SessionDto?> CommitAsync(ISessionFiles backend, LocalSession local, SessionCommit commit, IReadOnlyList<PayloadFile> payload, int usageSent, CancellationToken ct)
    {
        try
        {
            return await _client.CommitAsync(backend.Backend, local.SessionId, commit, ct).ConfigureAwait(false);
        }
        catch (CommitRefusedException refused) when (refused.Rejection.Reason == "missing-blobs")
        {
            await UploadAsync(payload, commit.Files, [], ct).ConfigureAwait(false);
            return await _client.CommitAsync(backend.Backend, local.SessionId, commit, ct).ConfigureAwait(false);
        }
        catch (CommitRefusedException refused) when (refused.Rejection is { Reason: "version", Current: { } current })
        {
            if (backend.Opaque)
            {
                return await ResolveOpaqueAsync(backend, local, commit, current, usageSent, ct).ConfigureAwait(false);
            }

            var mine = commit.Files[0];
            var theirs = current.Files.FirstOrDefault(f => f.Path == mine.Path);
            if (theirs is null || (theirs.Length <= mine.Length && (await HashAsync(payload[0].FullPath, theirs.Length, ct).ConfigureAwait(false)).Hash == theirs.Hash))
            {
                return await _client.CommitAsync(backend.Backend, local.SessionId, commit with { BaseVersion = current.Version }, ct).ConfigureAwait(false);
            }

            if (await ApplyAsync(current, Fetch.IfBehind, ct).ConfigureAwait(false))
            {
                return null; // This copy was an earlier state of the hub's: brought up to date.
            }

            if (backend is ClaudeCodeFiles)
            {
                // Went its own way: kept as a new session, and the hub's version takes its place.
                var fork = await ForkAsync(payload[0].FullPath, local, ct).ConfigureAwait(false);
                // Its first records are the original's, already counted.
                Remember(backend.Backend, fork, new SyncedSession(0, "", [], usageSent));
                await ApplyAsync(current, Fetch.Replace, ct).ConfigureAwait(false);
                _pushWanted = true;
            }
            else
            {
                Status?.Invoke($"{local.Title} changed here and on another PC; this PC's copy wasn't uploaded.");
            }

            return null;
        }
    }

    /// <summary>
    /// The hub moved on while an <see cref="ISessionFiles.Opaque"/> copy changed here too. Copies of a database can't be told
    /// apart as earlier and later, so: the same files are the same state; the same database (the first file) with other side
    /// files (a rename, notes) goes on top of the hub's version; and anything else changed on both PCs, so this PC's copy is set
    /// aside, whole, and the hub's version used. Nothing is lost, and the PCs agree again.
    /// </summary>
    private async Task<SessionDto?> ResolveOpaqueAsync(ISessionFiles backend, LocalSession local, SessionCommit commit, SessionDto current, int usageSent, CancellationToken ct)
    {
        if (SameFiles(commit.Files, current.Files))
        {
            Remember(backend.Backend, local.SessionId, new SyncedSession(current.Version, local.Stamp, current.Files.ToList(), usageSent));
            return null;
        }

        var mine = commit.Files[0];
        var theirs = current.Files.FirstOrDefault(f => f.Path == mine.Path);
        var synced = Known(backend.Backend, local.SessionId)?.Files.FirstOrDefault(f => f.Path == mine.Path);

        // The same database: only side files differ. Changed here (a rename, notes) on top of the database this PC synced, they
        // go on top; with a database this PC never synced itself (a fetch that stopped half way), the hub's version is put
        // in place whole.
        if (theirs == mine)
        {
            // With no record of it (the sync state was reset), this PC's side files go on top as they always did.
            if (synced is null || synced == mine)
            {
                return await _client.CommitAsync(backend.Backend, local.SessionId, commit with { BaseVersion = current.Version }, ct).ConfigureAwait(false);
            }

            if (!_isBusy(backend.Backend, local.SessionId) && !Holds(backend.Backend, local.SessionId) && !backend.InUse(local.SessionId))
            {
                await ApplyAsync(current, Fetch.Replace, ct).ConfigureAwait(false);
            }

            return null;
        }

        // The hub's database is the one this PC last had: the other PC changed only side files, and this PC's newer chat goes on
        // top (its side files with it, which is said).
        if (synced is not null && theirs == synced)
        {
            var committed = await _client.CommitAsync(backend.Backend, local.SessionId, commit with { BaseVersion = current.Version }, ct).ConfigureAwait(false);
            Status?.Invoke($"{local.Title} was renamed or edited on another PC while it went on here: this PC's version is used, so that change needs making again.");
            return committed;
        }

        if (_isBusy(backend.Backend, local.SessionId) || Holds(backend.Backend, local.SessionId) || backend.InUse(local.SessionId))
        {
            return null; // Open here: sorted out once this PC lets it go.
        }

        // Only side files changed here (a rename, say), on top of a database the hub has moved past: nothing here is lost by
        // taking the hub's version, except that change, which is said.
        if (synced == mine)
        {
            if (await ApplyAsync(current, Fetch.Replace, ct).ConfigureAwait(false))
            {
                Status?.Invoke($"{local.Title} changed on another PC while it was renamed or edited here: the other PC's version is used, so the change made here needs making again.");
            }

            return null;
        }

        // Set aside once per version of the hub's: if putting that version in place fails, the next rounds only try that again.
        var key = Key(backend.Backend, local.SessionId);
        if (!_setAside.TryGetValue(key, out var earlier) || earlier.Version != current.Version || earlier.Stamp != local.Stamp)
        {
            if (await backend.SetAsideAsync(local.SessionId, ct).ConfigureAwait(false) is not { } folder)
            {
                Status?.Invoke($"{local.Title} changed here and on another PC; this PC's copy wasn't uploaded.");
                return null;
            }

            _setAside[key] = earlier = (current.Version, local.Stamp, folder);
        }

        var aside = earlier.Folder;

        Status?.Invoke(await ApplyAsync(current, Fetch.Replace, ct).ConfigureAwait(false)
            ? $"{local.Title} changed here and on another PC: the other PC's version is used now, and this PC's is kept in {aside}."
            : $"{local.Title} changed here and on another PC: this PC's copy is kept in {aside}, and the other PC's comes next time.");
        return null;
    }

    /// <summary>
    /// The copies <see cref="ResolveOpaqueAsync"/> set aside, by session: for which version of the hub's, this PC's copy as it was
    /// (its stamp), and where. Set aside again only when either has changed.
    /// </summary>
    private readonly Dictionary<string, (int Version, string Stamp, string Folder)> _setAside = new(StringComparer.Ordinal);

    /// <summary>A copy of a transcript under a new session id, titled "… (from &lt;this PC&gt;)".</summary>
    private static async Task<string> ForkAsync(string transcript, LocalSession local, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var target = Path.Combine(Path.GetDirectoryName(transcript)!, id + ".jsonl");
        var lines = await File.ReadAllLinesAsync(transcript, ct).ConfigureAwait(false);
        var old = $"\"sessionId\":\"{local.SessionId}\"";
        var replacement = $"\"sessionId\":\"{id}\"";
        await File.WriteAllLinesAsync(target, lines.Select(l => l.Replace(old, replacement, StringComparison.Ordinal)), ct).ConfigureAwait(false);
        SessionStore.AppendCustomTitle(target, id, $"{local.Title} (from {Environment.MachineName})");
        return id;
    }

    private static bool SameFiles(IReadOnlyList<SessionFileDto> a, IReadOnlyList<SessionFileDto> b) =>
        a.Count == b.Count && a.OrderBy(f => f.Path, StringComparer.Ordinal).SequenceEqual(b.OrderBy(f => f.Path, StringComparer.Ordinal));

    /// <summary>Uploads the files the hub lacks; a file that only grew since the last upload sends only its new bytes.</summary>
    private async Task UploadAsync(IReadOnlyList<PayloadFile> payload, IReadOnlyList<SessionFileDto> files, IReadOnlyList<SessionFileDto> previous, CancellationToken ct)
    {
        for (var i = 0; i < payload.Count; i++)
        {
            var file = files[i];
            if (previous.Any(p => p.Hash == file.Hash) || await _client.HasBlobAsync(file.Hash, ct).ConfigureAwait(false))
            {
                continue;
            }

            var before = previous.FirstOrDefault(p => p.Path == file.Path);
            await using var stream = new FileStream(payload[i].FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (before is not null && before.Length < file.Length
                && (await HashAsync(payload[i].FullPath, before.Length, ct).ConfigureAwait(false)).Hash == before.Hash)
            {
                stream.Position = before.Length;
                try
                {
                    await _client.PutBlobAsync(file.Hash, new LimitedStream(stream, file.Length - before.Length), before.Hash, before.Length, ct).ConfigureAwait(false);
                    continue;
                }
                catch (HttpRequestException)
                {
                    stream.Position = 0; // The base blob is gone (swept): the whole file then.
                }
            }

            await _client.PutBlobAsync(file.Hash, new LimitedStream(stream, file.Length), null, 0, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The SHA-256 and length of a file's first <paramref name="limit"/> bytes (all of it by default).</summary>
    private static async Task<(string Hash, long Length)> HashAsync(string path, long limit, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (total < limit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - total)), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            sha.AppendData(buffer, 0, read);
            total += read;
        }

        return (Convert.ToHexStringLower(sha.GetHashAndReset()), total);
    }

    // ------------------------------------------------------------------ pull

    private async Task PullAsync(CancellationToken ct)
    {
        long since;
        lock (_state)
        {
            since = _state.Seq;
        }

        var changes = await _client.ChangesAsync(since, ct).ConfigureAwait(false);
        var applied = 0;
        var total = changes.Sessions.Count;
        for (var i = 0; i < total; i++)
        {
            var remote = changes.Sessions[i];
            ct.ThrowIfCancellationRequested();
            if (total > 0)
            {
                _activeSessionKey = Key(remote.Backend, remote.SessionId);
                ReportProgress(true, "Downloading", i, total, remote.Title);
            }

            var wrote = await ApplyAsync(remote, Fetch.Normal, ct).ConfigureAwait(false);
            _activeSessionKey = null;
            if (wrote)
            {
                SessionSynced?.Invoke(remote.Backend, remote.SessionId);
                if (++applied % 25 == 0)
                {
                    Save(); // A first sync is long: what it fetched stays fetched if Nomad closes meanwhile.
                }
            }
        }

        if (total > 0)
        {
            ReportProgress(true, "Downloading", total, total);
        }

        foreach (var state in changes.States)
        {
            StateChanged?.Invoke(state);
        }

        lock (_state)
        {
            _state.Seq = changes.Seq;
        }

        await RetryFetchesAsync(ct).ConfigureAwait(false);
        await CatchUpNewBackendsAsync(since, ct).ConfigureAwait(false);

        _projects = null;
        Save();
        await RefreshRemoteHostsAsync(ct).ConfigureAwait(false); // A lease taken or given back is a change too.
    }

    private List<ProjectDto>? _projects;

    /// <summary>When each failed fetch is next tried, and how many times it failed.</summary>
    private readonly Dictionary<string, (DateTime Next, int Failures)> _retryAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Asks for a session again on later rounds: every round while it is only open here (that costs no download), and after a
    /// failed fetch the next round, then 2 minutes doubling to an hour between tries.
    /// </summary>
    private void NoteRetry(string key, bool failed)
    {
        lock (_state)
        {
            if (!_state.Retry.Contains(key))
            {
                _state.Retry.Add(key);
            }
        }

        if (!failed)
        {
            return;
        }

        var failures = _retryAt.TryGetValue(key, out var earlier) ? earlier.Failures + 1 : 1;
        var wait = failures == 1 ? TimeSpan.Zero : TimeSpan.FromMinutes(Math.Min(60, 2 << Math.Min(failures - 2, 5)));
        _retryAt[key] = (DateTime.UtcNow + wait, failures);
    }

    /// <summary>Asks again for the sessions whose fetch failed (open here, say), until each lands or no longer needs to.</summary>
    private async Task RetryFetchesAsync(CancellationToken ct)
    {
        List<string> retry;
        lock (_state)
        {
            retry = [.. _state.Retry];
        }

        foreach (var key in retry)
        {
            if (_retryAt.TryGetValue(key, out var at) && at.Next > DateTime.UtcNow)
            {
                continue;
            }

            var slash = key.IndexOf('/');
            var (backend, sessionId) = (key[..slash], key[(slash + 1)..]);
            var remote = _backends.ContainsKey(backend) ? await _client.SessionAsync(backend, sessionId, ct).ConfigureAwait(false) : null;
            if (remote is null || remote.Deleted || Known(backend, sessionId)?.Version >= remote.Version)
            {
                lock (_state)
                {
                    _state.Retry.Remove(key);
                }

                _retryAt.Remove(key);

                continue;
            }

            await ApplyAsync(remote, Fetch.Normal, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The change feed passes each session once, so a backend this PC didn't sync before (a newer Nomad, say) would never get
    /// the sessions that went by: they are fetched from the start of the feed, once.
    /// </summary>
    private async Task CatchUpNewBackendsAsync(long since, CancellationToken ct)
    {
        List<string> fresh;
        lock (_state)
        {
            _state.Backends ??= [.. SyncStateFile.FirstBackends.Where(_backends.ContainsKey)];
            fresh = [.. _backends.Keys.Where(b => !_state.Backends.Contains(b))];
        }

        if (fresh.Count == 0)
        {
            return;
        }

        // On a first sync the feed already started at the beginning.
        if (since > 0)
        {
            var all = await _client.ChangesAsync(0, ct).ConfigureAwait(false);
            foreach (var remote in all.Sessions.Where(r => fresh.Contains(r.Backend)))
            {
                ct.ThrowIfCancellationRequested();
                await ApplyAsync(remote, Fetch.Normal, ct).ConfigureAwait(false);
            }
        }

        lock (_state)
        {
            _state.Backends.AddRange(fresh);
        }
    }

    /// <summary>
    /// Raises <see cref="SessionPulled"/> again for the sessions of <paramref name="backend"/> this PC fetched before but doesn't
    /// list (Nomad closed before it could note one, say), so the app lists them. Only for backends listed from Nomad's own
    /// settings (Antigravity).
    /// </summary>
    public async Task AnnounceUnlistedAsync(string backend, IReadOnlyCollection<string> listed, CancellationToken ct = default)
    {
        if (!_backends.TryGetValue(backend, out var files))
        {
            return;
        }

        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AnnounceUnlistedCoreAsync(backend, files, listed, ct).ConfigureAwait(false);
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task AnnounceUnlistedCoreAsync(string backend, ISessionFiles files, IReadOnlyCollection<string> listed, CancellationToken ct)
    {
        List<string> known;
        lock (_state)
        {
            known = [.. _state.Sessions.Keys.Where(k => k.StartsWith(backend + "/", StringComparison.Ordinal) && !_state.DeletedHere.Contains(k))
                .Select(k => k[(backend.Length + 1)..])];
        }

        foreach (var sessionId in known.Where(id => !listed.Contains(id) && files.MainFile(id) is not null))
        {
            try
            {
                if (await _client.SessionAsync(backend, sessionId, ct).ConfigureAwait(false) is { Deleted: false } remote)
                {
                    _projects ??= await _client.ProjectsAsync(ct).ConfigureAwait(false);
                    var folder = await FolderForAsync(remote, _projects, ct).ConfigureAwait(false);
                    SessionPulled?.Invoke(new PulledSession(backend, sessionId, folder, remote.WorkingDirectory, true, remote.Title, remote.LastActivityUtc));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return; // The hub is away or slow, say: tried again when the app next starts syncing.
            }
        }
    }

    /// <summary>
    /// Brings one session from the hub to this PC, unless this PC is writing it (mid-turn, or holding its lease) or it
    /// changed here since the last sync (the upload sorts that out). Returns whether it was written.
    /// </summary>
    private async Task<bool> ApplyAsync(SessionDto remote, Fetch mode, CancellationToken ct)
    {
        if (!_backends.TryGetValue(remote.Backend, out var backend))
        {
            return false;
        }

        var known = Known(remote.Backend, remote.SessionId);
        if (remote.Deleted)
        {
            if (known is not null || backend.StampOf(remote.SessionId) is not null)
            {
                lock (_state)
                {
                    _state.Sessions.Remove(Key(remote.Backend, remote.SessionId));
                }

                SessionDeleted?.Invoke(remote.Backend, remote.SessionId);
            }

            return false;
        }

        if (remote.Version == 0 || (known is not null && known.Version >= remote.Version) || _isBusy(remote.Backend, remote.SessionId)
            || Holds(remote.Backend, remote.SessionId))
        {
            return false;
        }

        var stamp = backend.StampOf(remote.SessionId);
        if (mode == Fetch.Normal && stamp is not null && known is not null && stamp != known.Stamp)
        {
            return false; // Changed here since the last sync too: the upload sorts it out (adds to it, or forks).
        }

        // Open here: nothing can replace it now, so nothing is downloaded either; it is asked for again later.
        if (backend.InUse(remote.SessionId))
        {
            NoteRetry(Key(remote.Backend, remote.SessionId), failed: false);
            return false;
        }

        if (backend.Opaque && stamp is not null && (mode == Fetch.IfBehind || (mode == Fetch.Normal && known is null)))
        {
            // A copy here the hub never had from this PC can't be compared by its first bytes: the upload compares the whole
            // copies, and sets this one aside if they differ.
            if (mode == Fetch.Normal)
            {
                _pushWanted = true;
            }

            return false;
        }

        _projects ??= await _client.ProjectsAsync(ct).ConfigureAwait(false);
        var folder = await FolderForAsync(remote, _projects, ct).ConfigureAwait(false);
        var downloads = new Dictionary<string, string>();
        try
        {
            foreach (var file in remote.Files)
            {
                var temp = Path.Combine(Path.GetTempPath(), $"nomad-hub-{Guid.NewGuid():N}");
                await using (var source = await _client.GetBlobAsync(file.Hash, ct).ConfigureAwait(false))
                await using (var target = File.Create(temp))
                {
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                }

                downloads[file.Path] = temp;
            }

            // A copy here the hub never had from this PC (from before the hub, say) is replaced only if it is the
            // hub's version or an earlier state of it; otherwise the upload adds to the hub's or keeps it as a fork.
            if (!backend.Opaque && (mode == Fetch.IfBehind || (mode == Fetch.Normal && known is null)) && backend.MainFile(remote.SessionId) is { } main && remote.Files.Count > 0
                && downloads.TryGetValue(remote.Files[0].Path, out var fetched))
            {
                var here = await HashAsync(main, long.MaxValue, ct).ConfigureAwait(false);
                if (here.Length > remote.Files[0].Length || (await HashAsync(fetched, here.Length, ct).ConfigureAwait(false)).Hash != here.Hash)
                {
                    if (mode == Fetch.Normal)
                    {
                        _pushWanted = true;
                    }

                    return false;
                }
            }

            var newStamp = await backend.WriteAsync(remote, downloads, folder, ct).ConfigureAwait(false);
            Remember(remote.Backend, remote.SessionId, new SyncedSession(remote.Version, newStamp ?? "", remote.Files.ToList(), known?.UsageSent ?? 0));
            lock (_state)
            {
                _state.Retry.Remove(Key(remote.Backend, remote.SessionId));
            }

            _retryAt.Remove(Key(remote.Backend, remote.SessionId));
            lock (_state)
            {
                _state.DeletedHere.Remove(Key(remote.Backend, remote.SessionId)); // Back from another PC: listed again.
            }

            SessionPulled?.Invoke(new PulledSession(remote.Backend, remote.SessionId, folder, remote.WorkingDirectory, stamp is null, remote.Title,
                remote.LastActivityUtc));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or InvalidDataException)
        {
            // The change feed has passed it: it is asked for again on later rounds until it lands, unless it can never land.
            if (ex is InvalidDataException)
            {
                lock (_state)
                {
                    _state.Retry.Remove(Key(remote.Backend, remote.SessionId));
                }

                _retryAt.Remove(Key(remote.Backend, remote.SessionId));
            }
            else
            {
                NoteRetry(Key(remote.Backend, remote.SessionId), failed: true);
            }

            Status?.Invoke($"Couldn't fetch {remote.Title}: {ex.Message}");
            return false;
        }
        finally
        {
            foreach (var temp in downloads.Values)
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// This PC's folder for a session: its project's folder here, else the session's own folder if it exists here
    /// (then remembered for the project), else the session's own folder as it is.
    /// </summary>
    private async Task<string> FolderForAsync(SessionDto remote, List<ProjectDto> projects, CancellationToken ct)
    {
        var project = remote.ProjectId is { } id ? projects.FirstOrDefault(p => p.Id == id) : null;
        if (project?.LocalPath is { } mapped && Directory.Exists(mapped))
        {
            return mapped;
        }

        var own = remote.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (project is not null && Directory.Exists(own) && ProjectKeys.For(own)?.Key == project.Key)
        {
            await _client.SetProjectFolderAsync(project.Id, own, ct).ConfigureAwait(false);
            projects[projects.IndexOf(project)] = project with { LocalPath = own };
        }

        return own;
    }

    // ------------------------------------------------------------------ state file

    private static SyncStateFile Load(string path, Uri hub)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var state = JsonSerializer.Deserialize(stream, HubSyncJson.Default.SyncStateFile);
                if (state is not null && state.Hub == hub.ToString())
                {
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new SyncStateFile { Hub = hub.ToString() };
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_state)
            {
                json = JsonSerializer.Serialize(_state, HubSyncJson.Default.SyncStateFile);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { _loop, _renewals })
        {
            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        Save();
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A read-only window of the next <paramref name="length"/> bytes of a stream.</summary>
    private sealed class LimitedStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _left = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _left; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancellationToken).ConfigureAwait(false);
            _left -= n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>What this PC last synced of a session: the hub version, its files then, and the local stamp then.</summary>
public sealed record SyncedSession(int Version, string Stamp, List<SessionFileDto> Files, int UsageSent = 0);

public sealed class SyncStateFile
{
    /// <summary>The backends a state file from before <see cref="Backends"/> was kept had synced.</summary>
    public static readonly string[] FirstBackends = ["claude-code", "codex", "opencode"];

    public string Hub { get; set; } = "";
    public long Seq { get; set; }
    public Dictionary<string, SyncedSession> Sessions { get; set; } = [];

    /// <summary>Sessions ("backend/id") whose fetch failed, asked for again on every round until they land.</summary>
    public List<string> Retry { get; set; } = [];

    /// <summary>The backends this PC has synced from the start of the change feed; null in a state file from before.</summary>
    public List<string>? Backends { get; set; }

    /// <summary>Sessions ("backend/id") deleted here while the hub couldn't be told: never listed here again.</summary>
    public HashSet<string> DeletedHere { get; set; } = [];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SyncStateFile))]
internal sealed partial class HubSyncJson : JsonSerializerContext;

/// <summary>How a fetch treats the copy already here.</summary>
internal enum Fetch
{
    /// <summary>The change feed: leaves a copy changed here, or one ahead of the hub's, to the upload.</summary>
    Normal,

    /// <summary>The upload found the hub moved on: replaces this copy only if it is an earlier state of the hub's.</summary>
    IfBehind,

    /// <summary>This copy was kept as a fork: the hub's version takes its place.</summary>
    Replace,
}
