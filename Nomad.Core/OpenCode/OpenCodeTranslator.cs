using System.Text.Json;
using Nomad.Core.Agents;

namespace Nomad.Core.OpenCode;

/// <summary>
/// Turns one OpenCode session's server events (<c>message.updated</c>, <c>message.part.updated</c> and
/// <c>.delta</c>, <c>permission.asked</c>, <c>session.idle</c>, …) into Nomad's <see cref="AgentEvent"/>s. Events of
/// other sessions (another chat, a subagent's child session) are ignored. Not thread-safe: feed it from one thread.
/// </summary>
public sealed class OpenCodeTranslator
{
    private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _announcedMessages = new(StringComparer.Ordinal);

    // Messages of turns that ended: OpenCode still sends their last part updates after an abort, which would show twice.
    private readonly HashSet<string> _closedMessages = new(StringComparer.Ordinal);

    // Each part of an assistant message becomes a block, numbered within its message in order of appearance.
    private readonly Dictionary<string, int> _partIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedParts = new(StringComparer.Ordinal);

    // Tool calls by call id: their mapped input once announced, and whether their result went out.
    private readonly Dictionary<string, (string Name, JsonElement Input)> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finishedCalls = new(StringComparer.Ordinal);

    private readonly Dictionary<string, PermissionRequest> _pending = new(StringComparer.Ordinal);

    private DateTime _turnStartedUtc;
    private string? _turnError;
    private int _steps;
    private double _cost;
    private long _input, _output, _cacheRead, _cacheWrite;

    public OpenCodeTranslator(string sessionId, string? workingDirectory)
    {
        SessionId = sessionId;
        WorkingDirectory = workingDirectory;
    }

    public string SessionId { get; }

    public string? WorkingDirectory { get; }

    public bool IsTurnRunning { get; private set; }

    /// <summary>The user asked to stop: the abort's error is no error.</summary>
    public bool InterruptRequested { get; set; }

    /// <summary>What the last finished step read: the context in use.</summary>
    public long ContextTokens { get; private set; }

    /// <summary>The session's latest real title (OpenCode writes one after the first prompt); empty before.</summary>
    public string Title { get; private set; } = "";

    /// <summary>Requests still waiting for an answer, by request id.</summary>
    public IReadOnlyDictionary<string, PermissionRequest> Pending => _pending;

    /// <summary>A prompt went out: a turn runs until the session goes idle.</summary>
    public void BeginTurn()
    {
        IsTurnRunning = true;
        InterruptRequested = false;
        _turnStartedUtc = DateTime.UtcNow;
        _turnError = null;
        _steps = 0;
        _cost = 0;
        _input = _output = _cacheRead = _cacheWrite = 0;
    }

    /// <summary>The request was answered here: a later <c>permission.replied</c> withdraws nothing.</summary>
    public bool TakePending(string requestId, out PermissionRequest? request) => _pending.Remove(requestId, out request);

    public IEnumerable<AgentEvent> Handle(JsonElement ev)
    {
        var type = OpenCodeJson.Str(ev, "type");
        var p = OpenCodeJson.Obj(ev, "properties");
        if (type is null || SessionOf(p) is { } sid && sid != SessionId)
        {
            return [];
        }

        return type switch
        {
            "message.updated" => MessageUpdated(OpenCodeJson.Obj(p, "info")),
            "message.part.updated" => PartUpdated(OpenCodeJson.Obj(p, "part")),
            "message.part.delta" => PartDelta(p),
            "permission.asked" => PermissionAsked(p),
            "question.asked" => QuestionAsked(p),
            "permission.replied" or "question.replied" or "question.rejected" => Withdrawn(OpenCodeJson.Str(p, "requestID")),
            "session.status" => Status(OpenCodeJson.Obj(p, "status")),
            "session.error" => Error(OpenCodeJson.Obj(p, "error")),
            "session.idle" => Idle(),
            "session.updated" => Updated(OpenCodeJson.Obj(p, "info")),
            _ => [],
        };
    }

    private static string? SessionOf(JsonElement p) =>
        OpenCodeJson.Str(p, "sessionID")
        ?? OpenCodeJson.Str(OpenCodeJson.Obj(p, "info"), "sessionID")
        ?? OpenCodeJson.Str(OpenCodeJson.Obj(p, "part"), "sessionID");

    private IEnumerable<AgentEvent> MessageUpdated(JsonElement info)
    {
        if (OpenCodeJson.Str(info, "id") is not { } id || OpenCodeJson.Str(info, "role") is not { } role)
        {
            yield break;
        }

        _roles[id] = role;
        if (role != "assistant")
        {
            yield break;
        }

        if (_announcedMessages.Add(id))
        {
            var model = OpenCodeJson.Str(info, "providerID") is { } provider && OpenCodeJson.Str(info, "modelID") is { } modelId ? $"{provider}/{modelId}" : null;
            yield return new AssistantMessageStartedEvent(id, model, null);
        }

        // A failed call ends its message with an error (a missing key, a provider outage): the turn reports it.
        if (OpenCodeJson.Obj(info, "error") is { ValueKind: JsonValueKind.Object } error && ErrorText(error) is { } text)
        {
            _turnError = text;
        }
    }

    private IEnumerable<AgentEvent> PartUpdated(JsonElement part)
    {
        if (OpenCodeJson.Str(part, "id") is not { } partId || OpenCodeJson.Str(part, "messageID") is not { } messageId
            || OpenCodeJson.Str(part, "type") is not { } type)
        {
            yield break;
        }

        // The prompt's own parts come back too; the transcript has them already.
        if (!_roles.TryGetValue(messageId, out var role) || role != "assistant" || _closedMessages.Contains(messageId))
        {
            yield break;
        }

        switch (type)
        {
            case "text" or "reasoning":
            {
                var kind = type == "text" ? AgentBlockKind.Text : AgentBlockKind.Thinking;
                var (index, isNew) = IndexOf(messageId, partId);
                if (isNew)
                {
                    _kinds[partId] = kind;
                    yield return new BlockStartedEvent(messageId, index, kind, null, null, null);
                }

                var ended = OpenCodeJson.Long(OpenCodeJson.Obj(part, "time"), "end") is not null;
                if (ended && _completedParts.Add(partId))
                {
                    yield return new BlockCompletedEvent(messageId, index, kind, OpenCodeJson.Str(part, "text") ?? "", null);
                }

                break;
            }

            case "tool":
            {
                if (OpenCodeJson.Str(part, "callID") is not { } callId)
                {
                    yield break;
                }

                var tool = OpenCodeJson.Str(part, "tool") ?? "tool";
                var state = OpenCodeJson.Obj(part, "state");
                var status = OpenCodeJson.Str(state, "status");
                var (index, isNew) = IndexOf(messageId, partId);
                if (isNew)
                {
                    yield return new BlockStartedEvent(messageId, index, AgentBlockKind.ToolUse, callId, OpenCodeTools.Name(tool), null);
                }

                // The input is complete from "running" on (a "pending" part has none yet).
                if (status is "running" or "completed" or "error" && !_calls.ContainsKey(callId))
                {
                    var call = OpenCodeTools.Map(tool, OpenCodeJson.Obj(state, "input"), WorkingDirectory);
                    _calls[callId] = call;
                    yield return new ToolCallEvent(callId, call.Name, call.Input, null);
                    yield return new BlockCompletedEvent(messageId, index, AgentBlockKind.ToolUse, null, null);
                }

                if (status is "completed" or "error" && _finishedCalls.Add(callId))
                {
                    var failed = status == "error";
                    var text = failed ? OpenCodeJson.Str(state, "error") ?? "The tool failed." : OpenCodeJson.Str(state, "output") ?? "";
                    var structured = failed ? default : OpenCodeTools.Structured(OpenCodeJson.Obj(state, "metadata"));
                    yield return new ToolResultEvent(callId, failed, text, structured, null);
                }

                break;
            }

            case "step-finish":
            {
                var tokens = OpenCodeJson.Obj(part, "tokens");
                var cache = OpenCodeJson.Obj(tokens, "cache");
                long input = OpenCodeJson.Long(tokens, "input") ?? 0, output = OpenCodeJson.Long(tokens, "output") ?? 0;
                long read = OpenCodeJson.Long(cache, "read") ?? 0, write = OpenCodeJson.Long(cache, "write") ?? 0;
                _input += input;
                _output += output;
                _cacheRead += read;
                _cacheWrite += write;
                _cost += OpenCodeJson.Double(part, "cost") ?? 0;
                _steps++;
                ContextTokens = input + read + write + output;
                break;
            }
        }
    }

    private IEnumerable<AgentEvent> PartDelta(JsonElement p)
    {
        if (OpenCodeJson.Str(p, "messageID") is not { } messageId || OpenCodeJson.Str(p, "partID") is not { } partId
            || OpenCodeJson.Str(p, "field") != "text" || OpenCodeJson.Str(p, "delta") is not { Length: > 0 } delta
            || !_partIndex.TryGetValue(partId, out var index) || _completedParts.Contains(partId) || _closedMessages.Contains(messageId))
        {
            yield break;
        }

        // Which kind the part is was told when it started; deltas only carry text.
        if (_kinds.TryGetValue(partId, out var kind) && kind == AgentBlockKind.Thinking)
        {
            yield return new ThinkingDeltaEvent(messageId, index, delta, null);
        }
        else
        {
            yield return new TextDeltaEvent(messageId, index, delta, null);
        }
    }

    private readonly Dictionary<string, AgentBlockKind> _kinds = new(StringComparer.Ordinal);

    private (int Index, bool IsNew) IndexOf(string messageId, string partId)
    {
        if (_partIndex.TryGetValue(partId, out var index))
        {
            return (index, false);
        }

        index = _nextIndex.GetValueOrDefault(messageId);
        _nextIndex[messageId] = index + 1;
        _partIndex[partId] = index;
        return (index, true);
    }

    private IEnumerable<AgentEvent> PermissionAsked(JsonElement p)
    {
        if (OpenCodeJson.Str(p, "id") is not { } requestId)
        {
            yield break;
        }

        var callId = OpenCodeJson.Str(OpenCodeJson.Obj(p, "tool"), "callID");
        var permission = OpenCodeJson.Str(p, "permission") ?? "tool";
        var patterns = OpenCodeJson.Arr(p, "patterns") is { } list
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        var metadata = OpenCodeJson.Obj(p, "metadata");

        // OpenCode asks before it reports the call as running, so build the input from what the request carries.
        var (name, input) = callId is not null && _calls.TryGetValue(callId, out var call)
            ? call
            : OpenCodeTools.FromPermission(permission, patterns, metadata);
        if (name == "Edit" && OpenCodeJson.Str(metadata, "diff") is { } diff && input.ValueKind == JsonValueKind.Object && !input.TryGetProperty("old_string", out _))
        {
            input = WithDiff(input, diff);
        }

        // The call's card shows while it waits; its real input replaces this one once it runs.
        if (callId is not null && !_calls.ContainsKey(callId))
        {
            yield return new ToolCallEvent(callId, name, input, null);
        }

        var request = new PermissionRequest
        {
            RequestId = requestId,
            ToolName = name,
            Input = input,
            ToolUseId = callId,
            BlockedPath = permission == "external_directory" ? patterns.FirstOrDefault() : null,
        };
        _pending[requestId] = request;
        yield return new PermissionRequestedEvent(request);
    }

    private IEnumerable<AgentEvent> QuestionAsked(JsonElement p)
    {
        if (OpenCodeJson.Str(p, "id") is not { } requestId)
        {
            yield break;
        }

        var request = new PermissionRequest
        {
            RequestId = requestId,
            ToolName = "AskUserQuestion",
            Input = OpenCodeTools.Questions(p),
            ToolUseId = OpenCodeJson.Str(OpenCodeJson.Obj(p, "tool"), "callID"),
            SuppressAllowForSession = true,
        };
        _pending[requestId] = request;
        yield return new PermissionRequestedEvent(request);
    }

    private IEnumerable<AgentEvent> Withdrawn(string? requestId)
    {
        // Answered elsewhere (OpenCode's own TUI or web app on the same server), or by us: then it is gone already.
        if (requestId is not null && _pending.Remove(requestId))
        {
            yield return new PermissionCancelledEvent(requestId);
        }
    }

    private IEnumerable<AgentEvent> Status(JsonElement status)
    {
        switch (OpenCodeJson.Str(status, "type"))
        {
            case "busy":
                yield return new StatusEvent("requesting");
                break;
            case "retry":
                yield return new StatusEvent("retrying");
                break;
        }
    }

    private IEnumerable<AgentEvent> Error(JsonElement error)
    {
        if (OpenCodeJson.Str(error, "name") == "MessageAbortedError")
        {
            InterruptRequested = true;
            yield break;
        }

        var text = ErrorText(error) ?? "OpenCode reported an error.";
        if (IsTurnRunning)
        {
            _turnError = text;
        }
        else
        {
            yield return new AgentErrorEvent(text);
        }
    }

    private IEnumerable<AgentEvent> Idle()
    {
        foreach (var id in _pending.Keys.ToList())
        {
            _pending.Remove(id);
            yield return new PermissionCancelledEvent(id);
        }

        if (!IsTurnRunning)
        {
            yield break;
        }

        IsTurnRunning = false;
        _closedMessages.UnionWith(_announcedMessages);
        var interrupted = InterruptRequested;
        var error = interrupted ? null : _turnError;
        yield return new StatusEvent(null);
        yield return new TurnCompletedEvent(new TurnResult
        {
            Subtype = interrupted ? "interrupted" : error is null ? "success" : "error",
            IsError = error is not null,
            Interrupted = interrupted,
            DurationMs = (long)(DateTime.UtcNow - _turnStartedUtc).TotalMilliseconds,
            NumTurns = _steps,
            ErrorMessage = error,
            CostUsd = _cost,
            InputTokens = _input,
            OutputTokens = _output,
            CacheReadTokens = _cacheRead,
            CacheCreationTokens = _cacheWrite,
            ContextTokens = ContextTokens,
        });
        InterruptRequested = false;
        _turnError = null;
    }

    private IEnumerable<AgentEvent> Updated(JsonElement info)
    {
        Title = OpenCodeTitles.Clean(OpenCodeJson.Str(info, "title"));
        yield break;
    }

    /// <summary>"Unauthorized: invalid x-api-key" from <c>{ name, data: { message } }</c>.</summary>
    internal static string? ErrorText(JsonElement error)
    {
        var message = OpenCodeJson.Str(OpenCodeJson.Obj(error, "data"), "message") ?? OpenCodeJson.Str(error, "message");
        var name = OpenCodeJson.Str(error, "name");
        return message ?? name;
    }

    private static JsonElement WithDiff(JsonElement input, string diff)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(input.GetRawText())!.AsObject();
        var (removed, added) = SplitDiff(diff);
        node["old_string"] = removed;
        node["new_string"] = added;
        return OpenCodeTools.ToElement(node);
    }

    /// <summary>A unified diff's removed and added lines, as the old and new text an Edit card shows.</summary>
    private static (string Removed, string Added) SplitDiff(string diff)
    {
        var removed = new List<string>();
        var added = new List<string>();
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('-'))
            {
                removed.Add(line[1..]);
            }
            else if (line.StartsWith('+'))
            {
                added.Add(line[1..]);
            }
        }

        return (string.Join('\n', removed), string.Join('\n', added));
    }
}
