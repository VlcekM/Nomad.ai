using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Core.Agents;
using Nomad.Core.Hub;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// One chat: its transcript (fetched a page at a time from the PC that has it open), the turn streaming now (through the relay),
/// any card waiting on an answer, and the message box. Every input goes to the PC through the hub (docs/mobile.md §3), which applies
/// it as if typed there; the PC's history is the truth, and is fetched again after anything changes.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IClosable
{
    public const int PageSize = 40;
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(350);

    private readonly MainViewModel _main;
    private readonly ChatRowViewModel _row;
    private int _generation = -1;
    private long _nextEvent;
    private bool _closed;
    private bool _refreshing;
    private bool _refreshAgain;
    private bool _refreshScheduled;
    private bool _newMessage = true;
    private bool _attaching;
    private DateTime _lastActivity;
    private CancellationTokenSource? _statusClear;

    // A message whose sending failed without an answer (it may have reached the PC): sent again, it keeps its id, so the PC
    // applies it once.
    private (string Text, string Id)? _unconfirmed;

    public ChatViewModel(MainViewModel main, ChatRowViewModel row)
    {
        _main = main;
        _row = row;
        Title = row.Title;
        State = row.State;
        _lastActivity = row.LastActivityUtc;
    }

    public Guid DeviceId => _row.DeviceId;

    public string Backend => _row.Backend;

    public string SessionId => _row.SessionId;

    public string Subtitle => string.Join(" · ", new[] { _row.DeviceName, _row.Agent, _row.Folder }.Where(s => !string.IsNullOrEmpty(s)));

    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial string State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial int Queued { get; set; }

    /// <summary>What the agent is doing in the turn streaming now ("Using Bash"), if anything.</summary>
    [ObservableProperty]
    public partial string? Activity { get; set; }

    /// <summary>The card waiting on an answer, if any.</summary>
    [ObservableProperty]
    public partial CardViewModel? Card { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = "";

    /// <summary>What the PC said about the last input, or a problem; clears itself after a while.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool StatusIsProblem { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadOlderCommand))]
    public partial bool HasOlder { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    public partial bool IsSending { get; set; }

    /// <summary>The host streams this chat live (its turn shows as it is written); otherwise changes show as the board reports them.</summary>
    [ObservableProperty]
    public partial bool IsLive { get; set; }

    public bool IsWorking => State == "working";

    public string StateText => (State switch
    {
        "waiting" => "Waiting on you",
        "working" => "Working",
        "stopped" => "Stopped",
        _ => "Idle",
    }) + (Queued > 0 ? $", {Queued} queued" : "");

    /// <summary>Raised when new entries arrive at the end, so the view can follow them.</summary>
    public event Action? EntriesAppended;

    // ------------------------------------------------------------------ loading

    /// <summary>Opens the chat: its live turn and its newest page.</summary>
    public async Task AttachAsync()
    {
        await WatchAsync();
        await RefreshAsync();
    }

    /// <summary>The connection came back: watch again (the turn so far comes with it) and fetch the newest page.</summary>
    public Task ReattachAsync() => AttachAsync();

    private async Task WatchAsync()
    {
        if (_main.Connection is not { } connection || _closed)
        {
            return;
        }

        // Numbering starts again with each stream. Events that arrive while the watch is asked for are shown as they come; the
        // buffer then fills in only what they didn't cover.
        _attaching = true;
        _nextEvent = 0;
        try
        {
            var watch = await connection.WatchAsync(Backend, SessionId);
            IsLive = watch.Live;
            ApplyEvents([.. watch.Buffer], watch.Next - watch.Buffer.Count);
            _nextEvent = Math.Max(_nextEvent, watch.Next);
        }
        catch
        {
            // Not streamed live (the hub's relay is off, say): the board's updates still say when to fetch.
            IsLive = false;
        }
        finally
        {
            _attaching = false;
        }
    }

    /// <summary>The newest page, replacing what shows (the PC's transcript is the truth). Only through <see cref="RefreshAsync"/>.</summary>
    private async Task LoadNewestAsync()
    {
        if (_main.Connection is not { } connection || _closed)
        {
            return;
        }

        try
        {
            var page = await connection.ChatHistoryAsync(DeviceId, Backend, SessionId, null, PageSize);
            if (_closed)
            {
                return;
            }

            var appended = Entries.Count == 0 || page.Entries.Count == 0 || page.Entries[^1].Index > Entries[^1].Index || Entries[^1].IsLocal;
            // Older pages already showing stay, unless the transcript changed underneath (their positions no longer fit).
            var sameGeneration = page.Generation == _generation;
            _generation = page.Generation;
            ApplyPage(page);
            var firstIndex = page.Entries.Count > 0 ? page.Entries[0].Index : int.MaxValue;
            List<ChatEntry> keep = sameGeneration ? [.. Entries.Where(e => !e.IsLocal && e.Index < firstIndex).Select(e => e.Entry)] : [];
            ChatsViewModel.Sync(Entries, [.. keep, .. page.Entries], e => e.Index, e => e.Index, e => new EntryViewModel(e), (vm, e) => vm.Update(e));
            HasOlder = Entries.Count > 0 && Entries[0].Index > 0;
            Card = page.Entries.LastOrDefault(e => e.Card is { Pending: true })?.Card is { } card ? CardFor(card) : null;
            IsLoading = page.Loading;
            if (page.Loading)
            {
                ScheduleRefresh(TimeSpan.FromSeconds(1));
            }

            if (appended)
            {
                EntriesAppended?.Invoke();
            }
        }
        catch (Exception ex)
        {
            IsLoading = false;
            ShowStatus("Couldn't load the chat: " + Problem(ex), problem: true);
        }
    }

    /// <summary>The page before the oldest entry showing. If the transcript changed underneath, it starts again from the newest.</summary>
    [RelayCommand(CanExecute = nameof(HasOlder))]
    private async Task LoadOlderAsync()
    {
        if (_main.Connection is not { } connection || Entries.Count == 0)
        {
            return;
        }

        try
        {
            var page = await connection.ChatHistoryAsync(DeviceId, Backend, SessionId, Entries[0].Index, PageSize);
            if (page.Generation != _generation)
            {
                Entries.Clear();
                await RefreshAsync();
                return;
            }

            for (var i = page.Entries.Count - 1; i >= 0; i--)
            {
                if (Entries.Count == 0 || page.Entries[i].Index < Entries[0].Index)
                {
                    Entries.Insert(0, new EntryViewModel(page.Entries[i]));
                }
            }

            HasOlder = Entries.Count > 0 && Entries[0].Index > 0 && page.Entries.Count > 0;
        }
        catch (Exception ex)
        {
            ShowStatus("Couldn't load earlier messages: " + Problem(ex), problem: true);
        }
    }

    private void ApplyPage(ChatPage page)
    {
        State = page.State;
        Queued = page.Queued;
        if (page.State != "working")
        {
            Activity = null;
        }
    }

    /// <summary>Fetches the newest page soon; changes that come close together share one fetch.</summary>
    public void ScheduleRefresh(TimeSpan? delay = null)
    {
        if (_closed || _refreshScheduled)
        {
            return;
        }

        _refreshScheduled = true;
        _ = Task.Delay(delay ?? RefreshDelay).ContinueWith(_ => AppServices.Post(() =>
        {
            _refreshScheduled = false;
            _ = RefreshAsync();
        }), TaskScheduler.Default);
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                await LoadNewestAsync();
            }
            while (_refreshAgain && !_closed);
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ------------------------------------------------------------------ live

    /// <summary>The board changed: the chat's state shows at once, and a chat not streamed live fetches its newest page.</summary>
    public void OnBoardChanged(BoardDevice device)
    {
        if (device.DeviceId != DeviceId || device.Chats.FirstOrDefault(c => c.Backend == Backend && c.SessionId == SessionId) is not { } chat)
        {
            return;
        }

        var changed = chat.State != State || chat.Queued != Queued || chat.Title != Title || chat.LastActivityUtc != _lastActivity;
        _lastActivity = chat.LastActivityUtc;
        Title = chat.Title.Length > 0 ? chat.Title : Title;
        State = chat.State;
        Queued = chat.Queued;
        if (changed && !IsLive)
        {
            ScheduleRefresh();
        }
    }

    public void OnRelayEvents(string backend, string sessionId, string[] events, long first)
    {
        if (_closed || backend != Backend || sessionId != SessionId || events.Length == 0)
        {
            return;
        }

        // A stream that starts again (the chat's process restarted, or its PC reconnected) numbers from 0.
        if (first == 0 && _nextEvent > 0 && !_attaching)
        {
            _nextEvent = 0;
        }

        IsLive = true;
        if (ApplyEvents(events, first))
        {
            ScheduleRefresh();
        }
    }

    /// <summary>Shows the events not seen yet; true when one changed the transcript in a way only a new page shows properly.</summary>
    private bool ApplyEvents(string[] events, long first)
    {
        var needsPage = false;
        for (var i = 0; i < events.Length; i++)
        {
            var number = first + i;
            if (number < _nextEvent)
            {
                continue; // Already had it from the buffer.
            }

            _nextEvent = number + 1;
            needsPage |= Apply(RelayCodec.Decode(events[i]));
        }

        return needsPage;
    }

    public void OnRelayEnded(string backend, string sessionId)
    {
        if (backend == Backend && sessionId == SessionId)
        {
            IsLive = false;
            _nextEvent = 0; // The next stream numbers from 0.
            Activity = null;
            ScheduleRefresh();
        }
    }

    /// <summary>Shows one live event; true when the transcript changed in a way only a new page shows properly.</summary>
    private bool Apply(AgentEvent? e)
    {
        switch (e)
        {
            case TurnStartedEvent or RelayPromptEvent:
                State = "working";
                Activity = "Working…";
                _newMessage = true;
                return true;

            case AssistantMessageStartedEvent { ParentToolUseId: null }:
                _newMessage = true;
                return false;

            case TextDeltaEvent { ParentToolUseId: null } delta:
                AppendLiveText(delta.Text);
                Activity = null;
                return false;

            case ToolCallEvent { ParentToolUseId: null } call:
                Activity = "Using " + call.Name;
                _newMessage = true;
                return true;

            case PermissionRequestedEvent or PermissionCancelledEvent or RelayAnsweredEvent:
                return true;

            case TurnCompletedEvent or AgentErrorEvent or ProcessExitedEvent:
                Activity = null;
                _newMessage = true;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Streamed text goes on the newest assistant entry, or a new one for a new message. The next page replaces it with the PC's
    /// own, so anything the stream and a page disagree on is put right when the turn ends.
    /// </summary>
    private void AppendLiveText(string text)
    {
        if (!_newMessage && Entries.Count > 0 && Entries[^1].IsAssistant)
        {
            Entries[^1].AppendLive(text);
        }
        else
        {
            var last = Entries.Count > 0 ? Entries[^1].Index : -1;
            Entries.Add(EntryViewModel.Local(last + 1, "assistant", text));
            _newMessage = false;
        }

        EntriesAppended?.Invoke();
    }

    // ------------------------------------------------------------------ input

    private bool CanSend => !IsSending && Draft.Trim().Length > 0;

    /// <summary>Sends the message. During a turn the PC queues it, as one typed there would be.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        var id = _unconfirmed is { } earlier && earlier.Text == text ? earlier.Id : NewId();
        _unconfirmed = (text, id);
        var result = await SendInputAsync(new ChatInput("prompt", text, Id: id));
        if (result is not null)
        {
            _unconfirmed = null; // The PC answered: a new send is a new message.
        }

        if (result is { Ok: true } && Draft.Trim() == text)
        {
            Draft = "";
        }
    }

    private bool CanStop => IsWorking && !IsSending;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => SendInputAsync(new ChatInput("stop", Id: NewId()));

    /// <summary>
    /// Sends one input and shows what the PC said. A timeout is tried once more with the same id, which the PC applies only once.
    /// </summary>
    internal async Task<ChatInputResult?> SendInputAsync(ChatInput input)
    {
        if (_main.Connection is not { } connection)
        {
            ShowStatus("Not connected to the hub.", problem: true);
            return null;
        }

        IsSending = true;
        try
        {
            ChatInputResult result;
            try
            {
                result = await connection.ChatSendAsync(DeviceId, Backend, SessionId, input);
            }
            catch (Exception first) when (IsRetryable(first) && connection.IsConnected)
            {
                // Once more with the same id: if the first did reach the PC, it answers this with the first's result.
                result = await connection.ChatSendAsync(DeviceId, Backend, SessionId, input);
            }

            ShowStatus(result.Message, problem: !result.Ok);
            ScheduleRefresh();
            return result;
        }
        catch (Exception ex)
        {
            ShowStatus("Couldn't reach the PC: " + Problem(ex) + (input.Kind == "prompt" ? " Send again to retry; it won't arrive twice." : ""), problem: true);
            return null;
        }
        finally
        {
            IsSending = false;
        }
    }

    private CardViewModel CardFor(ChatCard card) =>
        Card is { } shown && shown.Id == card.Id ? shown : new CardViewModel(card, SendInputAsync);

    private static bool IsRetryable(Exception ex) => ex is TimeoutException or TaskCanceledException
        || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("didn't answer", StringComparison.Ordinal);

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>A hub error without SignalR's wrapping ("An unexpected error occurred invoking 'ChatSend' on the server. HubException: …").</summary>
    internal static string Problem(Exception ex)
    {
        var message = ex.Message;
        var at = message.LastIndexOf("HubException: ", StringComparison.Ordinal);
        return at >= 0 ? message[(at + "HubException: ".Length)..] : message;
    }

    private void ShowStatus(string text, bool problem)
    {
        _statusClear?.Cancel();
        Status = text;
        StatusIsProblem = problem;
        var clear = _statusClear = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(problem ? 12 : 5), clear.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                AppServices.Post(() =>
                {
                    if (ReferenceEquals(_statusClear, clear))
                    {
                        Status = null;
                    }
                });
            }
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void Back() => _main.GoBack();

    /// <summary>The chat screen closed: stop watching it.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _statusClear?.Cancel();
        if (_main.Connection is { IsConnected: true } connection)
        {
            _ = connection.UnwatchAsync(Backend, SessionId).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
