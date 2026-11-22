using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nomad.Core.Agents;
using Nomad.Core.Codex;
using Nomad.Core.Hub;

namespace Nomad.Core.Antigravity;

/// <summary>
/// Follows what Antigravity does across and between prompts. ACP only acknowledges a subagent's launch and omits outputs for
/// <c>invoke_subagent</c>, <c>manage_subagents</c>, and <c>manage_task</c>. When the session's live <c>transcript.jsonl</c> is
/// present, this tracker reads it incrementally to bind exact subagent conversation IDs and background task IDs, clear subagents
/// as soon as their message arrives or <c>manage_subagents</c> kills/lists them as idle, and clear background commands/timers if
/// ACP misses a completion update. Without a transcript file, it falls back to ACP wake-up and tool-status tracking.
/// </summary>
internal sealed partial class AntigravityBackgroundWork : IDisposable
{
    /// <summary>
    /// How long the parent stays quiet after a model call before its unprompted reply counts as ended.
    /// </summary>
    private static readonly TimeSpan QuietEnd = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often the live transcript file is checked while background work is tracked.
    /// </summary>
    private static readonly TimeSpan TranscriptPollInterval = TimeSpan.FromSeconds(1);

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
    private sealed class OpenWork(string description, bool isDaemon, bool isRecurring, string? matchHint)
    {
        public string Description { get; } = description;
        public bool IsDaemon { get; } = isDaemon;
        public bool IsRecurring { get; } = isRecurring;
        public string? MatchHint { get; } = matchHint;
        public string? TaskId { get; set; }
        public bool IsBackgrounded { get; set; }
    }

    /// <summary>
    /// A command or timer promoted to awaited background work after a turn ended while it was still running.
    /// </summary>
    private sealed class AwaitedWork(string taskType, bool isRecurring, string? matchHint, string? taskId)
    {
        public string TaskType { get; } = taskType;
        public bool IsRecurring { get; } = isRecurring;
        public string? MatchHint { get; } = matchHint;
        public string? TaskId { get; set; } = taskId;
    }

    /// <summary>
    /// A launched subagent tracked until its finish, kill, or give-up deadline.
    /// </summary>
    private sealed class ChildSubagent(string toolCallId, string? conversationId, string? role, DateTimeOffset giveUpAt)
    {
        public string ToolCallId { get; } = toolCallId;
        public string? ConversationId { get; set; } = conversationId;
        public string? Role { get; } = role;
        public DateTimeOffset GiveUpAt { get; } = giveUpAt;
    }

    /// <summary>
    /// A pending <c>manage_subagents</c>, <c>manage_task</c>, or <c>send_message</c> tool call whose arguments take effect on completion.
    /// </summary>
    private sealed record PendingManageCall(string ToolName, string? Action, IReadOnlyList<string> TargetIds);

    /// <summary>
    /// Subagents still running, oldest first.
    /// </summary>
    private readonly List<ChildSubagent> _children = [];

    /// <summary>
    /// Maps every known subagent conversation ID seen in this session to its launch call ID and role, so follow-up
    /// <c>send_message</c> calls can resume a finished subagent row.
    /// </summary>
    private readonly Dictionary<string, (string ToolCallId, string? Role)> _knownChildren = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Launch call IDs for <c>invoke_subagent</c> calls that have started but not yet reported launch completion, with their role.
    /// </summary>
    private readonly Dictionary<string, string?> _openSubagents = new(StringComparer.Ordinal);

    /// <summary>
    /// Subagent conversation IDs observed in <c>transcript.jsonl</c> before ACP delivered their launch completion.
    /// </summary>
    private readonly Queue<string> _unassignedSubagentIds = new();

    /// <summary>
    /// Subagent reports observed in <c>transcript.jsonl</c> before the child was registered, keyed by conversation ID.
    /// </summary>
    private readonly Dictionary<string, string?> _earlyFinishedSubagents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Background task IDs already reported finished/cancelled in <c>transcript.jsonl</c> so late promotions don't resurrect them.
    /// </summary>
    private readonly HashSet<string> _finishedTaskIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pending <c>manage_subagents</c>, <c>manage_task</c>, and <c>send_message</c> calls awaiting completion.
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
    private readonly Dictionary<string, AwaitedWork> _awaitedWork = new(StringComparer.Ordinal);

    /// <summary>
    /// Number of awaited commands or timers that completed out-of-turn whose wake-up replies haven't finished yet.
    /// </summary>
    private int _pendingPostTurnToolWakeups;

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
    /// Path to Antigravity's live <c>transcript.jsonl</c> for this session, when known.
    /// </summary>
    private string? _transcriptPath;

    /// <summary>
    /// Byte offset already consumed from <see cref="_transcriptPath"/>.
    /// </summary>
    private long _transcriptOffset;

    /// <summary>
    /// Whether at least one line has been read from <see cref="_transcriptPath"/> in this live session.
    /// </summary>
    private bool _transcriptSeenData;

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
    /// Polls <see cref="_transcriptPath"/> while background work is active, and stops when idle.
    /// </summary>
    private readonly Timer _transcriptPoll;

    /// <summary>
    /// Unprompted or in-turn wake-up replies since the running subagents launched, used as a pure-ACP fallback when
    /// <c>transcript.jsonl</c> is unavailable.
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
        _transcriptPoll = new Timer(_ => SyncTranscript());
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
    /// Binds this tracker to the live Antigravity session's <c>transcript.jsonl</c>, starting from the current end of the file
    /// so resumed sessions never replay older entries.
    /// </summary>
    public void AttachSession(string sessionId, string? transcriptPath = null)
    {
        var path = transcriptPath
            ?? (AntigravityFiles.IsId(sessionId)
                ? Path.Combine(AntigravityFiles.DefaultHome, "brain", sessionId, ".system_generated", "logs", "transcript.jsonl")
                : null);
        if (string.IsNullOrWhiteSpace(path)) return;

        long initialOffset = 0;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) initialOffset = info.Length;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        lock (_gate)
        {
            _transcriptPath = path;
            _transcriptOffset = initialOffset;
            _transcriptSeenData = false;
            UpdateTranscriptPollLocked();
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
            _pendingPostTurnToolWakeups = 0;
        }
    }

    /// <summary>
    /// Finishes per-prompt tracking when <c>session/prompt</c> returns, syncing the live transcript first and promoting
    /// still-open commands and timers on normal completion.
    /// </summary>
    public void EndTurn(bool promote)
    {
        List<AgentEvent> events = [];
        lock (_gate)
        {
            SyncTranscriptLocked(events);
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

        if (kind == "usage_update")
        {
            lock (_gate)
            {
                SyncTranscriptLocked(events);
                if (inTurn && _turnHadModelOutput)
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

        if (inTurn && kind is "agent_message_chunk" or "agent_thought_chunk")
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
                        var commandLine = AntigravityTurn.CommandLine(rawInput);
                        _openCommands[id] = new OpenWork(desc, isDaemon, isRecurring: false, commandLine);
                    }
                    else if (toolName == "schedule")
                    {
                        var desc = AntigravityTurn.ScheduleDescription(rawInput, title);
                        var isRecurring = CodexJson.Str(rawInput, "CronExpression") is { Length: > 0 };
                        var prompt = CodexJson.Str(rawInput, "Prompt");
                        _openTimers[id] = new OpenWork(desc, isDaemon, isRecurring, prompt);
                    }
                    else if (toolName == "invoke_subagent")
                    {
                        _openSubagents[id] = CodexJson.Str(rawInput, "Role");
                    }
                    else if (toolName is "manage_subagents" or "manage_task" or "send_message")
                    {
                        _openManageCalls[id] = ParseManageCall(toolName, rawInput);
                    }

                    UpdateTranscriptPollLocked();
                    return;
                }

                HandleTerminalToolLocked(id, toolName, status!, update, inTurn, events);
                UpdateTranscriptPollLocked();
            }

            Emit(events);
            return;
        }

        if (kind != "tool_call_update" || !isTerminal) return;

        lock (_gate)
        {
            HandleTerminalToolLocked(id, toolName, status!, update, inTurn, events);
            UpdateTranscriptPollLocked();
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
            SyncTranscriptLocked(promoted);

            foreach (var (id, work) in _openCommands)
            {
                if (work.IsDaemon) continue;
                if (work.TaskId is { } tid && _finishedTaskIds.Contains(tid)) continue;

                _awaitedWork[id] = new AwaitedWork("local_bash", work.IsRecurring, work.MatchHint, work.TaskId);
                promoted.Add(new TaskStartedEvent(work.TaskId ?? "", id, "local_bash", work.Description));
            }

            foreach (var (id, work) in _openTimers)
            {
                if (work.IsDaemon) continue;
                if (work.TaskId is { } tid && _finishedTaskIds.Contains(tid)) continue;

                _awaitedWork[id] = new AwaitedWork("timer", work.IsRecurring, work.MatchHint, work.TaskId);
                promoted.Add(new TaskStartedEvent(work.TaskId ?? "", id, "timer", work.Description));
            }

            _openCommands.Clear();
            _openTimers.Clear();
            UpdateTranscriptPollLocked();
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
            UpdateTranscriptPollLocked();
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
    /// Reads any newly appended lines from Antigravity's live <c>transcript.jsonl</c> and emits resulting state updates.
    /// </summary>
    internal void SyncTranscript()
    {
        List<AgentEvent> events = [];
        lock (_gate)
        {
            SyncTranscriptLocked(events);
            UpdateTranscriptPollLocked();
        }

        Emit(events);
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
            _unassignedSubagentIds.Clear();
            _earlyFinishedSubagents.Clear();
            _finishedTaskIds.Clear();
            _pendingPostTurnToolWakeups = 0;
            _turnHadModelOutput = false;
            _awaitingWakeupInTurn = false;
            _inTurnWakeup = false;
            _pendingToolWakeups = 0;
            _transcriptPoll.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
        _transcriptPoll.Dispose();
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
            else if (!inTurn) _pendingPostTurnToolWakeups++;
        }
        else if (wasOpenTool && inTurn && _awaitingWakeupInTurn)
        {
            _pendingToolWakeups++;
        }

        var wasOpenSubagent = _openSubagents.Remove(id, out var pendingRole);
        if (status == "completed" && (toolName == "invoke_subagent" || wasOpenSubagent) && !_children.Any(c => c.ToolCallId == id))
        {
            var role = pendingRole ?? CodexJson.Str(CodexJson.Any(update, "rawInput"), "Role");
            var conversationId = ExtractConversationId(update);
            if (conversationId is null && _unassignedSubagentIds.Count > 0)
            {
                conversationId = _unassignedSubagentIds.Dequeue();
            }

            if (conversationId is not null)
            {
                _knownChildren[conversationId] = (id, role);
            }

            if (conversationId is not null && _earlyFinishedSubagents.Remove(conversationId, out var earlyReport))
            {
                events.Add(new SubagentStateEvent(id, "completed", null, null, earlyReport));
            }
            else
            {
                _children.Add(new ChildSubagent(id, conversationId, role, DateTimeOffset.UtcNow + GiveUp));
                ScheduleGiveUpLocked();
            }
        }

        PendingManageCall? manageCall = null;
        if (_openManageCalls.Remove(id, out var pending))
        {
            manageCall = pending;
        }
        else if (toolName is "manage_subagents" or "manage_task" or "send_message")
        {
            manageCall = ParseManageCall(toolName, CodexJson.Any(update, "rawInput"));
        }

        // Pull in any conversation/task IDs or list outputs Antigravity just wrote to transcript.jsonl.
        SyncTranscriptLocked(events);

        if (status == "completed" && manageCall is not null)
        {
            ApplyManageCallLocked(manageCall, update, events);
        }
    }

    /// <summary>
    /// Applies a completed <c>manage_subagents</c>, <c>manage_task</c>, or <c>send_message</c> call to tracked background state.
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
                    if (index < 0)
                    {
                        index = _children.FindIndex(c => c.ConversationId is null);
                    }

                    if (index >= 0)
                    {
                        var removed = _children[index];
                        _children.RemoveAt(index);
                        events.Add(new SubagentStateEvent(removed.ToolCallId, "stopped", null, null, null));
                    }
                }

                ReconcileFinishedCountLocked(events);
            }
            else if (string.Equals(call.Action, "list", StringComparison.OrdinalIgnoreCase) && _children.Count > 0 && ReportsNoActiveItems(update))
            {
                EndAllChildrenLocked("completed", null, events);
            }
        }
        else if (call.ToolName == "manage_task")
        {
            if (string.Equals(call.Action, "kill", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var target in call.TargetIds)
                {
                    StopTaskByTargetLocked(target, events);
                }
            }
            else if (string.Equals(call.Action, "list", StringComparison.OrdinalIgnoreCase) && _awaitedWork.Count > 0 && ReportsNoActiveItems(update))
            {
                FinishAllAwaitedWorkLocked("completed", events);
            }
        }
        else if (call.ToolName == "send_message")
        {
            foreach (var recipient in call.TargetIds)
            {
                if (_knownChildren.TryGetValue(recipient, out var known)
                    && !_children.Any(c => c.ToolCallId == known.ToolCallId))
                {
                    _children.Add(new ChildSubagent(known.ToolCallId, recipient, known.Role, DateTimeOffset.UtcNow + GiveUp));
                    events.Add(new SubagentStateEvent(known.ToolCallId, "running", null, null, null));
                    ScheduleGiveUpLocked();
                }
            }
        }
    }

    /// <summary>
    /// Reads newly appended JSONL lines from <see cref="_transcriptPath"/> and applies them while holding <see cref="_gate"/>.
    /// </summary>
    private void SyncTranscriptLocked(List<AgentEvent> events)
    {
        if (_transcriptPath is null) return;

        List<string> lines;
        try
        {
            if (!File.Exists(_transcriptPath)) return;

            using var stream = new FileStream(_transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _transcriptOffset)
            {
                _transcriptOffset = stream.Length;
                return;
            }

            var available = stream.Length - _transcriptOffset;
            if (available <= 0) return;

            stream.Position = _transcriptOffset;
            var buffer = new byte[available];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }

            if (read == 0) return;

            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0) return;

            _transcriptOffset += lastNewline + 1;
            var chunk = Encoding.UTF8.GetString(buffer, 0, lastNewline);
            lines = [.. chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                _transcriptSeenData = true;
                ApplyTranscriptStepLocked(doc.RootElement, events);
            }
            catch (JsonException)
            {
            }
        }
    }

    /// <summary>
    /// Interprets one step from Antigravity's <c>transcript.jsonl</c> while holding <see cref="_gate"/>.
    /// </summary>
    private void ApplyTranscriptStepLocked(JsonElement step, List<AgentEvent> events)
    {
        var type = CodexJson.Str(step, "type");
        var source = CodexJson.Str(step, "source");
        var content = CodexJson.Str(step, "content");
        if (string.IsNullOrEmpty(content)) return;

        if (type == "SYSTEM_MESSAGE" || source == "SYSTEM")
        {
            ApplySystemMessageLocked(content, events);
            return;
        }

        if (type != "GENERIC") return;

        // 1. Subagent launch result: "Created the following subagent:\n{ \"conversationId\": \"...\" }"
        if (content.Contains("Created the following subagent:", StringComparison.Ordinal))
        {
            var match = ConversationIdJsonPattern().Match(content);
            if (match.Success)
            {
                BindSubagentConversationIdLocked(match.Groups[1].Value, events);
            }

            return;
        }

        // 2. Backgrounded command or timer: "Tool is running as a background task with task id: <id>"
        var bgMatch = BackgroundTaskIdPattern().Match(content);
        if (bgMatch.Success)
        {
            var taskId = bgMatch.Groups[1].Value.Trim();
            var descMatch = TaskDescriptionLinePattern().Match(content);
            var taskDesc = descMatch.Success ? descMatch.Groups[1].Value.Trim() : null;
            BindBackgroundTaskIdLocked(taskId, taskDesc);
            return;
        }

        // 3. manage_subagents(list): "You have N active subagent(s):\n[...]"
        if (content.Contains("active subagent(s):", StringComparison.OrdinalIgnoreCase))
        {
            ReconcileSubagentListFromTranscriptLocked(content, events);
            return;
        }

        // 4. manage_task(list): "No background tasks are currently running." or "You have N background task(s) currently running:\n[...]"
        if (content.Contains("No background tasks are currently running", StringComparison.OrdinalIgnoreCase)
            || content.Contains("background task(s) currently running:", StringComparison.OrdinalIgnoreCase))
        {
            ReconcileTaskListFromTranscriptLocked(content, events);
            return;
        }

        // 5. manage_task(kill): "Task \"<taskId>\" cancelled."
        var cancelledTask = TaskCancelledPattern().Match(content);
        if (cancelledTask.Success)
        {
            StopTaskByTargetLocked(cancelledTask.Groups[1].Value.Trim(), events);
            return;
        }

        // 6. manage_subagents(kill): "Killed roles: Role1, Role2"
        var killedRoles = KilledRolesPattern().Match(content);
        if (killedRoles.Success)
        {
            foreach (var role in killedRoles.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var index = _children.FindIndex(c => string.Equals(c.Role, role, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var removed = _children[index];
                    _children.RemoveAt(index);
                    events.Add(new SubagentStateEvent(removed.ToolCallId, "stopped", null, null, null));
                }
            }

            ReconcileFinishedCountLocked(events);
        }
    }

    /// <summary>
    /// Processes a <c>SYSTEM_MESSAGE</c> entry from <c>transcript.jsonl</c> (subagent reply, task completion, or restart notice).
    /// </summary>
    private void ApplySystemMessageLocked(string content, List<AgentEvent> events)
    {
        var matches = SystemMessageHeaderPattern().Matches(content);
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var sender = match.Groups[1].Value.Trim();
            var bodyStart = match.Index + match.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            var body = content[bodyStart..bodyEnd];
            var closeIdx = body.LastIndexOf("</SYSTEM_MESSAGE>", StringComparison.Ordinal);
            if (closeIdx >= 0) body = body[..closeIdx];
            body = body.Trim();

            if (string.Equals(sender, "system", StringComparison.OrdinalIgnoreCase))
            {
                if (body.Contains("subagents and background tasks have been stopped", StringComparison.OrdinalIgnoreCase))
                {
                    EndAllChildrenLocked("stopped", null, events);
                    FinishAllAwaitedWorkLocked("stopped", events);
                    _openCommands.Clear();
                    _openTimers.Clear();
                    _openSubagents.Clear();
                }

                continue;
            }

            // Background command or timer completion: sender is "<sessionId>/task-<N>".
            if (sender.Contains("/task-", StringComparison.OrdinalIgnoreCase))
            {
                HandleTranscriptTaskMessageLocked(sender, body, events);
                continue;
            }

            // Subagent message back to parent: sender is the subagent's UUID conversationId.
            if (Guid.TryParseExact(sender, "D", out _))
            {
                var index = _children.FindIndex(c => string.Equals(c.ConversationId, sender, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var child = _children[index];
                    _children.RemoveAt(index);
                    events.Add(new SubagentStateEvent(child.ToolCallId, "completed", null, null, body.Length > 0 ? body : null));
                    ReconcileFinishedCountLocked(events);
                }
                else if (!_knownChildren.ContainsKey(sender))
                {
                    _earlyFinishedSubagents[sender] = body.Length > 0 ? body : null;
                }
            }
        }
    }

    /// <summary>
    /// Binds a subagent conversation ID learned from <c>transcript.jsonl</c> to the oldest unbound running child.
    /// </summary>
    private void BindSubagentConversationIdLocked(string conversationId, List<AgentEvent> events)
    {
        if (_children.Any(c => string.Equals(c.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var unbound = _children.FirstOrDefault(c => c.ConversationId is null);
        if (unbound is null)
        {
            _unassignedSubagentIds.Enqueue(conversationId);
            return;
        }

        unbound.ConversationId = conversationId;
        _knownChildren[conversationId] = (unbound.ToolCallId, unbound.Role);
        if (_earlyFinishedSubagents.Remove(conversationId, out var earlyReport))
        {
            _children.Remove(unbound);
            events.Add(new SubagentStateEvent(unbound.ToolCallId, "completed", null, null, earlyReport));
            ReconcileFinishedCountLocked(events);
        }
    }

    /// <summary>
    /// Binds a background task ID learned from <c>transcript.jsonl</c> to the corresponding open or awaited command/timer.
    /// </summary>
    private void BindBackgroundTaskIdLocked(string taskId, string? taskDescription)
    {
        var isTimer = taskDescription is not null
            && (taskDescription.StartsWith("Timer:", StringComparison.OrdinalIgnoreCase)
                || taskDescription.StartsWith("Cron:", StringComparison.OrdinalIgnoreCase));

        if (isTimer)
        {
            if (BindOpenWork(_openTimers, taskId, taskDescription)) return;
            if (BindAwaitedWork("timer", taskId, taskDescription)) return;
        }
        else
        {
            if (BindOpenWork(_openCommands, taskId, taskDescription)) return;
            if (BindAwaitedWork("local_bash", taskId, taskDescription)) return;
        }

        // Fallback across the other bucket if description didn't disambiguate.
        if (!BindOpenWork(isTimer ? _openCommands : _openTimers, taskId, taskDescription))
        {
            BindAwaitedWork(isTimer ? "local_bash" : "timer", taskId, taskDescription);
        }
    }

    private static bool BindOpenWork(Dictionary<string, OpenWork> dict, string taskId, string? taskDescription)
    {
        if (dict.Values.Any(w => string.Equals(w.TaskId, taskId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        OpenWork? best = null;
        if (!string.IsNullOrEmpty(taskDescription))
        {
            best = dict.Values.FirstOrDefault(w => w.TaskId is null && w.MatchHint is { Length: > 0 } hint
                && taskDescription.Contains(hint, StringComparison.Ordinal));
        }

        best ??= dict.Values.FirstOrDefault(w => w.TaskId is null);
        if (best is null) return false;

        best.TaskId = taskId;
        best.IsBackgrounded = true;
        return true;
    }

    private bool BindAwaitedWork(string taskType, string taskId, string? taskDescription)
    {
        if (_awaitedWork.Values.Any(w => string.Equals(w.TaskId, taskId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        AwaitedWork? best = null;
        if (!string.IsNullOrEmpty(taskDescription))
        {
            best = _awaitedWork.Values.FirstOrDefault(w => w.TaskType == taskType && w.TaskId is null && w.MatchHint is { Length: > 0 } hint
                && taskDescription.Contains(hint, StringComparison.Ordinal));
        }

        best ??= _awaitedWork.Values.FirstOrDefault(w => w.TaskType == taskType && w.TaskId is null);
        if (best is null) return false;

        best.TaskId = taskId;
        return true;
    }

    /// <summary>
    /// Clears an open or awaited command/timer when <c>transcript.jsonl</c> records its completion or cancellation message.
    /// </summary>
    private void HandleTranscriptTaskMessageLocked(string taskId, string body, List<AgentEvent> events)
    {
        var isCancelled = body.Contains("was canceled", StringComparison.OrdinalIgnoreCase)
            || body.Contains("was cancelled", StringComparison.OrdinalIgnoreCase);
        var isFinishedResult = body.Contains("finished with result:", StringComparison.OrdinalIgnoreCase);
        var status = isCancelled ? "stopped"
            : isFinishedResult && HasNonZeroExitCode(body) ? "failed"
            : "completed";

        // Remove from open commands/timers so PromoteOpenWork never promotes an already-finished task.
        foreach (var (callId, work) in _openCommands.ToList())
        {
            if (string.Equals(work.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
            {
                _openCommands.Remove(callId);
                _finishedTaskIds.Add(taskId);
                return;
            }
        }

        foreach (var (callId, work) in _openTimers.ToList())
        {
            if (string.Equals(work.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
            {
                if (work.IsRecurring && !isCancelled && !isFinishedResult) return;

                _openTimers.Remove(callId);
                _finishedTaskIds.Add(taskId);
                return;
            }
        }

        foreach (var (callId, awaited) in _awaitedWork.ToList())
        {
            if (!string.Equals(awaited.TaskId, taskId, StringComparison.OrdinalIgnoreCase)) continue;
            if (awaited.IsRecurring && !isCancelled && !isFinishedResult) return;

            _awaitedWork.Remove(callId);
            _finishedTaskIds.Add(taskId);
            EmitTaskEnded(callId, awaited.TaskType, status, events);
            return;
        }

        if (isCancelled || isFinishedResult)
        {
            _finishedTaskIds.Add(taskId);
        }
    }

    /// <summary>
    /// Reconciles tracked subagents against the JSON list returned by <c>manage_subagents(Action: "list")</c>.
    /// Finished subagents stay listed in Antigravity with <c>state: "idle"</c>, while killed ones are absent.
    /// </summary>
    private void ReconcileSubagentListFromTranscriptLocked(string content, List<AgentEvent> events)
    {
        if (_children.Count == 0) return;

        var arrayStart = content.IndexOf('[');
        var arrayEnd = content.LastIndexOf(']');
        if (arrayStart < 0 || arrayEnd < arrayStart) return;

        Dictionary<string, string?> listedStates = new(StringComparer.OrdinalIgnoreCase);
        var anyRunning = false;
        try
        {
            using var doc = JsonDocument.Parse(content[arrayStart..(arrayEnd + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var convId = CodexJson.Str(item, "conversationId") ?? CodexJson.Str(item, "conversation_id");
                var state = CodexJson.Str(item, "state");
                if (IsActiveSubagentState(state)) anyRunning = true;
                if (!string.IsNullOrWhiteSpace(convId))
                {
                    listedStates[convId] = state;
                }
            }
        }
        catch (JsonException)
        {
            return;
        }

        for (var i = _children.Count - 1; i >= 0; i--)
        {
            var child = _children[i];
            if (child.ConversationId is { } cid)
            {
                if (!listedStates.TryGetValue(cid, out var state))
                {
                    _children.RemoveAt(i);
                    events.Add(new SubagentStateEvent(child.ToolCallId, "stopped", null, null, null));
                }
                else if (!IsActiveSubagentState(state))
                {
                    _children.RemoveAt(i);
                    events.Add(new SubagentStateEvent(child.ToolCallId, MapSubagentState(state), null, null, null));
                }
            }
            else if (!anyRunning)
            {
                _children.RemoveAt(i);
                events.Add(new SubagentStateEvent(child.ToolCallId, "completed", null, null, null));
            }
        }

        ReconcileFinishedCountLocked(events);
    }

    /// <summary>
    /// Reconciles tracked background tasks against the output of <c>manage_task(Action: "list")</c>.
    /// </summary>
    private void ReconcileTaskListFromTranscriptLocked(string content, List<AgentEvent> events)
    {
        if (content.Contains("No background tasks are currently running", StringComparison.OrdinalIgnoreCase))
        {
            FinishAllAwaitedWorkLocked("completed", events);
            RemoveBackgroundedOpenWorkLocked();
            return;
        }

        var arrayStart = content.IndexOf('[');
        var arrayEnd = content.LastIndexOf(']');
        if (arrayStart < 0 || arrayEnd < arrayStart) return;

        HashSet<string> activeTaskIds = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(content[arrayStart..(arrayEnd + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if ((CodexJson.Str(item, "taskId") ?? CodexJson.Str(item, "task_id")) is { Length: > 0 } tid)
                {
                    activeTaskIds.Add(tid);
                }
            }
        }
        catch (JsonException)
        {
            return;
        }

        if (activeTaskIds.Count == 0)
        {
            FinishAllAwaitedWorkLocked("completed", events);
            RemoveBackgroundedOpenWorkLocked();
            return;
        }

        foreach (var (callId, awaited) in _awaitedWork.ToList())
        {
            if (awaited.TaskId is { } tid && !activeTaskIds.Contains(tid))
            {
                _awaitedWork.Remove(callId);
                _finishedTaskIds.Add(tid);
                EmitTaskEnded(callId, awaited.TaskType, "completed", events);
            }
        }

        foreach (var (callId, work) in _openCommands.ToList())
        {
            if (work.TaskId is { } tid && !activeTaskIds.Contains(tid))
            {
                _openCommands.Remove(callId);
                _finishedTaskIds.Add(tid);
            }
        }

        foreach (var (callId, work) in _openTimers.ToList())
        {
            if (work.TaskId is { } tid && !activeTaskIds.Contains(tid))
            {
                _openTimers.Remove(callId);
                _finishedTaskIds.Add(tid);
            }
        }
    }

    private void RemoveBackgroundedOpenWorkLocked()
    {
        foreach (var (callId, work) in _openCommands.ToList())
        {
            if (work.IsBackgrounded) _openCommands.Remove(callId);
        }

        foreach (var (callId, work) in _openTimers.ToList())
        {
            if (work.IsBackgrounded) _openTimers.Remove(callId);
        }
    }

    /// <summary>
    /// Stops an open or awaited command/timer targeted by a <c>manage_task(Action: "kill")</c> call.
    /// </summary>
    private void StopTaskByTargetLocked(string target, List<AgentEvent> events)
    {
        _finishedTaskIds.Add(target);

        foreach (var (callId, work) in _openCommands.ToList())
        {
            if (string.Equals(work.TaskId, target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(callId, target, StringComparison.OrdinalIgnoreCase))
            {
                _openCommands.Remove(callId);
                return;
            }
        }

        foreach (var (callId, work) in _openTimers.ToList())
        {
            if (string.Equals(work.TaskId, target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(callId, target, StringComparison.OrdinalIgnoreCase))
            {
                _openTimers.Remove(callId);
                return;
            }
        }

        string? matchedCallId = null;
        foreach (var (callId, awaited) in _awaitedWork)
        {
            if (string.Equals(awaited.TaskId, target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(callId, target, StringComparison.OrdinalIgnoreCase))
            {
                matchedCallId = callId;
                break;
            }
        }

        if (matchedCallId is null && _awaitedWork.Count == 1)
        {
            var only = _awaitedWork.First();
            if (only.Value.TaskId is null) matchedCallId = only.Key;
        }

        if (matchedCallId is not null && _awaitedWork.Remove(matchedCallId, out _))
        {
            events.Add(new TaskNotificationEvent(null, matchedCallId, "stopped", null));
        }
    }

    private void FinishAllAwaitedWorkLocked(string status, List<AgentEvent> events)
    {
        foreach (var (callId, awaited) in _awaitedWork.ToList())
        {
            if (awaited.TaskId is { } tid) _finishedTaskIds.Add(tid);
            EmitTaskEnded(callId, awaited.TaskType, status, events);
        }

        _awaitedWork.Clear();
    }

    private static void EmitTaskEnded(string toolCallId, string taskType, string status, List<AgentEvent> events)
    {
        if (taskType == "timer" || status is "stopped" or "killed")
        {
            events.Add(new TaskNotificationEvent(null, toolCallId, status, null));
        }
        else
        {
            events.Add(new ForegroundTaskEndedEvent("", toolCallId, status));
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
    /// Counts an in-turn wake-up as either a pending tool wake-up or one subagent's finish (fallback when transcript IDs aren't bound).
    /// </summary>
    private void CountInTurnWakeupLocked(List<AgentEvent> events)
    {
        if (_pendingToolWakeups > 0)
        {
            _pendingToolWakeups--;
            return;
        }

        // When the live transcript is active and every tracked child has a bound conversationId, exact transcript messages close them.
        if (_transcriptSeenData && _children.All(c => c.ConversationId is not null)) return;

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
            SyncTranscriptLocked(events);

            if (_pendingPostTurnToolWakeups > 0)
            {
                _pendingPostTurnToolWakeups--;
                if (_children.Count == 0) _pendingPostTurnToolWakeups = 0;
                UpdateTranscriptPollLocked();
            }
            else if (_children.Count > 0 && !(_transcriptSeenData && _children.All(c => c.ConversationId is not null)))
            {
                if (++_finished >= _children.Count)
                {
                    EndAllChildrenLocked("completed", null, events);
                }
            }
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
            SyncTranscriptLocked(events);

            for (var i = _children.Count - 1; i >= 0; i--)
            {
                if (_children[i].GiveUpAt <= now)
                {
                    var child = _children[i];
                    _children.RemoveAt(i);
                    events.Add(new SubagentStateEvent(child.ToolCallId, "unreported", null, null, "Antigravity did not report this subagent's finish."));
                }
            }

            ReconcileFinishedCountLocked(events);
            UpdateTranscriptPollLocked();
        }

        Emit(events);
    }

    private void ReconcileFinishedCountLocked(List<AgentEvent> events)
    {
        if (_children.Count == 0)
        {
            _finished = 0;
            _giveUp.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        else if (_finished >= _children.Count && !(_transcriptSeenData && _children.All(c => c.ConversationId is not null)))
        {
            EndAllChildrenLocked("completed", null, events);
        }
        else
        {
            ScheduleGiveUpLocked();
        }
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
            UpdateTranscriptPollLocked();
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

    private void UpdateTranscriptPollLocked()
    {
        var active = _transcriptPath is not null
            && (_children.Count > 0 || _awaitedWork.Count > 0 || _openCommands.Count > 0 || _openTimers.Count > 0 || _openSubagents.Count > 0);
        _transcriptPoll.Change(
            active ? TranscriptPollInterval : Timeout.InfiniteTimeSpan,
            active ? TranscriptPollInterval : Timeout.InfiniteTimeSpan);
    }

    private void Emit(List<AgentEvent> events)
    {
        foreach (var e in events)
        {
            _raise(e);
        }
    }

    private static bool IsActiveSubagentState(string? state) =>
        string.IsNullOrWhiteSpace(state)
        || state.Equals("running", StringComparison.OrdinalIgnoreCase)
        || state.Equals("pending", StringComparison.OrdinalIgnoreCase)
        || state.Equals("starting", StringComparison.OrdinalIgnoreCase)
        || state.Equals("working", StringComparison.OrdinalIgnoreCase)
        || state.Equals("in_progress", StringComparison.OrdinalIgnoreCase);

    private static string MapSubagentState(string? state) =>
        state?.ToLowerInvariant() switch
        {
            "failed" or "error" => "failed",
            "stopped" or "killed" or "cancelled" or "canceled" => "stopped",
            _ => "completed",
        };

    private static bool HasNonZeroExitCode(string body)
    {
        var match = ExitCodePattern().Match(body);
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) && code != 0;
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
        else if (toolName == "send_message")
        {
            if ((CodexJson.Str(rawInput, "Recipient") ?? CodexJson.Str(rawInput, "recipient")) is { Length: > 0 } recipient)
                targets.Add(recipient);
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

    [GeneratedRegex(@"""conversationId""\s*:\s*""([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})""")]
    private static partial Regex ConversationIdJsonPattern();

    [GeneratedRegex(@"Tool is running as a background task with task id:\s*(\S+)")]
    private static partial Regex BackgroundTaskIdPattern();

    [GeneratedRegex(@"^Task Description:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TaskDescriptionLinePattern();

    [GeneratedRegex(@"\[Message\][^\n]*?\bsender=([^\s\]]+)[^\n]*?\bcontent=")]
    private static partial Regex SystemMessageHeaderPattern();

    [GeneratedRegex(@"Task\s+""([^""]+)""\s+cancell?ed", RegexOptions.IgnoreCase)]
    private static partial Regex TaskCancelledPattern();

    [GeneratedRegex(@"^Killed roles:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex KilledRolesPattern();

    [GeneratedRegex(@"exited with code\s+(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExitCodePattern();
}
