using System.Text.Json;
using System.Text.RegularExpressions;
using Nomad.Core.Agents;
using Nomad.Core.Codex;

namespace Nomad.Core.Antigravity;

/// <summary>
/// Follows what Antigravity does across and between prompts. ACP only acknowledges a subagent's launch; when one finishes, the
/// parent either wakes inside the active prompt or replies on its own after <c>end_turn</c>. Each such wake-up counts as one
/// subagent finished (unless woken by a backgrounded command or timer), and explicit kill/list tool calls clear ended work immediately.
/// </summary>
internal sealed partial class AntigravityBackgroundWork : IDisposable
{
    /// <summary>
    /// How long the parent stays quiet after a model call before its unprompted reply counts as ended.
    /// </summary>
    private static readonly TimeSpan QuietEnd = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a subagent may run without a finish before its row ends as unreported.
    /// </summary>
    private static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Guards the state below against the reader thread and the timers.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// A tool call started during a prompt that can outlive it (a backgrounded command or timer).
    /// </summary>
    private sealed record OpenWork(string Description, bool IsDaemon);

    /// <summary>
    /// A launched subagent tracked until its finish, kill, or give-up deadline.
    /// </summary>
    private sealed record ChildSubagent(string ToolCallId, string? ConversationId, DateTimeOffset GiveUpAt);

    /// <summary>
    /// A pending <c>manage_subagents</c> or <c>manage_task</c> tool call whose arguments take effect on completion.
    /// </summary>
    private sealed record PendingManageCall(string ToolName, string? Action, IReadOnlyList<string> TargetIds);

    /// <summary>
    /// Subagents still running, oldest first.
    /// </summary>
    private readonly List<ChildSubagent> _children = [];

    /// <summary>
    /// Launch call IDs for <c>invoke_subagent</c> calls that have started but not yet reported launch completion.
    /// </summary>
    private readonly HashSet<string> _openSubagents = new(StringComparer.Ordinal);

    /// <summary>
    /// Pending <c>manage_subagents</c> and <c>manage_task</c> calls awaiting completion.
    /// </summary>
    private readonly Dictionary<string, PendingManageCall> _openManageCalls = new(StringComparer.Ordinal);

    /// <summary>
    /// Shell commands in the current prompt that have not yet reported completion.
    /// </summary>
    private readonly Dictionary<string, OpenWork> _openCommands = new(StringComparer.Ordinal);

    /// <summary>
    /// Schedule timers in the current prompt that have not yet reported completion.
    /// </summary>
    private readonly Dictionary<string, OpenWork> _openTimers = new(StringComparer.Ordinal);

    /// <summary>
    /// Commands and timers promoted to awaited background work after a turn ended while they were still running.
    /// </summary>
    private readonly Dictionary<string, string> _awaitedWork = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether an awaited command or timer finished since the last subagent finish check, so its wake-up reply isn't counted as a subagent.
    /// </summary>
    private bool _wokeByTool;

    /// <summary>
    /// In-turn tracking: whether the current model step emitted visible output (text or thought).
    /// </summary>
    private bool _turnHadModelOutput;

    /// <summary>
    /// In-turn tracking: a model step ended with <c>usage_update</c> and no tool call yet, so resumed output is a background wake-up.
    /// </summary>
    private bool _awaitingWakeupInTurn;

    /// <summary>
    /// In-turn tracking: the model resumed inside the same <c>session/prompt</c> to handle a background wake-up.
    /// </summary>
    private bool _inTurnWakeup;

    /// <summary>
    /// In-turn tracking: number of backgrounded commands or timers that completed while waiting inside the current prompt.
    /// </summary>
    private int _pendingToolWakeups;

    /// <summary>
    /// Publishes transcript and subagent events.
    /// </summary>
    private readonly Action<AgentEvent> _raise;

    /// <summary>
    /// The model the parent's replies are shown under.
    /// </summary>
    private readonly Func<string?> _model;

    /// <summary>
    /// Ends an unprompted reply once the parent goes quiet.
    /// </summary>
    private readonly Timer _quiet;

    /// <summary>
    /// Ends unreported subagents after a long silence.
    /// </summary>
    private readonly Timer _giveUp;

    /// <summary>
    /// Unprompted or in-turn wake-up replies since the running subagents launched, each one subagent's finish.
    /// </summary>
    private int _finished;

    /// <summary>
    /// The unprompted reply streaming now.
    /// </summary>
    private AntigravityTurn? _reply;

    /// <summary>
    /// When the current reply began.
    /// </summary>
    private long _replyStart;

    /// <summary>
    /// Creates the tracker; nothing runs until a subagent launches or the parent speaks unprompted.
    /// </summary>
    public AntigravityBackgroundWork(Action<AgentEvent> raise, Func<string?> model)
    {
        _raise = raise;
        _model = model;
        _quiet = new Timer(_ => EndReply(promote: true));
        _giveUp = new Timer(_ => GiveUpChildren());
    }

    /// <summary>
    /// Whether the parent is replying with no prompt running.
    /// </summary>
    public bool IsReplying
    {
        get
        {
            lock (_gate) return _reply is not null;
        }
    }

    /// <summary>
    /// Resets per-prompt wake-up tracking when a new <c>session/prompt</c> starts.
    /// </summary>
    public void BeginTurn()
    {
        lock (_gate)
        {
            _turnHadModelOutput = false;
            _awaitingWakeupInTurn = false;
            _inTurnWakeup = false;
            _pendingToolWakeups = 0;
        }
    }

    /// <summary>
    /// Finishes per-prompt tracking when <c>session/prompt</c> returns, promoting still-open commands and timers on normal completion.
    /// </summary>
    public void EndTurn(bool promote)
    {
        List<AgentEvent> events = [];
        lock (_gate)
        {
            if (_inTurnWakeup)
            {
                CountInTurnWakeupLocked(events);
                _inTurnWakeup = false;
            }

            _turnHadModelOutput = false;
            _awaitingWakeupInTurn = false;
            _pendingToolWakeups = 0;
        }

        Emit(events);
        if (promote) PromoteOpenWork();
        else ClearOpenWork();
    }

    /// <summary>
    /// Tracks tool launches, completions, and in-turn background wake-ups across prompts.
    /// </summary>
    public void Track(JsonElement update, bool inTurn = false)
    {
        var kind = CodexJson.Str(update, "sessionUpdate");
        List<AgentEvent> events = [];

        if (inTurn)
        {
            if (kind is "agent_message_chunk" or "agent_thought_chunk")
            {
                lock (_gate)
                {
                    if (_awaitingWakeupInTurn)
                    {
                        _inTurnWakeup = true;
                        _awaitingWakeupInTurn = false;
                    }

                    _turnHadModelOutput = true;
                }

                return;
            }

            if (kind == "usage_update")
            {
                lock (_gate)
                {
                    if (_turnHadModelOutput)
                    {
                        if (_inTurnWakeup)
                        {
                            CountInTurnWakeupLocked(events);
                            _inTurnWakeup = false;
                        }

                        _awaitingWakeupInTurn = true;
                        _turnHadModelOutput = false;
                    }
                }

                Emit(events);
                return;
            }
        }

        var id = CodexJson.Str(update, "toolCallId");
        if (id is null) return;

        var status = CodexJson.Str(update, "status");
        var toolName = CodexJson.Str(update, "name");
        var isTerminal = AntigravityTurn.IsTerminalStatus(status);

        if (kind == "tool_call")
        {
            var rawInput = CodexJson.Any(update, "rawInput");
            var title = CodexJson.Str(update, "title");
            var isDaemon = CodexJson.Bool(rawInput, "IsDaemon") == true;

            lock (_gate)
            {
                if (inTurn)
                {
                    _awaitingWakeupInTurn = false;
                    _turnHadModelOutput = false;
                }

                if (!isTerminal)
                {
                    if (toolName == "run_command" || CodexJson.Str(update, "kind") == "execute")
                    {
                        var desc = AntigravityTurn.CommandDescription(rawInput, title) ?? "command";
                        _openCommands[id] = new OpenWork(desc, isDaemon);
                    }
                    else if (toolName == "schedule")
                    {
                        var desc = AntigravityTurn.ScheduleDescription(rawInput, title);
                        _openTimers[id] = new OpenWork(desc, isDaemon);
                    }
                    else if (toolName == "invoke_subagent")
                    {
                        _openSubagents.Add(id);
                    }
                    else if (toolName is "manage_subagents" or "manage_task")
                    {
                        _openManageCalls[id] = ParseManageCall(toolName, rawInput);
                    }

                    return;
                }

                HandleTerminalToolLocked(id, toolName, status!, update, inTurn, events);
            }

            Emit(events);
            return;
        }

        if (kind != "tool_call_update" || !isTerminal) return;

        lock (_gate)
        {
            HandleTerminalToolLocked(id, toolName, status!, update, inTurn, events);
        }

        Emit(events);
    }

    /// <summary>
    /// Promotes commands and timers that are still running when a prompt returns <c>end_turn</c> to awaited background work.
    /// </summary>
    public void PromoteOpenWork()
    {
        List<AgentEvent> promoted = [];
        lock (_gate)
        {
            foreach (var (id, work) in _openCommands)
            {
                if (work.IsDaemon) continue;
                _awaitedWork[id] = "local_bash";
                promoted.Add(new TaskStartedEvent("", id, "local_bash", work.Description));
            }

            foreach (var (id, work) in _openTimers)
            {
                if (work.IsDaemon) continue;
                _awaitedWork[id] = "timer";
                promoted.Add(new TaskStartedEvent("", id, "timer", work.Description));
            }

            _openCommands.Clear();
            _openTimers.Clear();
        }

        Emit(promoted);
    }

    /// <summary>
    /// Drops uncompleted call tracking when a prompt fails or is cancelled.
    /// </summary>
    public void ClearOpenWork()
    {
        lock (_gate)
        {
            _openCommands.Clear();
            _openTimers.Clear();
            _openSubagents.Clear();
            _openManageCalls.Clear();
        }
    }

    /// <summary>
    /// Shows an update that arrived with no prompt running, as a reply the agent began itself.
    /// A lone <c>tool_call_update</c> finishing an earlier call emits its result without starting a reply.
    /// </summary>
    public void Update(JsonElement update)
    {
        var kind = CodexJson.Str(update, "sessionUpdate");
        var content = kind is "agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update";
        AntigravityTurn? reply;
        ToolResultEvent? loneResult = null;
        lock (_gate)
        {
            if (_reply is null && kind == "tool_call_update")
            {
                loneResult = AntigravityTurn.CreateToolResult(update);
                reply = null;
            }
            else if (_reply is null && !content)
            {
                return;
            }
            else
            {
                reply = _reply ??= BeginReply();
            }
        }

        if (reply is null)
        {
            if (loneResult is not null) _raise(loneResult);
            return;
        }

        if (content) reply.Update(update);

        // A model call ends with its usage; a quiet moment after it ends the reply (a tool still running holds it open).
        _quiet.Change(kind == "usage_update" ? QuietEnd : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Ends the unprompted reply, if one streams: a prompt is about to start, or the parent went quiet.
    /// </summary>
    public void EndReply(bool promote = false)
    {
        AntigravityTurn? reply;
        long start;
        lock (_gate)
        {
            reply = _reply;
            start = _replyStart;
            _reply = null;
        }

        if (reply is null) return;

        _quiet.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        reply.FinishBlock();
        if (promote) PromoteOpenWork();

        var duration = (long)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _raise(new TurnCompletedEvent(new TurnResult { Subtype = "end_turn", DurationMs = duration }));
        CountFinish();
    }

    /// <summary>
    /// The process stopped, and its subagents, backgrounded commands, and timers with it.
    /// </summary>
    public void Stop()
    {
        EndReply();
        EndChildren("stopped", null);
        List<string> awaited;
        lock (_gate)
        {
            awaited = [.. _awaitedWork.Keys];
            _awaitedWork.Clear();
            _openCommands.Clear();
            _openTimers.Clear();
            _openSubagents.Clear();
            _openManageCalls.Clear();
            _wokeByTool = false;
            _turnHadModelOutput = false;
            _awaitingWakeupInTurn = false;
            _inTurnWakeup = false;
            _pendingToolWakeups = 0;
        }

        foreach (var id in awaited)
        {
            _raise(new TaskNotificationEvent(null, id, "stopped", null));
        }
    }

    /// <summary>
    /// Stops the timers.
    /// </summary>
    public void Dispose()
    {
        _quiet.Dispose();
        _giveUp.Dispose();
    }

    /// <summary>
    /// Processes a terminal <c>tool_call</c> or <c>tool_call_update</c> while holding <see cref="_gate"/>.
    /// </summary>
    private void HandleTerminalToolLocked(string id, string? toolName, string status, JsonElement update, bool inTurn, List<AgentEvent> events)
    {
        var wasOpenTool = _openCommands.Remove(id) | _openTimers.Remove(id);
        var wasAwaited = _awaitedWork.Remove(id);
        if (wasAwaited)
        {
            if (inTurn && _awaitingWakeupInTurn) _pendingToolWakeups++;
            else _wokeByTool = true;
        }
        else if (wasOpenTool && inTurn && _awaitingWakeupInTurn)
        {
            _pendingToolWakeups++;
        }

        var wasOpenSubagent = _openSubagents.Remove(id);
        if (status == "completed" && (toolName == "invoke_subagent" || wasOpenSubagent) && !_children.Any(c => c.ToolCallId == id))
        {
            var conversationId = ExtractConversationId(update);
            _children.Add(new ChildSubagent(id, conversationId, DateTimeOffset.UtcNow + GiveUp));
            ScheduleGiveUpLocked();
        }

        PendingManageCall? manageCall = null;
        if (_openManageCalls.Remove(id, out var pending))
        {
            manageCall = pending;
        }
        else if (toolName is "manage_subagents" or "manage_task")
        {
            manageCall = ParseManageCall(toolName, CodexJson.Any(update, "rawInput"));
        }

        if (status == "completed" && manageCall is not null)
        {
            ApplyManageCallLocked(manageCall, update, events);
        }
    }

    /// <summary>
    /// Applies a completed <c>manage_subagents</c> or <c>manage_task</c> call to tracked background state.
    /// </summary>
    private void ApplyManageCallLocked(PendingManageCall call, JsonElement update, List<AgentEvent> events)
    {
        if (call.ToolName == "manage_subagents")
        {
            if (string.Equals(call.Action, "kill_all", StringComparison.OrdinalIgnoreCase))
            {
                EndAllChildrenLocked("stopped", null, events);
            }
            else if (string.Equals(call.Action, "kill", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var target in call.TargetIds)
                {
                    var index = _children.FindIndex(c => string.Equals(c.ConversationId, target, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.ToolCallId, target, StringComparison.OrdinalIgnoreCase));
                    if (index < 0 && _children.Count == 1 && _children[0].ConversationId is null)
                    {
                        index = 0;
                    }

                    if (index >= 0)
                    {
                        var removed = _children[index];
                        _children.RemoveAt(index);
                        events.Add(new SubagentStateEvent(removed.ToolCallId, "stopped", null, null, null));
                    }
                }

                if (_children.Count > 0 && _finished >= _children.Count)
                {
                    EndAllChildrenLocked("completed", null, events);
                }
                else
                {
                    ScheduleGiveUpLocked();
                }
            }
            else if (string.Equals(call.Action, "list", StringComparison.OrdinalIgnoreCase) && _children.Count > 0 && ReportsNoActiveItems(update))
            {
                EndAllChildrenLocked("completed", null, events);
            }
        }
        else if (call.ToolName == "manage_task")
        {
            if (string.Equals(call.Action, "kill", StringComparison.OrdinalIgnoreCase) && _awaitedWork.Count == 1)
            {
                var id = _awaitedWork.Keys.First();
                _awaitedWork.Remove(id);
                events.Add(new TaskNotificationEvent(null, id, "stopped", null));
            }
            else if (string.Equals(call.Action, "list", StringComparison.OrdinalIgnoreCase) && _awaitedWork.Count > 0 && ReportsNoActiveItems(update))
            {
                foreach (var id in _awaitedWork.Keys.ToList())
                {
                    events.Add(new TaskNotificationEvent(null, id, "completed", null));
                }

                _awaitedWork.Clear();
            }
        }
    }

    /// <summary>
    /// Starts showing a reply the agent began by itself.
    /// </summary>
    private AntigravityTurn BeginReply()
    {
        _replyStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _raise(new TurnResumedEvent());
        return new AntigravityTurn(_model(), _raise);
    }

    /// <summary>
    /// Counts an in-turn wake-up as either a pending tool wake-up or one subagent's finish.
    /// </summary>
    private void CountInTurnWakeupLocked(List<AgentEvent> events)
    {
        if (_pendingToolWakeups > 0)
        {
            _pendingToolWakeups--;
            return;
        }

        if (_children.Count == 0) return;
        if (++_finished < _children.Count) return;

        EndAllChildrenLocked("completed", null, events);
    }

    /// <summary>
    /// Counts an unprompted reply as one subagent's finish (unless it was woken by a command or timer); all have finished once replies match launches.
    /// </summary>
    private void CountFinish()
    {
        List<AgentEvent> events = [];
        lock (_gate)
        {
            if (_wokeByTool)
            {
                _wokeByTool = false;
                return;
            }

            if (_children.Count == 0) return;
            if (++_finished < _children.Count) return;

            EndAllChildrenLocked("completed", null, events);
        }

        Emit(events);
    }

    /// <summary>
    /// Ends every tracked subagent whose give-up deadline has passed, rescheduling the timer for any newer ones.
    /// </summary>
    private void GiveUpChildren()
    {
        List<AgentEvent> events = [];
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            for (var i = _children.Count - 1; i >= 0; i--)
            {
                if (_children[i].GiveUpAt <= now)
                {
                    var child = _children[i];
                    _children.RemoveAt(i);
                    events.Add(new SubagentStateEvent(child.ToolCallId, "unreported", null, null, "Antigravity did not report this subagent's finish."));
                }
            }

            if (_children.Count == 0)
            {
                _finished = 0;
            }
            else if (_finished >= _children.Count)
            {
                EndAllChildrenLocked("completed", null, events);
            }
            else
            {
                ScheduleGiveUpLocked();
            }
        }

        Emit(events);
    }

    /// <summary>
    /// Ends every running subagent's row with <paramref name="status"/>.
    /// </summary>
    private void EndChildren(string status, string? message)
    {
        List<AgentEvent> events = [];
        lock (_gate)
        {
            EndAllChildrenLocked(status, message, events);
        }

        Emit(events);
    }

    private void EndAllChildrenLocked(string status, string? message, List<AgentEvent> events)
    {
        foreach (var child in _children)
        {
            events.Add(new SubagentStateEvent(child.ToolCallId, status, null, null, message));
        }

        _children.Clear();
        _finished = 0;
        _giveUp.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void ScheduleGiveUpLocked()
    {
        if (_children.Count == 0)
        {
            _giveUp.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var next = _children.Min(c => c.GiveUpAt) - DateTimeOffset.UtcNow;
        _giveUp.Change(next > TimeSpan.Zero ? next : TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
    }

    private void Emit(List<AgentEvent> events)
    {
        foreach (var e in events)
        {
            _raise(e);
        }
    }

    private static PendingManageCall ParseManageCall(string toolName, JsonElement rawInput)
    {
        var action = CodexJson.Str(rawInput, "Action") ?? CodexJson.Str(rawInput, "action");
        List<string> targets = [];
        if (toolName == "manage_subagents")
        {
            foreach (var item in CodexJson.Arr(rawInput, "ConversationIds"))
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id) targets.Add(id);
            }
        }
        else if (toolName == "manage_task")
        {
            if ((CodexJson.Str(rawInput, "TaskId") ?? CodexJson.Str(rawInput, "taskId")) is { Length: > 0 } taskId)
                targets.Add(taskId);
        }

        return new PendingManageCall(toolName, action, targets);
    }

    private static string? ExtractConversationId(JsonElement update)
    {
        var rawOutput = CodexJson.Any(update, "rawOutput");
        if (rawOutput.ValueKind == JsonValueKind.Object)
        {
            var direct = CodexJson.Str(rawOutput, "conversationId") ?? CodexJson.Str(rawOutput, "conversation_id");
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
        }

        var text = ResultText(update);
        if (string.IsNullOrEmpty(text)) return null;

        var match = UuidPattern().Match(text);
        return match.Success ? match.Value : null;
    }

    private static bool ReportsNoActiveItems(JsonElement update)
    {
        var rawOutput = CodexJson.Any(update, "rawOutput");
        if (rawOutput.ValueKind == JsonValueKind.Array && rawOutput.GetArrayLength() == 0) return true;
        if (rawOutput.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in new[] { "subagents", "tasks", "items" })
            {
                if (rawOutput.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 0)
                    return true;
            }
        }

        var text = ResultText(update);
        if (string.IsNullOrWhiteSpace(text)) return false;

        return text.Contains("no active", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no running", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no direct subagents", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no subagents", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no background tasks", StringComparison.OrdinalIgnoreCase)
            || text.Contains("0 active", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResultText(JsonElement update)
    {
        var output = CodexJson.Any(update, "rawOutput");
        var parts = CodexJson.Arr(update, "content")
            .Where(c => CodexJson.Str(c, "type") == "content")
            .Select(c => CodexJson.Str(CodexJson.Obj(c, "content"), "text") ?? "")
            .Where(t => t.Length > 0);
        var text = string.Join("\n", parts);
        if (text.Length == 0 && output.ValueKind != JsonValueKind.Undefined) text = output.ToString();
        return text;
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex UuidPattern();
}
