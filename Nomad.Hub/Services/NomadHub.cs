using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nomad.Hub.Contracts;
using Nomad.Hub.Data;

namespace Nomad.Hub.Services;

/// <summary>
/// Each Nomad's one live connection: presence, lease renewals and "the change feed moved" pings
/// (<see cref="HubNames.Changed"/>), handoff requests to the PC holding a session, the relay, and the phone's board.
/// </summary>
[Authorize]
public sealed class NomadHub(HubDb db, ChangeLog changes, Presence presence, RelayState relay, BoardState board, HubOptions options)
    : Microsoft.AspNetCore.SignalR.Hub
{
    public static string DeviceGroup(Guid device) => $"device:{device:N}";

    public static string RelayGroup(string key) => "relay:" + key;

    public const string BoardGroup = "board";

    public override async Task OnConnectedAsync()
    {
        var device = Context.User!.DeviceId();
        presence.Connected(device);
        await Groups.AddToGroupAsync(Context.ConnectionId, DeviceGroup(device));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Disconnected(Context.User!.DeviceId());
        foreach (var (backend, sessionId) in relay.DropHost(Context.ConnectionId))
        {
            await Clients.Group(RelayGroup(RelayState.Key(backend, sessionId))).SendAsync(HubNames.RelayEnded, backend, sessionId);
        }

        if (board.Drop(Context.ConnectionId) is { } gone)
        {
            await Clients.Group(BoardGroup).SendAsync(HubNames.BoardChanged, gone);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Renews the caller's leases; returns those another PC holds now.</summary>
    public Task<List<LeaseKey>> RenewLeases(List<LeaseKey> keys) => Leases.RenewAsync(db, changes, Context.User!.DeviceId(), keys);

    // ------------------------------------------------------------------ relay

    private void RequireRelay()
    {
        if (!options.RelayEnabled)
        {
            throw new HubException("The relay is off on this hub.");
        }
    }

    /// <summary>The host streams its session: the events go to the session's viewers and into the turn's buffer.</summary>
    public async Task RelayPublish(string backend, string sessionId, string[] events, bool newTurn)
    {
        RequireRelay();
        var first = relay.Publish(backend, sessionId, Context.ConnectionId, Context.User!.DeviceName(), events, newTurn);
        if (events.Length > 0)
        {
            await Clients.Group(RelayGroup(RelayState.Key(backend, sessionId))).SendAsync(HubNames.RelayEvents, backend, sessionId, events, first);
        }
    }

    public async Task RelayStop(string backend, string sessionId)
    {
        RequireRelay();
        if (relay.Stop(backend, sessionId, Context.ConnectionId))
        {
            await Clients.Group(RelayGroup(RelayState.Key(backend, sessionId))).SendAsync(HubNames.RelayEnded, backend, sessionId);
        }
    }

    public async Task<WatchResult> RelayWatch(string backend, string sessionId)
    {
        RequireRelay();
        await Groups.AddToGroupAsync(Context.ConnectionId, RelayGroup(RelayState.Key(backend, sessionId)));
        return relay.Watch(backend, sessionId);
    }

    public Task RelayUnwatch(string backend, string sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RelayGroup(RelayState.Key(backend, sessionId)));

    // ------------------------------------------------------------------ the phone (docs/mobile.md)

    /// <summary>A PC's chats, all of them: kept for the board and sent to whoever watches it.</summary>
    public async Task BoardPublish(IReadOnlyList<BoardChat> chats)
    {
        RequireRelay();
        var device = board.Publish(Context.ConnectionId, Context.User!.DeviceId(), Context.User!.DeviceName(), chats);
        await Clients.Group(BoardGroup).SendAsync(HubNames.BoardChanged, device);
    }

    /// <summary>The board as it is now; every change after it comes as <see cref="HubNames.BoardChanged"/>.</summary>
    public async Task<IReadOnlyList<BoardDevice>> BoardWatch()
    {
        RequireRelay();
        await Groups.AddToGroupAsync(Context.ConnectionId, BoardGroup);
        return board.All();
    }

    /// <summary>A page of a chat's transcript, from the PC that has it open.</summary>
    public Task<ChatPage> ChatHistory(Guid device, string backend, string sessionId, int? before, int limit) =>
        AskPcAsync(device, backend, sessionId, (pc, ct) =>
            Clients.Client(pc).InvokeAsync<ChatPage>(HubNames.ChatHistoryRequested, backend, sessionId, before, Math.Clamp(limit, 1, 200), ct));

    /// <summary>A prompt, an answer or Stop for a chat, applied by the PC that has it open; it says whether it took it.</summary>
    public Task<ChatInputResult> ChatSend(Guid device, string backend, string sessionId, ChatInput input)
    {
        var from = Context.User!.DeviceName();
        return AskPcAsync(device, backend, sessionId, (pc, ct) =>
            Clients.Client(pc).InvokeAsync<ChatInputResult>(HubNames.ChatInputRequested, backend, sessionId, input, from, ct));
    }

    private async Task<T> AskPcAsync<T>(Guid device, string backend, string sessionId, Func<string, CancellationToken, Task<T>> ask)
    {
        RequireRelay();
        var pc = board.ConnectionWith(device, backend, sessionId)
            ?? throw new HubException("That chat isn't open in Nomad on that PC any more.");
        using var timeout = new CancellationTokenSource(HubNames.ChatAnswerTimeout);
        try
        {
            return await ask(pc, timeout.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            throw new HubException("That PC didn't answer.");
        }
    }

    /// <summary>A viewer's prompt, answer or Stop, for the host to apply as if typed there.</summary>
    public async Task RelaySendInput(string backend, string sessionId, string input)
    {
        RequireRelay();
        var host = relay.HostOf(backend, sessionId) ?? throw new HubException("The session isn't live on another PC any more.");
        await Clients.Client(host).SendAsync(HubNames.RelayInput, backend, sessionId, input, Context.User!.DeviceName());
    }
}

/// <summary>
/// The live sessions being streamed: which connection hosts each, and the events of its current turn, so a viewer
/// that joins mid-turn sees all of it. In memory only; a hub restart ends every stream (the hosts publish again).
/// </summary>
public sealed class RelayState
{
    private const int MaxBuffered = 5000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);

    private sealed class Live(string connection, string hostName)
    {
        public string Connection { get; set; } = connection;
        public string HostName { get; set; } = hostName;
        public List<string> Buffer { get; } = [];

        /// <summary>How many events the stream has had: the next one's number.</summary>
        public long Next { get; set; }
    }

    public static string Key(string backend, string sessionId) => backend + "/" + sessionId;

    /// <summary>Adds a host's events; returns the number of the first.</summary>
    public long Publish(string backend, string sessionId, string connection, string hostName, string[] events, bool newTurn)
    {
        lock (_lock)
        {
            var key = Key(backend, sessionId);
            if (!_live.TryGetValue(key, out var live))
            {
                _live[key] = live = new Live(connection, hostName);
            }

            live.Connection = connection;
            live.HostName = hostName;
            if (newTurn)
            {
                live.Buffer.Clear();
            }

            live.Buffer.AddRange(events);
            if (live.Buffer.Count > MaxBuffered)
            {
                live.Buffer.RemoveRange(0, live.Buffer.Count - MaxBuffered);
            }

            var first = live.Next;
            live.Next += events.Length;
            return first;
        }
    }

    public bool Stop(string backend, string sessionId, string connection)
    {
        lock (_lock)
        {
            var key = Key(backend, sessionId);
            return _live.TryGetValue(key, out var live) && live.Connection == connection && _live.Remove(key);
        }
    }

    public WatchResult Watch(string backend, string sessionId)
    {
        lock (_lock)
        {
            return _live.TryGetValue(Key(backend, sessionId), out var live)
                ? new WatchResult(true, live.HostName, [.. live.Buffer], live.Next)
                : new WatchResult(false, null, [], 0);
        }
    }

    public string? HostOf(string backend, string sessionId)
    {
        lock (_lock)
        {
            return _live.TryGetValue(Key(backend, sessionId), out var live) ? live.Connection : null;
        }
    }

    /// <summary>Ends every stream a connection hosted; returns them.</summary>
    public List<(string Backend, string SessionId)> DropHost(string connection)
    {
        lock (_lock)
        {
            var gone = _live.Where(kv => kv.Value.Connection == connection).Select(kv => kv.Key).ToList();
            foreach (var key in gone)
            {
                _live.Remove(key);
            }

            return gone.Select(k => (k[..k.IndexOf('/')], k[(k.IndexOf('/') + 1)..])).ToList();
        }
    }
}

public static class Leases
{
    public static async Task<List<LeaseKey>> RenewAsync(HubDb db, ChangeLog changes, Guid device, IReadOnlyList<LeaseKey> keys)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        return await changes.WriteAsync(db, async nextSeq =>
        {
            var lost = new List<LeaseKey>();
            var now = DateTime.UtcNow;
            foreach (var key in keys)
            {
                var session = await Sessions.FindOrCreateAsync(db, key.Backend, key.SessionId);
                if (session.LeaseDevice is { } holder && holder != device && session.LeaseExpiresUtc > now)
                {
                    lost.Add(key);
                    continue;
                }

                if (session.LeaseDevice != device)
                {
                    session.Seq = await nextSeq();
                }

                session.LeaseDevice = device;
                session.LeaseExpiresUtc = now + HubNames.LeaseLifetime;
            }

            return lost;
        });
    }
}

/// <summary>
/// The phone's board: the chats each connected PC has open, as it last published them. In memory only, like the relay; a
/// PC's chats leave the board with its connection.
/// </summary>
public sealed class BoardState
{
    /// <summary>The most chats one PC may put on the board.</summary>
    private const int MaxChats = 200;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, BoardDevice> _byConnection = new(StringComparer.Ordinal);

    /// <summary>Replaces a connection's chats; returns its board entry.</summary>
    public BoardDevice Publish(string connection, Guid device, string deviceName, IReadOnlyList<BoardChat>? chats)
    {
        var entry = new BoardDevice(device, deviceName, [.. (chats ?? []).Take(MaxChats)]);
        lock (_lock)
        {
            _byConnection[connection] = entry;
        }

        return entry;
    }

    /// <summary>Takes a connection's chats off the board; returns the PC with no chats, or null when it had none there.</summary>
    public BoardDevice? Drop(string connection)
    {
        lock (_lock)
        {
            if (!_byConnection.Remove(connection, out var entry))
            {
                return null;
            }

            // Another connection of the same PC (a second Nomad window, say) still lists its own chats.
            return _byConnection.Values.FirstOrDefault(d => d.DeviceId == entry.DeviceId) ?? entry with { Chats = [] };
        }
    }

    public IReadOnlyList<BoardDevice> All()
    {
        lock (_lock)
        {
            return [.. _byConnection.Values];
        }
    }

    /// <summary>The connection of <paramref name="device"/> that has the chat open, if any.</summary>
    public string? ConnectionWith(Guid device, string backend, string sessionId)
    {
        lock (_lock)
        {
            return _byConnection.FirstOrDefault(kv => kv.Value.DeviceId == device
                && kv.Value.Chats.Any(c => c.Backend == backend && c.SessionId == sessionId)).Key;
        }
    }
}
