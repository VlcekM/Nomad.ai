using System.Text.Json;
using Nomad.Core.Agents;
using Nomad.Core.Codex;

namespace Nomad.Core.Antigravity;

/// <summary>
/// Follows what Antigravity does between prompts. ACP only acknowledges a subagent's launch; when one finishes, the parent replies
/// on its own, with no prompt running. Each such reply counts as one subagent finished, and the rows end once replies match launches.
/// </summary>
internal sealed class AntigravityBackgroundWork : IDisposable
{
    /// <summary>
    /// How long the parent stays quiet after a model call before its unprompted reply counts as ended.
    /// </summary>
    private static readonly TimeSpan QuietEnd = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long subagents may run with the parent silent before their rows end as unreported (a finish it never woke for).
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
    /// The launch calls of subagents still running, oldest first.
    /// </summary>
    private readonly List<string> _children = [];

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
    /// Unprompted replies since the running subagents launched, each one subagent's finish.
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
    /// Tracks tool launches and completions across prompts (subagents, backgrounded commands, and timers).
    /// </summary>
    public void Track(JsonElement update)
    {
        var kind = CodexJson.Str(update, "sessionUpdate");
        var id = CodexJson.Str(update, "toolCallId");
        if (id is null) return;

        var status = CodexJson.Str(update, "status");
        var toolName = CodexJson.Str(update, "name");
        if (kind == "tool_call")
        {
            if (status is "completed" or "failed") return;

            var rawInput = CodexJson.Any(update, "rawInput");
            var title = CodexJson.Str(update, "title");
            var isDaemon = CodexJson.Bool(rawInput, "IsDaemon") == true;
            if (toolName == "run_command" || CodexJson.Str(update, "kind") == "execute")
            {
                var desc = AntigravityTurn.CommandDescription(rawInput, title) ?? "command";
                lock (_gate) _openCommands[id] = new OpenWork(desc, isDaemon);
            }
            else if (toolName == "schedule")
            {
                var desc = AntigravityTurn.ScheduleDescription(rawInput, title);
                lock (_gate) _openTimers[id] = new OpenWork(desc, isDaemon);
            }

            return;
        }

        if (kind != "tool_call_update" || status is not ("completed" or "failed")) return;

        lock (_gate)
        {
            _openCommands.Remove(id);
            _openTimers.Remove(id);
            if (_awaitedWork.Remove(id)) _wokeByTool = true;
            if (status == "completed" && toolName == "invoke_subagent" && !_children.Contains(id))
            {
                _children.Add(id);
                _giveUp.Change(GiveUp, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Promotes commands and timers that are still running when a prompt returns <c>end_turn</c> to awaited background work.
    /// </summary>
    public void PromoteOpenWork()
    {
        List<(string Id, string TaskType, string Description)> promoted = [];
        lock (_gate)
        {
            foreach (var (id, work) in _openCommands)
            {
                if (work.IsDaemon) continue;
                _awaitedWork[id] = "local_bash";
                promoted.Add((id, "local_bash", work.Description));
            }

            foreach (var (id, work) in _openTimers)
            {
                if (work.IsDaemon) continue;
                _awaitedWork[id] = "timer";
                promoted.Add((id, "timer", work.Description));
            }

            _openCommands.Clear();
            _openTimers.Clear();
        }

        foreach (var (id, taskType, description) in promoted)
        {
            _raise(new TaskStartedEvent("", id, taskType, description));
        }
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
        bool tracking;
        lock (_gate)
        {
            if (_reply is null && kind == "tool_call_update")
            {
                if (AntigravityTurn.CreateToolResult(update) is { } result)
                {
                    _raise(result);
                }

                return;
            }

            if (_reply is null && !content) return;

            reply = _reply ??= BeginReply();
            tracking = _children.Count > 0;
        }

        if (content) reply.Update(update);

        if (tracking) _giveUp.Change(GiveUp, Timeout.InfiniteTimeSpan);

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
            _wokeByTool = false;
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
    /// Starts showing a reply the agent began by itself.
    /// </summary>
    private AntigravityTurn BeginReply()
    {
        _replyStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _raise(new TurnResumedEvent());
        return new AntigravityTurn(_model(), _raise);
    }

    /// <summary>
    /// Counts an unprompted reply as one subagent's finish (unless it was woken by a command or timer); all have finished once replies match launches.
    /// </summary>
    private void CountFinish()
    {
        lock (_gate)
        {
            if (_wokeByTool)
            {
                _wokeByTool = false;
                return;
            }

            if (_children.Count == 0) return;
            if (++_finished < _children.Count) return;
        }

        EndChildren("completed", null);
    }

    /// <summary>
    /// The parent stayed silent: its unreported subagents end without a status.
    /// </summary>
    private void GiveUpChildren() => EndChildren("unreported", "Antigravity did not report this subagent's finish.");

    /// <summary>
    /// Ends every running subagent's row with <paramref name="status"/>.
    /// </summary>
    private void EndChildren(string status, string? message)
    {
        List<string> ended;
        lock (_gate)
        {
            ended = [.. _children];
            _children.Clear();
            _finished = 0;
        }

        _giveUp.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var id in ended)
        {
            _raise(new SubagentStateEvent(id, status, null, null, message));
        }
    }
}
