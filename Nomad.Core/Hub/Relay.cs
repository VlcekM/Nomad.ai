using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nomad.Core.Agents;
using Nomad.Core.Protocol;
using Nomad.Hub.Contracts;

namespace Nomad.Core.Hub;

// The live relay (docs/hub.md §8). The PC running a session publishes its agent events through the hub; another PC
// watches them with a RelayBackend, an IAgentBackend whose "process" is that session on the host, and sends prompts,
// answers and Stop back. The hub only passes these JSON strings along.

/// <summary>A permission or question card answered (on the host or by a viewer): the other PCs close theirs.</summary>
public sealed record RelayAnsweredEvent(string RequestId, string By) : AgentEvent;

/// <summary>A prompt the host's session took (typed there, or relayed from <paramref name="From"/>): a turn is running.</summary>
public sealed record RelayPromptEvent(string Text, string? Uuid, string? From) : AgentEvent;

/// <summary>What a viewer sends the host: a prompt, an answer, Stop, or a setting.</summary>
public sealed record RelayInput(
    string Kind,
    string? Text = null,
    string? Uuid = null,
    IReadOnlyList<PromptImage>? Images = null,
    PermissionRequest? Request = null,
    PermissionDecision? Decision = null,
    string? Value = null,
    bool? Flag = null)
{
    public const string Prompt = "prompt";
    public const string Answer = "answer";
    public const string Interrupt = "interrupt";
    public const string Model = "model";
    public const string Mode = "mode";
    public const string Effort = "effort";
    public const string Fast = "fast";
    public const string StopTask = "stop-task";
}

/// <summary>Events and inputs to and from JSON: <c>{"t":"TextDeltaEvent","e":{…}}</c>, with no reflection (NativeAOT).</summary>
public static class RelayCodec
{
    private static readonly Dictionary<Type, JsonTypeInfo> ByType = new();
    private static readonly Dictionary<string, JsonTypeInfo> ByName = new(StringComparer.Ordinal);

    static RelayCodec()
    {
        var c = RelayJson.Default;
        foreach (var info in new JsonTypeInfo[]
                 {
                     c.SessionStartedEvent, c.TurnStartedEvent, c.RemotePromptEvent, c.TaskNotificationEvent, c.WorkflowStartedEvent,
                     c.WorkflowProgressEvent, c.TaskUpdatedEvent, c.CommandOutputEvent, c.PromptSuggestionEvent,
                     c.AssistantMessageStartedEvent, c.BlockStartedEvent, c.TextDeltaEvent, c.ThinkingDeltaEvent, c.BlockCompletedEvent,
                     c.ToolCallEvent, c.ToolResultEvent, c.PermissionRequestedEvent, c.PermissionCancelledEvent, c.FolderMoveApprovedEvent,
                     c.PermissionModeChangedEvent, c.ModelChangedEvent, c.StatusEvent, c.TurnCompletedEvent, c.AgentErrorEvent,
                     c.ProcessExitedEvent, c.RelayAnsweredEvent, c.RelayPromptEvent,
                 })
        {
            ByType[info.Type] = info;
            ByName[info.Type.Name] = info;
        }
    }

    /// <summary>Whether a viewer gets this event (not the account, plan or Remote Control ones, which are the host's own).</summary>
    public static bool IsRelayed(AgentEvent e) => ByType.ContainsKey(e.GetType());

    public static string? Encode(AgentEvent e)
    {
        if (!ByType.TryGetValue(e.GetType(), out var info))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", info.Type.Name);
            writer.WritePropertyName("e");
            JsonSerializer.Serialize(writer, e, info);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static AgentEvent? Decode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("t", out var t) && t.GetString() is { } name && ByName.TryGetValue(name, out var info)
                && doc.RootElement.TryGetProperty("e", out var e)
                ? e.Deserialize(info) as AgentEvent
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string EncodeInput(RelayInput input) => JsonSerializer.Serialize(input, RelayJson.Default.RelayInput);

    public static RelayInput? DecodeInput(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, RelayJson.Default.RelayInput);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A session another PC runs, watched through the hub: its events arrive as if from a local process, and prompts,
/// answers and Stop go to that PC.
/// </summary>
public sealed class RelayBackend : IAgentBackend
{
    private readonly HubClient _client;
    private readonly string _backend;
    private readonly ConcurrentDictionary<string, byte> _sent = new(StringComparer.Ordinal);
    private volatile bool _running;
    private volatile bool _turn;
    private bool _watching;

    public RelayBackend(HubClient client, string backend, string sessionId)
    {
        _client = client;
        _backend = backend;
        SessionId = sessionId;
    }

    public string? HostName { get; private set; }

    public string DisplayName => $"{AgentBackends.Name(_backend)} on {HostName ?? "another PC"}";

    public string? SessionId { get; }

    public bool IsProcessRunning => _running;

    public bool IsTurnRunning => _turn;

    public event Action<AgentEvent>? EventReceived;

    /// <summary>Watches the session; throws <see cref="InvalidOperationException"/> when its host doesn't stream it.</summary>
    public async Task StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default)
    {
        _client.RelayEvents += OnEvents;
        _client.RelayEnded += OnEnded;
        _client.ConnectionChanged += OnConnectionChanged;
        WatchResult watch;
        try
        {
            watch = await _client.WatchAsync(_backend, SessionId!, cancellationToken).ConfigureAwait(false);
            _watching = true;
        }
        catch
        {
            Detach();
            throw;
        }

        if (!watch.Live)
        {
            await StopAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The session's PC doesn't stream it to the hub.");
        }

        HostName = watch.HostName;
        _running = true;
        lock (_order)
        {
            Handle(watch.Buffer);
            _next = watch.Next;
            foreach (var (events, first) in _early)
            {
                Deliver(events, first);
            }

            _early.Clear();
        }
    }

    // Events are numbered by the hub. Those that come while the watch is being asked for wait for its answer: the
    // ones its buffer already has are dropped, the rest follow it.
    private readonly Lock _order = new();
    private readonly List<(string[] Events, long First)> _early = [];
    private long _next = -1;

    private void OnEvents(string backend, string sessionId, string[] events, long first)
    {
        if (backend != _backend || sessionId != SessionId)
        {
            return;
        }

        lock (_order)
        {
            if (_next < 0)
            {
                _early.Add((events, first));
            }
            else if (_running)
            {
                Deliver(events, first);
            }
        }
    }

    private void Deliver(string[] events, long first)
    {
        var skip = (int)Math.Clamp(_next - first, 0, events.Length);
        Handle(events.Skip(skip));
        _next = Math.Max(_next, first + events.Length);
    }

    /// <summary>Back after a drop: the hub forgot this watcher, so watch again (and end if the host is gone meanwhile).</summary>
    private async void OnConnectionChanged(bool connected)
    {
        if (!connected || !_running)
        {
            return;
        }

        try
        {
            var watch = await _client.WatchAsync(_backend, SessionId!).ConfigureAwait(false);
            if (!watch.Live)
            {
                OnEnded(_backend, SessionId!);
                return;
            }

            // Numbered afresh if the hub restarted; what was missed meanwhile shows when the transcript reloads.
            lock (_order)
            {
                _next = watch.Next;
            }
        }
        catch (Exception)
        {
            OnEnded(_backend, SessionId!);
        }
    }

    private void OnEnded(string backend, string sessionId)
    {
        if (backend == _backend && sessionId == SessionId && _running)
        {
            _running = false;
            if (_turn)
            {
                _turn = false;
                EventReceived?.Invoke(new TurnCompletedEvent(new TurnResult { Subtype = "relay_ended" }));
            }

            EventReceived?.Invoke(new ProcessExitedEvent(0, true, ""));
        }
    }

    private void Handle(IEnumerable<string> events)
    {
        foreach (var json in events)
        {
            var e = RelayCodec.Decode(json);
            if (e is null || (e is RelayPromptEvent { Uuid: { } uuid } && _sent.ContainsKey(uuid)))
            {
                continue; // Unknown, or this PC's own prompt (already in its transcript).
            }

            if (e is RelayPromptEvent or RemotePromptEvent or TurnStartedEvent)
            {
                _turn = true;
            }
            else if (e is TurnCompletedEvent)
            {
                _turn = false;
            }
            else if (e is ProcessExitedEvent)
            {
                _running = false;
                _turn = false;
            }

            EventReceived?.Invoke(e);
        }
    }

    private Task SendAsync(RelayInput input, CancellationToken ct) => _client.SendInputAsync(_backend, SessionId!, RelayCodec.EncodeInput(input), ct);

    public async Task SendPromptAsync(string text, string? uuid = null, IReadOnlyList<PromptImage>? images = null, CancellationToken cancellationToken = default)
    {
        uuid ??= Guid.NewGuid().ToString();
        _sent[uuid] = 0;
        _turn = true;
        EventReceived?.Invoke(new TurnStartedEvent(text));
        await SendAsync(new RelayInput(RelayInput.Prompt, text, uuid, images is { Count: > 0 } ? images : null), cancellationToken).ConfigureAwait(false);
    }

    public Task RespondAsync(PermissionRequest request, PermissionDecision decision, CancellationToken cancellationToken = default) =>
        SendAsync(new RelayInput(RelayInput.Answer, Request: request, Decision: decision), cancellationToken);

    public Task InterruptAsync(CancellationToken cancellationToken = default) => SendAsync(new RelayInput(RelayInput.Interrupt), cancellationToken);

    private async Task<bool> SetAsync(RelayInput input, CancellationToken ct)
    {
        await SendAsync(input, ct).ConfigureAwait(false);
        return true;
    }

    public Task<bool> SetModelAsync(string model, CancellationToken cancellationToken = default) => SetAsync(new RelayInput(RelayInput.Model, Value: model), cancellationToken);

    public Task<bool> SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => SetAsync(new RelayInput(RelayInput.Mode, Value: mode), cancellationToken);

    public Task<bool> SetEffortAsync(string? effort, CancellationToken cancellationToken = default) => SetAsync(new RelayInput(RelayInput.Effort, Value: effort), cancellationToken);

    public Task<bool> SetFastModeAsync(bool enabled, CancellationToken cancellationToken = default) => SetAsync(new RelayInput(RelayInput.Fast, Flag: enabled), cancellationToken);

    public Task<bool> SetWorkflowSettingsAsync(bool? ultracode, string? size, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> StopTaskAsync(string taskId, CancellationToken cancellationToken = default) => SetAsync(new RelayInput(RelayInput.StopTask, Value: taskId), cancellationToken);

    public Task<string?> GenerateTitleAsync(string description, bool persist, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<RemoteControlInfo?> SetRemoteControlAsync(bool enabled, string? name, string? reattachSessionId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<RemoteControlInfo?>(null);

    private void Detach()
    {
        _client.RelayEvents -= OnEvents;
        _client.RelayEnded -= OnEnded;
        _client.ConnectionChanged -= OnConnectionChanged;
    }

    public async Task StopAsync()
    {
        _running = false;
        _turn = false;
        Detach();
        if (_watching)
        {
            _watching = false;
            try
            {
                await _client.UnwatchAsync(_backend, SessionId!).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Gone with the connection anyway.
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>
/// The host's side: sends a running session's events to the hub in small batches (every 50 ms); at each turn's end
/// the hub empties its buffer, so it holds only the turn running now. Drops them quietly when the hub can't be reached.
/// </summary>
public sealed class RelayPublisher : IAsyncDisposable
{
    private const int MaxQueued = 20_000;

    private readonly HubClient _client;
    private readonly string _backend;
    private readonly Func<string?> _sessionId;
    private readonly IAgentBackend _source;
    private readonly Func<bool> _enabled;
    private readonly string _hostName;
    private readonly ConcurrentQueue<string> _queue = new();
    private (string Uuid, string From)? _expected;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private bool _newTurn = true;
    private volatile bool _ended;

    /// <param name="hostName">This PC's name, on the prompts typed here.</param>
    /// <param name="enabled">Whether to stream now (this PC's switch and the hub's); the events are dropped while not.</param>
    public RelayPublisher(HubClient client, string backend, Func<string?> sessionId, IAgentBackend source, string hostName, Func<bool> enabled)
    {
        _client = client;
        _hostName = hostName;
        _backend = backend;
        _sessionId = sessionId;
        _source = source;
        _enabled = enabled;
        source.EventReceived += OnEvent;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Marks where a turn ended in the queue: the hub's buffer is emptied there.</summary>
    private const string TurnEnd = "\0turn-end";

    /// <summary>The next prompt the session sends is one a viewer relayed: it goes out with the viewer's uuid and name.</summary>
    public void ExpectPrompt(string uuid, string from) => _expected = (uuid, from);

    private void OnEvent(AgentEvent e)
    {
        if (e is TurnStartedEvent started)
        {
            // Viewers see the prompt itself (the backend's own event carries only its text).
            var expected = _expected;
            _expected = null;
            Publish(new RelayPromptEvent(started.Prompt, expected?.Uuid, expected?.From ?? _hostName));
            return;
        }

        Publish(e);
        if (e is TurnCompletedEvent)
        {
            _queue.Enqueue(TurnEnd);
        }
        else if (e is ProcessExitedEvent)
        {
            _ended = true;
        }
    }

    /// <summary>Sends an event (one of the backend's, or one the host made up: a relayed prompt, an answer).</summary>
    public void Publish(AgentEvent e)
    {
        if (_queue.Count < MaxQueued && RelayCodec.Encode(e) is { } json)
        {
            _queue.Enqueue(json);
        }
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                await FlushAsync().ConfigureAwait(false);
                if (_ended && _queue.IsEmpty)
                {
                    await StopRelayAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FlushAsync()
    {
        if (_queue.IsEmpty || _sessionId() is not { } id || !_client.IsConnected || !_enabled())
        {
            if (!_client.IsConnected || !_enabled())
            {
                _queue.Clear(); // Offline or off: nobody can watch; the transcript syncs later anyway.
            }

            return;
        }

        var batch = new List<string>();
        var endsTurn = false;
        while (batch.Count < 500 && _queue.TryDequeue(out var json))
        {
            if (json == TurnEnd)
            {
                endsTurn = true;
                break;
            }

            batch.Add(json);
        }

        try
        {
            if (batch.Count > 0)
            {
                await _client.PublishAsync(_backend, id, batch.ToArray(), _newTurn, _stop.Token).ConfigureAwait(false);
                _newTurn = false;
            }

            // The turn is in the transcript now (synced soon): a viewer joining from here on starts from that, so the
            // hub forgets the turn.
            if (endsTurn)
            {
                _newTurn = true;
                await _client.PublishAsync(_backend, id, [], true, _stop.Token).ConfigureAwait(false);
                _newTurn = false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The hub went away mid-turn: these are lost to viewers, the transcript isn't.
        }
    }

    private async Task StopRelayAsync()
    {
        if (_sessionId() is { } id && _client.IsConnected)
        {
            try
            {
                await _client.StopRelayAsync(_backend, id).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _source.EventReceived -= OnEvent;
        _ended = true;
        await FlushAsync().ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        await StopRelayAsync().ConfigureAwait(false);
    }
}

/// <summary>A JSON element that may be unset (<c>default</c>): written as null, and null read back as unset.</summary>
internal sealed class OptionalJsonElementConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
        }
        else
        {
            value.WriteTo(writer);
        }
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = [typeof(OptionalJsonElementConverter)])]
[JsonSerializable(typeof(SessionStartedEvent))]
[JsonSerializable(typeof(TurnStartedEvent))]
[JsonSerializable(typeof(RemotePromptEvent))]
[JsonSerializable(typeof(TaskNotificationEvent))]
[JsonSerializable(typeof(WorkflowStartedEvent))]
[JsonSerializable(typeof(WorkflowProgressEvent))]
[JsonSerializable(typeof(TaskUpdatedEvent))]
[JsonSerializable(typeof(CommandOutputEvent))]
[JsonSerializable(typeof(PromptSuggestionEvent))]
[JsonSerializable(typeof(AssistantMessageStartedEvent))]
[JsonSerializable(typeof(BlockStartedEvent))]
[JsonSerializable(typeof(TextDeltaEvent))]
[JsonSerializable(typeof(ThinkingDeltaEvent))]
[JsonSerializable(typeof(BlockCompletedEvent))]
[JsonSerializable(typeof(ToolCallEvent))]
[JsonSerializable(typeof(ToolResultEvent))]
[JsonSerializable(typeof(PermissionRequestedEvent))]
[JsonSerializable(typeof(PermissionCancelledEvent))]
[JsonSerializable(typeof(FolderMoveApprovedEvent))]
[JsonSerializable(typeof(PermissionModeChangedEvent))]
[JsonSerializable(typeof(ModelChangedEvent))]
[JsonSerializable(typeof(StatusEvent))]
[JsonSerializable(typeof(TurnCompletedEvent))]
[JsonSerializable(typeof(AgentErrorEvent))]
[JsonSerializable(typeof(ProcessExitedEvent))]
[JsonSerializable(typeof(RelayAnsweredEvent))]
[JsonSerializable(typeof(RelayPromptEvent))]
[JsonSerializable(typeof(RelayInput))]
internal sealed partial class RelayJson : JsonSerializerContext;
