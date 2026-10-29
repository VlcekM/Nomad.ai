using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;

namespace Nomad.Core.Codex;

/// <summary>A server request waiting on the user: its JSON-RPC id, its method, and the card it shows as.</summary>
public sealed record CodexPendingRequest(JsonElement Id, string Method, PermissionRequest Request, JsonElement Parameters);

/// <summary>
/// Turns one Codex thread's app-server notifications (<c>item/started</c>, <c>item/agentMessage/delta</c>,
/// <c>turn/completed</c>, …) and requests (approvals, questions) into Nomad's <see cref="AgentEvent"/>s. Every item
/// is its own message: its text, its reasoning, or its tool calls. Not thread-safe: feed it from one thread.
/// </summary>
public sealed class CodexTranslator
{
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodexCall> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finishedCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _summaryParts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodexPendingRequest> _pending = new(StringComparer.Ordinal);

    private DateTime _turnStartedUtc;
    private string? _turnError;
    private int _steps;
    private int _plans;
    private Usage? _baseline;
    private Usage _turnUsage;

    public CodexTranslator(string threadId, string? workingDirectory)
    {
        ThreadId = threadId;
        WorkingDirectory = workingDirectory;
    }

    public string ThreadId { get; }

    public string? WorkingDirectory { get; }

    /// <summary>The model the thread runs on (for its context window).</summary>
    public string? Model { get; set; }

    public bool IsTurnRunning { get; private set; }

    /// <summary>The running turn's id, once the server named it (for interrupting it).</summary>
    public string? TurnId { get; private set; }

    /// <summary>The user asked to stop: the turn ending interrupted is no error.</summary>
    public bool InterruptRequested { get; set; }

    /// <summary>What the last model call read and wrote: the context in use.</summary>
    public long ContextTokens { get; private set; }

    /// <summary>The thread's name, once it has one.</summary>
    public string Title { get; private set; } = "";

    public IReadOnlyDictionary<string, CodexPendingRequest> Pending => _pending;

    /// <summary>A prompt went out: a turn runs until the server says it completed.</summary>
    public void BeginTurn()
    {
        IsTurnRunning = true;
        InterruptRequested = false;
        TurnId = null;
        _turnStartedUtc = DateTime.UtcNow;
        _turnError = null;
        _steps = 0;
        _baseline = null;
        _turnUsage = default;
    }

    /// <summary>The prompt could not be sent: no turn runs after all.</summary>
    public void CancelTurn() => IsTurnRunning = false;

    /// <summary>The request was answered here: a later <c>serverRequest/resolved</c> withdraws nothing.</summary>
    public bool TakePending(string requestId, out CodexPendingRequest? request) => _pending.Remove(requestId, out request);

    public IEnumerable<AgentEvent> Handle(string method, JsonElement p)
    {
        if (CodexJson.Str(p, "threadId") is { } thread && thread != ThreadId)
        {
            return [];
        }

        return method switch
        {
            "turn/started" => TurnStarted(CodexJson.Obj(p, "turn")),
            "item/started" => ItemStarted(CodexJson.Obj(p, "item")),
            "item/completed" => ItemCompleted(CodexJson.Obj(p, "item")),
            "item/agentMessage/delta" or "item/plan/delta" => TextDelta(p),
            "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta" => ThinkingDelta(p, CodexJson.Str(p, "delta")),
            "item/reasoning/summaryPartAdded" => SummaryPart(p),
            "turn/plan/updated" => Plan(p),
            "thread/tokenUsage/updated" => TokenUsage(CodexJson.Obj(p, "tokenUsage")),
            "serverRequest/resolved" => Withdrawn(RequestKey(CodexJson.Any(p, "requestId"))),
            "thread/name/updated" => Named(CodexJson.Str(p, "threadName")),
            "error" => Error(p),
            "turn/completed" => TurnCompleted(CodexJson.Obj(p, "turn")),
            _ => [],
        };
    }

    /// <summary>
    /// A request of the server's. Approvals and questions become cards (and wait in <see cref="Pending"/>);
    /// <paramref name="unsupported"/> is set for the rest, which the caller turns down.
    /// </summary>
    public IEnumerable<AgentEvent> Request(JsonElement id, string method, JsonElement p, out bool unsupported)
    {
        unsupported = false;
        var key = RequestKey(id);
        var itemId = CodexJson.Str(p, "itemId");
        PermissionRequest? request = null;
        var events = new List<AgentEvent>();
        switch (method)
        {
            case "item/commandExecution/requestApproval":
            {
                var call = itemId is not null && _calls.TryGetValue(itemId, out var known)
                    ? known
                    : new CodexCall(itemId ?? key, "PowerShell", CodexJson.ToElement(new JsonObject { ["command"] = CodexTools.Unwrap(CodexJson.Str(p, "command") ?? "").Command }));
                events.AddRange(Announce(call));
                request = new PermissionRequest
                {
                    RequestId = key,
                    ToolName = call.Name,
                    Input = call.Input,
                    ToolUseId = call.Id,
                    Description = CodexJson.Str(p, "reason"),
                };
                break;
            }

            case "item/fileChange/requestApproval":
            {
                var calls = _calls.Values.Where(c => itemId is not null && (c.Id == itemId || c.Id.StartsWith(itemId + "#", StringComparison.Ordinal))).ToList();
                var first = calls.FirstOrDefault() ?? new CodexCall(itemId ?? key, "Edit", CodexJson.ToElement(new JsonObject { ["file_path"] = CodexJson.Str(p, "grantRoot") ?? "" }));
                var others = calls.Skip(1).Select(c => Path.GetFileName(CodexJson.Str(c.Input, "file_path") ?? "")).Where(n => n.Length > 0).ToList();
                request = new PermissionRequest
                {
                    RequestId = key,
                    ToolName = first.Name,
                    Input = first.Input,
                    ToolUseId = first.Id,
                    Description = CodexJson.Str(p, "reason") ?? (others.Count > 0 ? "Also changes " + string.Join(", ", others) : null),
                    BlockedPath = CodexJson.Str(p, "grantRoot"),
                };
                break;
            }

            case "item/tool/requestUserInput":
                request = new PermissionRequest
                {
                    RequestId = key,
                    ToolName = "AskUserQuestion",
                    Input = Questions(p),
                    ToolUseId = itemId,
                    SuppressAllowForSession = true,
                };
                break;

            case "item/permissions/requestApproval":
                request = new PermissionRequest
                {
                    RequestId = key,
                    ToolName = "Permissions",
                    DisplayName = "More access",
                    Input = CodexJson.ToElement(new JsonObject { ["reason"] = CodexJson.Str(p, "reason"), ["permissions"] = CodexJson.ToNode(CodexJson.Any(p, "permissions")) }),
                    ToolUseId = itemId,
                    Description = CodexJson.Str(p, "reason"),
                };
                break;

            case "mcpServer/elicitation/request":
                request = new PermissionRequest
                {
                    RequestId = key,
                    ToolName = $"mcp__{CodexJson.Str(p, "serverName") ?? "server"}__request",
                    Input = CodexJson.Keep(p),
                    Description = CodexJson.Str(p, "message"),
                    SuppressAllowForSession = true,
                };
                break;

            default:
                unsupported = true;
                return [];
        }

        _pending[key] = new CodexPendingRequest(id.Clone(), method, request, CodexJson.Keep(p));
        events.Add(new PermissionRequestedEvent(request));
        return events;
    }

    /// <summary>The answer a decision makes to a pending request, in the shape its method wants.</summary>
    public static JsonNode Reply(CodexPendingRequest pending, PermissionDecision decision)
    {
        var allow = decision.Choice != PermissionChoice.Deny;
        switch (pending.Method)
        {
            case "item/commandExecution/requestApproval" or "item/fileChange/requestApproval":
                return new JsonObject
                {
                    ["decision"] = decision.Choice switch
                    {
                        PermissionChoice.AllowForSession => "acceptForSession",
                        PermissionChoice.Allow => "accept",
                        _ => "decline",
                    },
                };

            case "item/tool/requestUserInput":
            {
                var answers = new JsonObject();
                var given = allow && decision.UpdatedInput is { } updated ? CodexJson.Obj(updated, "answers") : default;
                foreach (var q in CodexJson.Arr(pending.Parameters, "questions"))
                {
                    if (CodexJson.Str(q, "id") is not { } qid)
                    {
                        continue;
                    }

                    var text = CodexJson.Str(given, CodexJson.Str(q, "question") ?? "") ?? "";
                    var labels = CodexJson.Arr(q, "options").Select(o => CodexJson.Str(o, "label")).OfType<string>().ToList();
                    var pieces = text.Split(", ");
                    var chosen = pieces.Length > 1 && pieces.All(labels.Contains) ? pieces : text.Length > 0 ? [text] : [];
                    answers[qid] = new JsonObject { ["answers"] = new JsonArray([.. chosen.Select(c => (JsonNode?)JsonValue.Create(c))]) };
                }

                return new JsonObject { ["answers"] = answers };
            }

            case "item/permissions/requestApproval":
            {
                var granted = new JsonObject();
                if (allow && CodexJson.Obj(pending.Parameters, "permissions") is { ValueKind: JsonValueKind.Object } asked)
                {
                    foreach (var p in asked.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
                    {
                        granted[p.Name] = JsonNode.Parse(p.Value.GetRawText());
                    }
                }

                return new JsonObject { ["permissions"] = granted, ["scope"] = decision.Choice == PermissionChoice.AllowForSession ? "session" : "turn" };
            }

            default:
                return new JsonObject { ["action"] = allow ? "accept" : "decline", ["content"] = null, ["_meta"] = null };
        }
    }

    /// <summary>The server is gone: a running turn ends with it, and nothing waits any more.</summary>
    public IEnumerable<AgentEvent> ServerExited(string message)
    {
        foreach (var key in _pending.Keys.ToList())
        {
            _pending.Remove(key);
            yield return new PermissionCancelledEvent(key);
        }

        if (IsTurnRunning)
        {
            IsTurnRunning = false;
            yield return new TurnCompletedEvent(new TurnResult { Subtype = "process_exited", IsError = true, ErrorMessage = message });
        }
    }

    private IEnumerable<AgentEvent> TurnStarted(JsonElement turn)
    {
        TurnId = CodexJson.Str(turn, "id") ?? TurnId;
        IsTurnRunning = true;
        yield return new StatusEvent("requesting");
    }

    private IEnumerable<AgentEvent> ItemStarted(JsonElement item)
    {
        if (CodexJson.Str(item, "id") is not { } id || !_started.Add(id))
        {
            yield break;
        }

        switch (CodexJson.Str(item, "type"))
        {
            case "agentMessage" or "plan":
                yield return new AssistantMessageStartedEvent(id, Model, null);
                yield return new BlockStartedEvent(id, 0, AgentBlockKind.Text, null, null, null);
                break;
            case "reasoning":
                yield return new AssistantMessageStartedEvent(id, Model, null);
                yield return new BlockStartedEvent(id, 0, AgentBlockKind.Thinking, null, null, null);
                break;
            case "contextCompaction":
                yield return new StatusEvent("compacting");
                break;
            case var type when CodexTools.IsTool(type):
                foreach (var call in CodexTools.Calls(item, WorkingDirectory))
                {
                    foreach (var e in Announce(call))
                    {
                        yield return e;
                    }
                }

                break;
        }
    }

    private IEnumerable<AgentEvent> ItemCompleted(JsonElement item)
    {
        if (CodexJson.Str(item, "id") is not { } id || !_completed.Add(id))
        {
            yield break;
        }

        var type = CodexJson.Str(item, "type");
        if (type is "agentMessage" or "plan" or "reasoning" && !_started.Contains(id))
        {
            // Seen only finished (a resumed turn): it starts here.
            foreach (var e in ItemStarted(item))
            {
                yield return e;
            }
        }

        switch (type)
        {
            case "agentMessage" or "plan":
                yield return new BlockCompletedEvent(id, 0, AgentBlockKind.Text, CodexJson.Str(item, "text") ?? "", null);
                break;
            case "reasoning":
            {
                var summary = string.Join("\n\n", CodexJson.Strings(item, "summary").Where(s => s.Length > 0));
                var text = summary.Length > 0 ? summary : string.Join("\n\n", CodexJson.Strings(item, "content").Where(s => s.Length > 0));
                yield return new BlockCompletedEvent(id, 0, AgentBlockKind.Thinking, text, null);
                break;
            }

            case "contextCompaction":
                yield return new StatusEvent("requesting");
                break;
            case var t when CodexTools.IsTool(t):
                foreach (var call in CodexTools.Calls(item, WorkingDirectory))
                {
                    foreach (var e in Announce(call))
                    {
                        yield return e;
                    }
                }

                foreach (var result in CodexTools.Results(item))
                {
                    if (_finishedCalls.Add(result.Id))
                    {
                        yield return new ToolResultEvent(result.Id, result.IsError, result.Text, result.Structured, null);
                    }
                }

                break;
        }
    }

    /// <summary>A call's card: once, when its input is first known.</summary>
    private IEnumerable<AgentEvent> Announce(CodexCall call)
    {
        if (_calls.ContainsKey(call.Id))
        {
            yield break;
        }

        _calls[call.Id] = call;
        yield return new BlockStartedEvent(call.Id, 0, AgentBlockKind.ToolUse, call.Id, call.Name, null);
        yield return new ToolCallEvent(call.Id, call.Name, call.Input, null);
        yield return new BlockCompletedEvent(call.Id, 0, AgentBlockKind.ToolUse, null, null);
    }

    private IEnumerable<AgentEvent> TextDelta(JsonElement p)
    {
        if (CodexJson.Str(p, "itemId") is { } id && CodexJson.Str(p, "delta") is { Length: > 0 } delta && _started.Contains(id) && !_completed.Contains(id))
        {
            yield return new TextDeltaEvent(id, 0, delta, null);
        }
    }

    private IEnumerable<AgentEvent> ThinkingDelta(JsonElement p, string? delta)
    {
        if (CodexJson.Str(p, "itemId") is { } id && delta is { Length: > 0 } && _started.Contains(id) && !_completed.Contains(id))
        {
            yield return new ThinkingDeltaEvent(id, 0, delta, null);
        }
    }

    /// <summary>A summary's next part: a blank line between it and the last.</summary>
    private IEnumerable<AgentEvent> SummaryPart(JsonElement p)
    {
        if (CodexJson.Str(p, "itemId") is not { } id)
        {
            return [];
        }

        var parts = _summaryParts.GetValueOrDefault(id) + 1;
        _summaryParts[id] = parts;
        return parts > 1 ? ThinkingDelta(p, "\n\n") : [];
    }

    /// <summary>Codex's plan steps, as the todo list Claude keeps with TodoWrite.</summary>
    private IEnumerable<AgentEvent> Plan(JsonElement p)
    {
        var todos = new JsonArray();
        foreach (var step in CodexJson.Arr(p, "plan"))
        {
            var text = CodexJson.Str(step, "step") ?? "";
            todos.Add((JsonNode)new JsonObject
            {
                ["content"] = text,
                ["status"] = CodexJson.Str(step, "status") switch
                {
                    "inProgress" or "in_progress" => "in_progress",
                    "completed" => "completed",
                    _ => "pending",
                },
                ["activeForm"] = text,
            });
        }

        if (todos.Count == 0)
        {
            yield break;
        }

        var id = $"plan-{CodexJson.Str(p, "turnId") ?? TurnId}-{++_plans}";
        var input = CodexJson.ToElement(new JsonObject { ["todos"] = todos });
        foreach (var e in Announce(new CodexCall(id, "TodoWrite", input)))
        {
            yield return e;
        }

        _finishedCalls.Add(id);
        yield return new ToolResultEvent(id, false, CodexJson.Str(p, "explanation") ?? "", default, null);
    }

    private IEnumerable<AgentEvent> TokenUsage(JsonElement usage)
    {
        var total = Usage.Of(CodexJson.Obj(usage, "total"));
        var last = Usage.Of(CodexJson.Obj(usage, "last"));

        // Before this turn's first model call, the thread had used its total less that call.
        _baseline ??= total - last;
        _turnUsage = total - _baseline.Value;
        _steps++;
        ContextTokens = last.Input + last.Output;
        if (Model is not null && CodexJson.Long(usage, "modelContextWindow") is { } window and > 0)
        {
            CodexServer.ContextWindows[Model] = window;
        }

        yield break;
    }

    private IEnumerable<AgentEvent> Withdrawn(string key)
    {
        // Answered elsewhere, or by us: then it is gone already.
        if (_pending.Remove(key))
        {
            yield return new PermissionCancelledEvent(key);
        }
    }

    private IEnumerable<AgentEvent> Named(string? name)
    {
        Title = name?.Trim() ?? "";
        yield break;
    }

    private IEnumerable<AgentEvent> Error(JsonElement p)
    {
        var message = ErrorText(CodexJson.Obj(p, "error")) ?? "Codex reported an error.";
        if (CodexJson.Bool(p, "willRetry") == true)
        {
            yield return new StatusEvent("retrying");
        }
        else if (IsTurnRunning)
        {
            _turnError = message;
        }
        else
        {
            yield return new AgentErrorEvent(message);
        }
    }

    private IEnumerable<AgentEvent> TurnCompleted(JsonElement turn)
    {
        foreach (var key in _pending.Keys.ToList())
        {
            _pending.Remove(key);
            yield return new PermissionCancelledEvent(key);
        }

        if (!IsTurnRunning)
        {
            yield break;
        }

        IsTurnRunning = false;
        var status = CodexJson.Str(turn, "status");
        var interrupted = status == "interrupted" || InterruptRequested;
        var error = interrupted ? null : status == "failed" ? ErrorText(CodexJson.Obj(turn, "error")) ?? _turnError ?? "The turn failed." : _turnError;
        var duration = CodexJson.Long(turn, "durationMs") ?? (long)(DateTime.UtcNow - _turnStartedUtc).TotalMilliseconds;
        yield return new StatusEvent(null);
        yield return new TurnCompletedEvent(new TurnResult
        {
            Subtype = interrupted ? "interrupted" : error is null ? "success" : "error",
            IsError = error is not null,
            Interrupted = interrupted,
            DurationMs = duration,
            NumTurns = _steps,
            ErrorMessage = error,
            InputTokens = Math.Max(0, _turnUsage.Input - _turnUsage.Cached),
            OutputTokens = _turnUsage.Output,
            CacheReadTokens = _turnUsage.Cached,
            ContextTokens = ContextTokens,
        });
        InterruptRequested = false;
        _turnError = null;
        TurnId = null;
    }

    /// <summary>"…" from a <c>TurnError</c>, with its details when it has any.</summary>
    internal static string? ErrorText(JsonElement error)
    {
        var message = CodexJson.Str(error, "message");
        var details = CodexJson.Str(error, "additionalDetails");
        return message is null ? details : details is { Length: > 0 } && !message.Contains(details, StringComparison.Ordinal) ? $"{message}\n{details}" : message;
    }

    private static string RequestKey(JsonElement id) => "codex-" + (id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText());

    /// <summary>Codex's questions as AskUserQuestion's.</summary>
    private static JsonElement Questions(JsonElement p)
    {
        var questions = new JsonArray();
        foreach (var q in CodexJson.Arr(p, "questions"))
        {
            var options = new JsonArray();
            foreach (var o in CodexJson.Arr(q, "options"))
            {
                options.Add((JsonNode)new JsonObject { ["label"] = CodexJson.Str(o, "label"), ["description"] = CodexJson.Str(o, "description") });
            }

            questions.Add((JsonNode)new JsonObject
            {
                ["question"] = CodexJson.Str(q, "question"),
                ["header"] = CodexJson.Str(q, "header"),
                ["multiSelect"] = false,
                ["options"] = options,
            });
        }

        return CodexJson.ToElement(new JsonObject { ["questions"] = questions });
    }

    private readonly record struct Usage(long Input, long Cached, long Output)
    {
        public static Usage Of(JsonElement e) => new(CodexJson.Long(e, "inputTokens") ?? 0, CodexJson.Long(e, "cachedInputTokens") ?? 0, CodexJson.Long(e, "outputTokens") ?? 0);

        public static Usage operator -(Usage a, Usage b) => new(a.Input - b.Input, a.Cached - b.Cached, a.Output - b.Output);
    }
}
