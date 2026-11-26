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

    public ObservableCollection<PcGroupViewModel> Groups { get; } = [];

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

            _devices.Clear();
            foreach (var device in board)
            {
                if (device.Chats.Count > 0)
                {
                    _devices[device.DeviceId] = device;
                }
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

    /// <summary>One PC's entry changed. An older update than the one held is ignored; a PC with no chats leaves the list.</summary>
    public void Apply(BoardDevice device)
    {
        if (_devices.TryGetValue(device.DeviceId, out var known) && device.Seq != 0 && device.Seq < known.Seq)
        {
            return;
        }

        if (device.Chats.Count == 0)
        {
            _devices.Remove(device.DeviceId);
        }
        else
        {
            _devices[device.DeviceId] = device;
        }

        Rebuild();
    }

    public void Clear()
    {
        _devices.Clear();
        Groups.Clear();
        Status = "Connecting to the hub…";
    }

    [RelayCommand]
    private void Open(ChatRowViewModel row) => main.OpenChatRow(row);

    [RelayCommand]
    private void OpenSettings() => main.OpenSettings();

    /// <summary>The order chats show in: waiting on you, then working, then the rest, newest activity first within each.</summary>
    public static int Rank(string state) => state switch
    {
        "waiting" => 0,
        "working" => 1,
        _ => 2,
    };

    private void Rebuild()
    {
        // PCs with something waiting on the user come first, then by name. Rows and groups are kept and updated, not rebuilt, so
        // the list doesn't jump while it updates.
        var ordered = _devices.Values
            .OrderBy(d => d.Chats.Any(c => c.State == "waiting") ? 0 : 1)
            .ThenBy(d => d.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Sync(Groups, ordered, g => g.DeviceId, d => d.DeviceId, d => new PcGroupViewModel(d.DeviceId), (g, d) => g.Update(d));
        Status = _devices.Count == 0
            ? main.IsConnected
                ? "No chats are shared right now. On a PC, open Nomad's Settings › Hub and turn on Stream live sessions; its open chats show here."
                : "Connecting to the hub…"
            : null;
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

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    public ObservableCollection<ChatRowViewModel> Chats { get; } = [];

    public void Update(BoardDevice device)
    {
        Name = device.DeviceName;
        var waiting = device.Chats.Count(c => c.State == "waiting");
        Summary = waiting > 0 ? $"{waiting} waiting on you" : device.Chats.Count == 1 ? "1 chat" : $"{device.Chats.Count} chats";
        var ordered = device.Chats
            .OrderBy(c => ChatsViewModel.Rank(c.State))
            .ThenByDescending(c => c.LastActivityUtc)
            .ToList();
        ChatsViewModel.Sync(Chats, ordered, r => (r.Backend, r.SessionId), c => (c.Backend, c.SessionId), c => new ChatRowViewModel(device.DeviceId, c.Backend, c.SessionId),
            (r, c) => r.Update(device.DeviceName, c));
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
        LastLine = chat.LastLine.ReplaceLineEndings(" ");
        LastActivityUtc = chat.LastActivityUtc;
        Queued = chat.Queued;
    }
}
