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
    /// The launch calls of subagents still running, oldest first.
    /// </summary>
    private readonly List<string> _children = [];

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
        _quiet = new Timer(_ => EndReply());
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
    /// Notes a subagent launched, from any update of a prompt or of a reply.
    /// </summary>
    public void Track(JsonElement update)
    {
        if (CodexJson.Str(update, "sessionUpdate") != "tool_call_update" || CodexJson.Str(update, "status") != "completed") return;
        if (CodexJson.Str(update, "name") != "invoke_subagent" || CodexJson.Str(update, "toolCallId") is not { } id) return;

        lock (_gate)
        {
            if (!_children.Contains(id)) _children.Add(id);
        }

        _giveUp.Change(GiveUp, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Shows an update that arrived with no prompt running, as a reply the agent began itself.
    /// </summary>
    public void Update(JsonElement update)
    {
        var kind = CodexJson.Str(update, "sessionUpdate");
        var content = kind is "agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update";
        AntigravityTurn? reply;
        bool tracking;
        lock (_gate)
        {
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
    public void EndReply()
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
        var duration = (long)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _raise(new TurnCompletedEvent(new TurnResult { Subtype = "end_turn", DurationMs = duration }));
        CountFinish();
    }

    /// <summary>
    /// The process stopped, and its subagents with it.
    /// </summary>
    public void Stop()
    {
        EndReply();
        EndChildren("stopped", null);
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
    /// Counts an unprompted reply as one subagent's finish; all have finished once replies match launches.
    /// </summary>
    private void CountFinish()
    {
        lock (_gate)
        {
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
