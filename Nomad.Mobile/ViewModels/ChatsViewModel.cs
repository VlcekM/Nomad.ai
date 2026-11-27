using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// The chats screen: every PC's open chats, grouped by PC. Chats waiting on the user come first, then working ones, then the rest
/// by last activity. The board updates live; pulling down fetches it whole again.
/// </summary>
public sealed partial class ChatsViewModel(MainViewModel main) : ObservableObject
{
    private readonly Dictionary<Guid, BoardDevice> _devices = [];

    // The newest sequence seen for each PC, including PCs that have left: an older snapshot never brings one back.
    private readonly Dictionary<Guid, long> _seen = [];

    public ObservableCollection<PcGroupViewModel> Groups { get; } = [];

    /// <summary>The board changed (Settings lists the PCs).</summary>
    public event Action? BoardChanged;

    /// <summary>What the list is waiting for, or why it is empty; null when it shows chats.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; } = "Connecting to the hub…";

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    public IReadOnlyList<BoardDevice> Devices => [.. _devices.Values.OrderBy(d => d.DeviceName, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Fetches the whole board again (on connecting, and when the list is pulled down).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (main.Connection is not { } connection)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var board = await connection.BoardWatchAsync();
            if (!ReferenceEquals(main.Connection, connection))
            {
                return;
            }

            // Updates that arrived while it was asked for are newer than the snapshot and stay: a PC is taken from it only when
            // the snapshot's entry is at least as new, and one missing from it goes only if nothing newer came since.
            var newest = board.Count > 0 ? board.Max(d => d.Seq) : 0;
            foreach (var id in _devices.Keys.Where(id => board.All(d => d.DeviceId != id) && _seen.GetValueOrDefault(id) <= newest).ToList())
            {
                _devices.Remove(id);
            }

            foreach (var device in board)
            {
                Take(device);
            }

            Rebuild();
            foreach (var device in board)
            {
                main.OpenChat?.OnBoardChanged(device);
            }
        }
        catch (Exception ex)
        {
            Status = "Couldn't load the chats: " + ex.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// One PC's entry changed. An older update than the one held is ignored; a PC that has gone leaves the list (one still
    /// connected with no chats open stays, so a new chat can start there).
    /// </summary>
    public void Apply(BoardDevice device)
    {
        if (Take(device))
        {
            Rebuild();
        }
    }

    /// <summary>
    /// A new connection: the hub may have restarted and counts its changes from 1 again, so what was seen before says nothing
    /// about what comes now. The board fetched next replaces it.
    /// </summary>
    public void ForgetSequence() => _seen.Clear();

    /// <summary>Takes a PC's entry unless a newer one was seen; false when it was older.</summary>
    private bool Take(BoardDevice device)
    {
        if (device.Seq != 0 && device.Seq < _seen.GetValueOrDefault(device.DeviceId))
        {
            return false;
        }

        _seen[device.DeviceId] = device.Seq;
        if (device.Chats.Count == 0 && !device.Online)
        {
            _devices.Remove(device.DeviceId);
        }
        else
        {
            _devices[device.DeviceId] = device;
        }

        return true;
    }

    public void Clear()
    {
        _seen.Clear();
        _devices.Clear();
        Groups.Clear();
        Status = "Connecting to the hub…";
    }

    [RelayCommand]
    private void Open(ChatRowViewModel row) => main.OpenChatRow(row);

    [RelayCommand]
    private void OpenSettings() => main.OpenSettings();

    /// <summary>A new chat on one of the PCs, as their new-session page starts one.</summary>
    [RelayCommand]
    private void NewChat() => main.OpenNewChat();

    /// <summary>A chat's row, when it is on the board.</summary>
    public ChatRowViewModel? Find(Guid device, string backend, string sessionId) =>
        Groups.FirstOrDefault(g => g.DeviceId == device)?.Chats.FirstOrDefault(c => c.Backend == backend && c.SessionId == sessionId);

    /// <summary>The order chats show in: waiting on you, then working, then the rest, newest activity first within each.</summary>
    public static int Rank(string state) => state switch
    {
        "waiting" => 0,
        "working" => 1,
        _ => 2,
    };

    private void Rebuild()
    {
        // PCs by name. Nothing moves as chats work or wait (a moving row takes the tap meant for another); rows and groups are
        // kept and updated, not rebuilt.
        var ordered = _devices.Values
            .OrderBy(d => d.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Sync(Groups, ordered, g => g.DeviceId, d => d.DeviceId, d => new PcGroupViewModel(d.DeviceId), (g, d) => g.Update(d));
        Status = _devices.Count == 0
            ? main.IsConnected
                ? "No chats are shared right now. On a PC, open Nomad's Settings › Hub and turn on Stream live sessions; its open chats show here."
                : "Connecting to the hub…"
            : null;
        BoardChanged?.Invoke();
    }

    /// <summary>Makes <paramref name="target"/> match <paramref name="source"/> in order, keeping and updating items with the same key.</summary>
    internal static void Sync<TItem, TSource, TKey>(ObservableCollection<TItem> target, IReadOnlyList<TSource> source, Func<TItem, TKey> itemKey,
        Func<TSource, TKey> sourceKey, Func<TSource, TItem> create, Action<TItem, TSource> update)
        where TKey : notnull
    {
        var wanted = source.Select(sourceKey).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(itemKey(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < source.Count; i++)
        {
            var key = sourceKey(source[i]);
            var at = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (EqualityComparer<TKey>.Default.Equals(itemKey(target[j]), key))
                {
                    at = j;
                    break;
                }
            }

            TItem item;
            if (at < 0)
            {
                item = create(source[i]);
                target.Insert(i, item);
            }
            else
            {
                item = target[at];
                if (at != i)
                {
                    target.Move(at, i);
                }
            }

            update(item, source[i]);
        }
    }
}

/// <summary>A PC and its open chats, in the order they show.</summary>
public sealed partial class PcGroupViewModel(Guid deviceId) : ObservableObject
{
    public Guid DeviceId { get; } = deviceId;

    // Where each chat first appeared: its place for good, so the list never reorders under a finger. New chats go last.
    private readonly Dictionary<(string Backend, string SessionId), int> _place = [];

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    public ObservableCollection<ChatRowViewModel> Chats { get; } = [];

    /// <summary>The PC's Idle list: chats not open there, newest first (the PC sends them pinned first, then by time).</summary>
    public ObservableCollection<ChatRowViewModel> Idle { get; } = [];

    [ObservableProperty]
    public partial bool HasIdle { get; set; }

    public void Update(BoardDevice device)
    {
        Name = device.DeviceName;
        var open = device.Chats.Where(c => c.State != "closed").ToList();
        var waiting = open.Count(c => c.State == "waiting");
        Summary = waiting > 0 ? $"{waiting} waiting on you" : open.Count switch
        {
            0 => "No open chats",
            1 => "1 open chat",
            var n => $"{n} open chats",
        };
        // The first time a PC is seen its chats come in board order (waiting, working, newest); after that each keeps its place.
        foreach (var chat in open.OrderBy(c => ChatsViewModel.Rank(c.State)).ThenByDescending(c => c.LastActivityUtc))
        {
            _place.TryAdd((chat.Backend, chat.SessionId), _place.Count);
        }

        var ordered = open.OrderBy(c => _place[(c.Backend, c.SessionId)]).ToList();
        ChatsViewModel.Sync(Chats, ordered, r => (r.Backend, r.SessionId), c => (c.Backend, c.SessionId), c => new ChatRowViewModel(device.DeviceId, c.Backend, c.SessionId),
            (r, c) => r.Update(device.DeviceName, c));
        List<BoardChat> closed = [.. device.Chats.Where(c => c.State == "closed")];
        ChatsViewModel.Sync(Idle, closed, r => (r.Backend, r.SessionId), c => (c.Backend, c.SessionId), c => new ChatRowViewModel(device.DeviceId, c.Backend, c.SessionId),
            (r, c) => r.Update(device.DeviceName, c));
        HasIdle = Idle.Count > 0;
    }
}

/// <summary>One chat on the board.</summary>
public sealed partial class ChatRowViewModel(Guid deviceId, string backend, string sessionId) : ObservableObject
{
    public Guid DeviceId { get; } = deviceId;

    public string Backend { get; } = backend;

    public string SessionId { get; } = sessionId;

    [ObservableProperty]
    public partial string DeviceName { get; set; } = "";

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Agent { get; set; } = "";

    /// <summary>The folder's own name ("Nomad.ai"), not its whole path.</summary>
    [ObservableProperty]
    public partial string? Folder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsWorking), nameof(StateText))]
    public partial string State { get; set; } = "idle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial string? Waiting { get; set; }

    [ObservableProperty]
    public partial string LastLine { get; set; } = "";

    [ObservableProperty]
    public partial DateTime LastActivityUtc { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial int Queued { get; set; }

    public bool IsWaiting => State == "waiting";

    public bool IsWorking => State == "working";

    /// <summary>"Waiting: allow Bash", "Working, 1 queued", "Idle", "Stopped".</summary>
    public string StateText
    {
        get
        {
            var text = State switch
            {
                "waiting" => Waiting is { Length: > 0 } w ? "Waiting: " + w : "Waiting on you",
                "working" => "Working",
                "stopped" => "Stopped",
                "closed" => "Not open",
                _ => "Idle",
            };
            return Queued > 0 ? $"{text}, {Queued} queued" : text;
        }
    }

    /// <summary>A PC folder's last part, whichever slashes it uses (a phone's own path rules don't know Windows paths).</summary>
    public static string? FolderName(string? folder)
    {
        var trimmed = folder?.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.Length > 0 ? name : trimmed;
    }

    public void Update(string deviceName, BoardChat chat)
    {
        DeviceName = deviceName;
        Title = chat.Title.Length > 0 ? chat.Title : "Untitled chat";
        Agent = chat.Agent;
        Folder = FolderName(chat.Folder);
        State = chat.State;
        Waiting = chat.Waiting;
        // A chat not open on the PC has no last line to hand: when it was last used instead, as the desktop's Idle list says it.
        LastLine = chat.State == "closed" && chat.LastLine.Length == 0
            ? Nomad.App.Infrastructure.Format.Since(chat.LastActivityUtc)
            : chat.LastLine.ReplaceLineEndings(" ");
        LastActivityUtc = chat.LastActivityUtc;
        Queued = chat.Queued;
    }
}
