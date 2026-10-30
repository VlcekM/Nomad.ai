using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nomad.Core.Agents;
using Nomad.Core.Sessions;
using Nomad.Hub.Contracts;

namespace Nomad.Core.Hub;

/// <summary>A session the hub brought to this PC (new, or a newer version), and the folder it belongs to here.</summary>
public sealed record PulledSession(string Backend, string SessionId, string Folder, string? OriginalFolder, bool IsNew);

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

    /// <param name="isBusy">Whether a session is mid-turn on this PC (its files are still being written): not touched until it isn't.</param>
    public HubSync(HubClient client, IEnumerable<ISessionFiles> backends, string statePath, Func<string, string, bool> isBusy)
    {
        _client = client;
        _backends = backends.ToDictionary(b => b.Backend);
        _statePath = statePath;
        _isBusy = isBusy;
        _state = Load(statePath, client.BaseUrl);
        client.Changed += _ => Wake(pull: true);
        client.ConnectionChanged += up =>
        {
            if (up)
            {
                Wake(push: true, pull: true);
            }

            ConnectionChanged?.Invoke(up);
        };
        client.HandoffRequested += r => HandoffRequested?.Invoke(r);
    }

    public HubClient Client => _client;

    public Guid DeviceId => _me;

    /// <summary>A session came from the hub (raised on the sync loop's thread).</summary>
    public event Action<PulledSession>? SessionPulled;

    /// <summary>A session was deleted on another PC.</summary>
    public event Action<string, string>? SessionDeleted;

    /// <summary>A per-session flag changed on the hub (archived, pinned, …), this PC's own changes included.</summary>
    public event Action<SessionStateDto>? StateChanged;

    /// <summary>Another PC took a lease this PC held (it had lapsed): stop that session's process.</summary>
    public event Action<LeaseKey>? LeaseLost;

    public event Action<HandoffRequest>? HandoffRequested;

    public event Action<bool>? ConnectionChanged;

    /// <summary>What the loop last did, or its last error.</summary>
    public event Action<string>? Status;

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

            if (held.Count == 0 || !_client.IsConnected)
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
                Status?.Invoke($"Synced {DateTime.Now:t}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Status?.Invoke("Sync failed: " + ex.Message);
            }

            await _wake.WaitAsync(ScanInterval, ct).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            if (!_pushWanted && !_pullWanted)
            {
                _pushWanted = _pullWanted = true; // The periodic scan.
            }
        }
    }

    /// <summary>Fetches and uploads now (and once more if that turned up more to do), without waiting for the loop.</summary>
    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        _pullWanted = _pushWanted = true;
        await StepAsync(ct).ConfigureAwait(false);
        if (_pullWanted || _pushWanted)
        {
            await StepAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task StepAsync(CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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

                try
                {
                    await PushAsync(backend, local, known, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Status?.Invoke($"Couldn't upload {local.Title}: {ex.Message}");
                }
            }

            Save();
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
        foreach (var remote in changes.Sessions)
        {
            ct.ThrowIfCancellationRequested();
            await ApplyAsync(remote, Fetch.Normal, ct).ConfigureAwait(false);
        }

        foreach (var state in changes.States)
        {
            StateChanged?.Invoke(state);
        }

        lock (_state)
        {
            _state.Seq = changes.Seq;
        }

        _projects = null;
        Save();
    }

    private List<ProjectDto>? _projects;

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
            if ((mode == Fetch.IfBehind || (mode == Fetch.Normal && known is null)) && backend.MainFile(remote.SessionId) is { } main && remote.Files.Count > 0
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
            SessionPulled?.Invoke(new PulledSession(remote.Backend, remote.SessionId, folder, remote.WorkingDirectory, stamp is null));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
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
    public string Hub { get; set; } = "";
    public long Seq { get; set; }
    public Dictionary<string, SyncedSession> Sessions { get; set; } = [];
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
