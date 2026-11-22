using System.Text.Json;
using Nomad.Core.Agents;
using Nomad.Core.Antigravity;

namespace Nomad.Core.Tests;

/// <summary>
/// Checks the ACP event translation used by live turns and saved-session replay.
/// </summary>
public sealed class AntigravityTests
{
    /// <summary>
    /// Antigravity gives the delegated role and prompt in the invocation arguments.
    /// </summary>
    [Fact]
    public void MapsSubagentInvocationsToTheSharedTaskTool()
    {
        var events = new List<AgentEvent>();
        var turn = new AntigravityTurn(AntigravityBackend.DefaultModel, events.Add);
        Send(turn, """
            {"sessionUpdate":"tool_call","toolCallId":"sub-1","name":"invoke_subagent","title":"Run invoke_subagent",
             "kind":"other","status":"in_progress","rawInput":{"Role":"Reviewer","Prompt":"Check the diff","TypeName":"research","Model":"inherit"}}
            """);
        Send(turn, """{"sessionUpdate":"tool_call_update","toolCallId":"sub-1","status":"completed"}""");

        var call = Assert.Single(events.OfType<ToolCallEvent>());
        Assert.Equal("Task", call.Name);
        Assert.True(call.IsSubagentLaunch);
        Assert.Equal("Reviewer", call.Input.GetProperty("description").GetString());
        Assert.Equal("Check the diff", call.Input.GetProperty("prompt").GetString());
        Assert.Equal(call.ToolUseId, Assert.Single(events.OfType<ToolResultEvent>()).ToolUseId);
    }

    /// <summary>
    /// The quota command returns remaining fractions, while Nomad's bars show usage.
    /// </summary>
    [Fact]
    public void ParsesSharedGeminiQuotasAsUsedPercentages()
    {
        var limits = AntigravityUsage.Parse("""
            {"command":{"name":"usage","data":{"groups":[{"buckets":[
              {"id":"gemini-weekly","remaining_fraction":0.28,"reset_time":"2026-11-27T18:57:53Z"},
              {"id":"gemini-5h","remaining_fraction":0.06},
              {"id":"3p-weekly","remaining_fraction":0.5}
            ]}]}}}
            """);

        Assert.Collection(limits,
            weekly =>
            {
                Assert.Equal("weekly_all", weekly.Kind);
                Assert.Equal(72, weekly.Percent);
                Assert.Equal(DateTimeOffset.Parse("2026-11-27T18:57:53Z"), weekly.ResetsAt);
            },
            session =>
            {
                Assert.Equal("session", session.Kind);
                Assert.Equal(94, session.Percent);
                Assert.Equal("warning", session.Severity);
            });
    }

    /// <summary>
    /// A tool call closes the preceding streamed text before the next block starts.
    /// </summary>
    [Fact]
    public void StreamsTextAndToolsInTranscriptOrder()
    {
        var events = new List<AgentEvent>();
        var turn = new AntigravityTurn(AntigravityBackend.DefaultModel, events.Add);
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Checking "}}""");
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"the file."}}""");
        Send(turn, """{"sessionUpdate":"tool_call","toolCallId":"command-1","title":"Run command","kind":"execute","status":"in_progress"}""");
        Send(turn, """{"sessionUpdate":"tool_call_update","toolCallId":"command-1","status":"completed","rawOutput":"contents"}""");
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Done."}}""");
        turn.FinishBlock();

        var contents = events.Where(e => e is BlockCompletedEvent or ToolCallEvent or ToolResultEvent).ToArray();
        Assert.Collection(contents,
            e => Assert.Equal("Checking the file.", Assert.IsType<BlockCompletedEvent>(e).Text),
            e => Assert.Equal("command-1", Assert.IsType<ToolCallEvent>(e).ToolUseId),
            e => Assert.Equal("contents", Assert.IsType<ToolResultEvent>(e).Text),
            e => Assert.Equal("Done.", Assert.IsType<BlockCompletedEvent>(e).Text));
        Assert.Equal(AntigravityBackend.DefaultModel, Assert.Single(events.OfType<AssistantMessageStartedEvent>()).Model);
        Assert.True(Assert.Single(events.OfType<ToolCallEvent>()).IsShellCommand);
    }

    /// <summary>
    /// Passes a scoped document to the translator to catch retained JSON references.
    /// </summary>
    /// <summary>
    /// The parent replies by itself once per finished subagent; the rows end once replies match launches.
    /// </summary>
    [Fact]
    public async Task EndsSubagentsWhenTheParentHasRepliedForEach()
    {
        var events = new List<AgentEvent>();
        using var work = new AntigravityBackgroundWork(e => { lock (events) events.Add(e); }, () => AntigravityBackend.DefaultModel);
        Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"a","name":"invoke_subagent","status":"completed"}""");
        Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"b","name":"invoke_subagent","status":"completed"}""");

        await ReplyAsync(work, "A finished.");
        lock (events) Assert.Empty(events.OfType<SubagentStateEvent>());

        await ReplyAsync(work, "B finished.");
        lock (events)
        {
            Assert.Equal(2, events.OfType<TurnResumedEvent>().Count());
            Assert.Equal(2, events.OfType<TurnCompletedEvent>().Count());
            Assert.Equal(["a", "b"], events.OfType<SubagentStateEvent>().Where(s => s.Status == "completed").Select(s => s.AgentId));
        }
    }

    /// <summary>
    /// Sends an unprompted reply and waits for the quiet moment that ends it.
    /// </summary>
    private static async Task ReplyAsync(AntigravityBackgroundWork work, string text)
    {
        Feed(work, $$$"""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"{{{text}}}"}}""");
        Feed(work, """{"sessionUpdate":"usage_update","used":1,"size":2}""");
        for (var i = 0; i < 50 && work.IsReplying; i++) await Task.Delay(100);

        Assert.False(work.IsReplying);
    }

    /// <summary>
    /// Passes an update through as the backend does with no prompt running.
    /// </summary>
    private static void Feed(AntigravityBackgroundWork work, string update)
    {
        using var document = JsonDocument.Parse(update);
        work.Track(document.RootElement);
        work.Update(document.RootElement);
    }

    /// <summary>
    /// Notes a launch, as from a prompt's updates.
    /// </summary>
    private static void Track(AntigravityBackgroundWork work, string update)
    {
        using var document = JsonDocument.Parse(update);
        work.Track(document.RootElement);
    }

    /// <summary>
    /// Strips a streamed prompt-suggestion tag across chunk boundaries without leaking it into visible text.
    /// </summary>
    [Fact]
    public void StripsPromptSuggestionTagAcrossChunks()
    {
        var events = new List<AgentEvent>();
        var turn = new AntigravityTurn(AntigravityBackend.DefaultModel, events.Add);
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Fixed the bug."}}""");
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"\n\n<nomad-"}}""");
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"suggestion>Run the tests</nomad-sug"}}""");
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"gestion>\n"}}""");
        turn.FinishBlock();

        Assert.Equal("Run the tests", turn.Suggestion);
        var streamed = string.Concat(events.OfType<TextDeltaEvent>().Select(e => e.Text));
        Assert.Equal("Fixed the bug.", streamed);
        Assert.Equal("Fixed the bug.", Assert.Single(events.OfType<BlockCompletedEvent>()).Text);
    }

    /// <summary>
    /// Ordinary angle brackets and trailing whitespace pass through unchanged when no suggestion tag is present.
    /// </summary>
    [Fact]
    public void KeepsOrdinaryAngleBracketsAndWhitespace()
    {
        var events = new List<AgentEvent>();
        var turn = new AntigravityTurn(AntigravityBackend.DefaultModel, events.Add);
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Use List<int> "}}""");
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"here.\n"}}""");
        turn.FinishBlock();

        Assert.Null(turn.Suggestion);
        Assert.Equal("Use List<int> here.\n", Assert.Single(events.OfType<BlockCompletedEvent>()).Text);
    }

    /// <summary>
    /// Maps <c>run_command</c> inputs (<c>CommandLine</c>, <c>toolSummary</c>) to the shared shell tool shape.
    /// </summary>
    [Fact]
    public void MapsRunCommandInputsToSharedShellShape()
    {
        var events = new List<AgentEvent>();
        var turn = new AntigravityTurn(AntigravityBackend.DefaultModel, events.Add);
        Send(turn, """
            {"sessionUpdate":"tool_call","toolCallId":"cmd-1","name":"run_command","kind":"execute","status":"in_progress",
             "title":"node -e \"console.log(42)\"","rawInput":{"CommandLine":"node -e \"console.log(42)\"","toolSummary":"Quick JS check"}}
            """);

        var call = Assert.Single(events.OfType<ToolCallEvent>());
        Assert.True(call.IsShellCommand);
        Assert.Equal(OperatingSystem.IsWindows() ? "PowerShell" : "Bash", call.Name);
        Assert.Equal("node -e \"console.log(42)\"", call.Input.GetProperty("command").GetString());
        Assert.Equal("Quick JS check", call.Input.GetProperty("description").GetString());
    }

    /// <summary>
    /// Commands and non-daemon timers still open at <c>end_turn</c> promote to awaited work, complete on their late
    /// <c>tool_call_update</c> without opening a reply, and keep their wake-up reply from closing pending subagents.
    /// </summary>
    [Fact]
    public async Task PromotesOpenCommandsAndTimersAcrossTurns()
    {
        var events = new List<AgentEvent>();
        using var work = new AntigravityBackgroundWork(e => { lock (events) events.Add(e); }, () => AntigravityBackend.DefaultModel);

        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"sub-1","name":"invoke_subagent","status":"in_progress","title":"Run invoke_subagent"}
            """);
        Track(work, """
            {"sessionUpdate":"tool_call_update","toolCallId":"sub-1","name":"invoke_subagent","status":"completed"}
            """);
        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"cmd-bg","name":"run_command","kind":"execute","status":"in_progress",
             "title":"dotnet build","rawInput":{"CommandLine":"dotnet build","toolSummary":"Build .NET app"}}
            """);
        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"cmd-daemon","name":"run_command","kind":"execute","status":"in_progress",
             "title":"npm run dev","rawInput":{"CommandLine":"npm run dev","toolSummary":"Dev server","IsDaemon":true}}
            """);
        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"timer-1","name":"schedule","kind":"other","status":"in_progress",
             "title":"Run schedule","rawInput":{"DurationSeconds":6,"Prompt":"check builds"}}
            """);

        work.PromoteOpenWork();

        lock (events)
        {
            var started = events.OfType<TaskStartedEvent>().ToList();
            Assert.Collection(started,
                cmd =>
                {
                    Assert.Equal("cmd-bg", cmd.ToolUseId);
                    Assert.Equal("local_bash", cmd.TaskType);
                    Assert.Equal("Build .NET app", cmd.Description);
                },
                timer =>
                {
                    Assert.Equal("timer-1", timer.ToolUseId);
                    Assert.Equal("timer", timer.TaskType);
                    Assert.Equal("6s timer: check builds", timer.Description);
                });
        }

        // A lone tool_call_update completing the timer emits ToolResultEvent without starting an unprompted reply.
        Feed(work, """{"sessionUpdate":"tool_call_update","toolCallId":"timer-1","name":"schedule","status":"completed"}""");
        Assert.False(work.IsReplying);
        lock (events)
        {
            Assert.Single(events.OfType<ToolResultEvent>(), r => r.ToolUseId == "timer-1" && !r.IsError);
        }

        // The timer's wake-up reply does not prematurely complete sub-1.
        await ReplyAsync(work, "Checking builds.");
        lock (events) Assert.Empty(events.OfType<SubagentStateEvent>());

        // The background command completes, and its wake-up reply also doesn't close sub-1.
        Feed(work, """{"sessionUpdate":"tool_call_update","toolCallId":"cmd-bg","name":"run_command","status":"completed","rawOutput":{"output":"ok"}}""");
        await ReplyAsync(work, "Build finished.");
        lock (events) Assert.Empty(events.OfType<SubagentStateEvent>());

        // Now sub-1's own unprompted reply closes sub-1.
        await ReplyAsync(work, "Subagent finished.");
        lock (events)
        {
            var sub = Assert.Single(events.OfType<SubagentStateEvent>());
            Assert.Equal("sub-1", sub.AgentId);
            Assert.Equal("completed", sub.Status);
        }
    }

    /// <summary>
    /// When a subagent finishes inside the same <c>session/prompt</c> (before <c>end_turn</c>), the in-turn wake-up closes it
    /// without leaving a stuck "Waiting for ..." banner after the prompt returns.
    /// </summary>
    [Fact]
    public void EndsSubagentWhenParentWakesInsideActivePrompt()
    {
        var events = new List<AgentEvent>();
        using var work = new AntigravityBackgroundWork(e => { lock (events) events.Add(e); }, () => AntigravityBackend.DefaultModel);

        work.BeginTurn();
        Track(work, """{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Starting both."}}""", inTurn: true);
        Track(work, """{"sessionUpdate":"usage_update","used":100,"size":1000}""", inTurn: true);
        Track(work, """{"sessionUpdate":"tool_call","toolCallId":"cmd-1","name":"run_command","kind":"execute","status":"in_progress","rawInput":{"CommandLine":"sleep 8","toolSummary":"Startup checks"}}""", inTurn: true);
        Track(work, """{"sessionUpdate":"tool_call","toolCallId":"sub-1","name":"invoke_subagent","status":"in_progress"}""", inTurn: true);
        Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"sub-1","status":"completed"}""", inTurn: true);
        Track(work, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Started both, waiting."}}""", inTurn: true);
        Track(work, """{"sessionUpdate":"usage_update","used":200,"size":1000}""", inTurn: true);

        // Background command completes while the prompt is waiting, then the parent wakes twice (once for subagent, once for command).
        Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"cmd-1","name":"run_command","status":"completed"}""", inTurn: true);
        Track(work, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Subagent replied."}}""", inTurn: true);
        Track(work, """{"sessionUpdate":"usage_update","used":300,"size":1000}""", inTurn: true);
        lock (events) Assert.Empty(events.OfType<SubagentStateEvent>());

        Track(work, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Command finished."}}""", inTurn: true);
        Track(work, """{"sessionUpdate":"usage_update","used":400,"size":1000}""", inTurn: true);
        work.EndTurn(promote: true);

        lock (events)
        {
            Assert.Empty(events.OfType<TaskStartedEvent>());
            var sub = Assert.Single(events.OfType<SubagentStateEvent>());
            Assert.Equal("sub-1", sub.AgentId);
            Assert.Equal("completed", sub.Status);
        }
    }

    /// <summary>
    /// Explicit <c>manage_subagents</c> kills or empty list results immediately clear tracked subagents.
    /// </summary>
    [Fact]
    public void ClearsSubagentsOnManageSubagentsKillAndEmptyList()
    {
        var events = new List<AgentEvent>();
        using var work = new AntigravityBackgroundWork(e => { lock (events) events.Add(e); }, () => AntigravityBackend.DefaultModel);

        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"sub-1","name":"invoke_subagent","status":"completed",
             "rawOutput":{"conversationId":"11111111-1111-1111-1111-111111111111"}}
            """);
        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"sub-2","name":"invoke_subagent","status":"completed",
             "rawOutput":{"conversationId":"22222222-2222-2222-2222-222222222222"}}
            """);

        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"kill-1","name":"manage_subagents","status":"in_progress",
             "rawInput":{"Action":"kill","ConversationIds":["11111111-1111-1111-1111-111111111111"]}}
            """);
        Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"kill-1","status":"completed"}""");

        lock (events)
        {
            var killed = Assert.Single(events.OfType<SubagentStateEvent>());
            Assert.Equal("sub-1", killed.AgentId);
            Assert.Equal("stopped", killed.Status);
        }

        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"list-1","name":"manage_subagents","status":"in_progress",
             "rawInput":{"Action":"list"}}
            """);
        Track(work, """
            {"sessionUpdate":"tool_call_update","toolCallId":"list-1","status":"completed",
             "rawOutput":{"subagents":[]}}
            """);

        lock (events)
        {
            Assert.Collection(events.OfType<SubagentStateEvent>(),
                first => Assert.Equal(("sub-1", "stopped"), (first.AgentId, first.Status)),
                second => Assert.Equal(("sub-2", "completed"), (second.AgentId, second.Status)));
        }
    }

    /// <summary>
    /// Terminal tool statuses beyond <c>completed</c>/<c>failed</c> (such as <c>cancelled</c>) and <c>manage_task</c> kills clear awaited commands.
    /// </summary>
    [Fact]
    public void ClearsAwaitedCommandsOnCancelledStatusOrManageTaskKill()
    {
        var events = new List<AgentEvent>();
        using var work = new AntigravityBackgroundWork(e => { lock (events) events.Add(e); }, () => AntigravityBackend.DefaultModel);

        work.BeginTurn();
        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"cmd-1","name":"run_command","kind":"execute","status":"in_progress",
             "rawInput":{"CommandLine":"git status","toolSummary":"Startup checks"}}
            """, inTurn: true);
        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"cmd-2","name":"run_command","kind":"execute","status":"in_progress",
             "rawInput":{"CommandLine":"dotnet build","toolSummary":"Build"}}
            """, inTurn: true);
        work.EndTurn(promote: true);

        Feed(work, """{"sessionUpdate":"tool_call_update","toolCallId":"cmd-1","name":"run_command","status":"cancelled"}""");
        lock (events)
        {
            var result = Assert.Single(events.OfType<ToolResultEvent>());
            Assert.Equal("cmd-1", result.ToolUseId);
            Assert.True(result.IsError);
        }

        Track(work, """
            {"sessionUpdate":"tool_call","toolCallId":"kill-task","name":"manage_task","status":"in_progress",
             "rawInput":{"Action":"kill","TaskId":"task-2"}}
            """);
        Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"kill-task","status":"completed"}""");

        lock (events)
        {
            var stopped = Assert.Single(events.OfType<TaskNotificationEvent>());
            Assert.Equal("cmd-2", stopped.ToolUseId);
            Assert.Equal("stopped", stopped.Status);
        }
    }

    /// <summary>
    /// Multiple background tools finishing out-of-turn before their wake-up replies must each be accounted for so a
    /// running subagent is not closed early (pure-ACP fallback without a transcript file).
    /// </summary>
    [Fact]
    public async Task CountsMultiplePostTurnToolWakeupsSeparatelyFromSubagents()
    {
        var events = new List<AgentEvent>();
        using var work = new AntigravityBackgroundWork(e => { lock (events) events.Add(e); }, () => AntigravityBackend.DefaultModel);

        work.BeginTurn();
        Track(work, """{"sessionUpdate":"tool_call","toolCallId":"sub-1","name":"invoke_subagent","status":"in_progress"}""", inTurn: true);
        Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"sub-1","name":"invoke_subagent","status":"completed"}""", inTurn: true);
        Track(work, """{"sessionUpdate":"tool_call","toolCallId":"timer-1","name":"schedule","status":"in_progress","rawInput":{"DurationSeconds":8,"Prompt":"check"}}""", inTurn: true);
        Track(work, """{"sessionUpdate":"tool_call","toolCallId":"cmd-1","name":"run_command","kind":"execute","status":"in_progress","rawInput":{"CommandLine":"sleep 10","toolSummary":"Long check"}}""", inTurn: true);
        work.EndTurn(promote: true);

        // Both tools complete before either unprompted wake-up reply streams.
        Feed(work, """{"sessionUpdate":"tool_call_update","toolCallId":"timer-1","name":"schedule","status":"completed"}""");
        Feed(work, """{"sessionUpdate":"tool_call_update","toolCallId":"cmd-1","name":"run_command","status":"completed"}""");

        await ReplyAsync(work, "Timer fired.");
        lock (events) Assert.Empty(events.OfType<SubagentStateEvent>());

        await ReplyAsync(work, "Command finished.");
        lock (events) Assert.Empty(events.OfType<SubagentStateEvent>());

        await ReplyAsync(work, "Subagent finished.");
        lock (events)
        {
            var sub = Assert.Single(events.OfType<SubagentStateEvent>());
            Assert.Equal("sub-1", sub.AgentId);
            Assert.Equal("completed", sub.Status);
        }
    }

    /// <summary>
    /// When ACP omits <c>rawOutput</c> on <c>invoke_subagent</c> and <c>manage_subagents</c>, reading Antigravity's live
    /// <c>transcript.jsonl</c> binds exact conversation IDs, prevents command wake-ups from prematurely closing subagents,
    /// clears each subagent on its <c>SYSTEM_MESSAGE</c> or idle/empty <c>manage_subagents(list)</c>, and resumes on <c>send_message</c>.
    /// </summary>
    [Fact]
    public void ReconcilesSubagentsFromLiveTranscriptWhenAcpOmitsOutputs()
    {
        var tempDir = Directory.CreateTempSubdirectory("nomad-agy-test-");
        try
        {
            var transcriptPath = Path.Combine(tempDir.FullName, "transcript.jsonl");
            var events = new List<AgentEvent>();
            using var work = new AntigravityBackgroundWork(e => { lock (events) events.Add(e); }, () => AntigravityBackend.DefaultModel);
            work.AttachSession("a79021aa-2a69-4c8b-a2ef-5218be5224fd", transcriptPath);

            work.BeginTurn();
            Track(work, """{"sessionUpdate":"tool_call","toolCallId":"cmd-1","name":"run_command","kind":"execute","status":"in_progress","rawInput":{"CommandLine":"sleep 6","toolSummary":"Slow startup check"}}""", inTurn: true);
            Track(work, """{"sessionUpdate":"tool_call","toolCallId":"sub-1","name":"invoke_subagent","status":"in_progress","rawInput":{"Role":"Independent Code Reviewer","TypeName":"research"}}""", inTurn: true);
            Track(work, """{"sessionUpdate":"tool_call","toolCallId":"sub-2","name":"invoke_subagent","status":"in_progress","rawInput":{"Role":"Doc Checker","TypeName":"research"}}""", inTurn: true);

            // Real agy acp omits rawOutput and content on invoke_subagent tool_call_update.
            Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"sub-1","name":"invoke_subagent","status":"completed"}""", inTurn: true);
            Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"sub-2","name":"invoke_subagent","status":"completed"}""", inTurn: true);

            File.AppendAllLines(transcriptPath,
            [
                """{"step_index":2,"source":"MODEL","type":"GENERIC","status":"RUNNING","content":"Tool is running as a background task with task id: a79021aa-2a69-4c8b-a2ef-5218be5224fd/task-2\nTask Description: sleep 6"}""",
                """{"step_index":3,"source":"MODEL","type":"GENERIC","status":"DONE","content":"Created the following subagent:\n{\n  \"conversationId\":  \"11111111-1111-1111-1111-111111111111\"\n}"}""",
                """{"step_index":4,"source":"MODEL","type":"GENERIC","status":"DONE","content":"Created the following subagent:\n{\n  \"conversationId\":  \"22222222-2222-2222-2222-222222222222\"\n}"}""",
            ]);

            // Command completes before usage_update, then wakes the parent in-turn (live task-48 sequence).
            Track(work, """{"sessionUpdate":"tool_call_update","toolCallId":"cmd-1","name":"run_command","status":"completed"}""", inTurn: true);
            Track(work, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Launched both"}}""", inTurn: true);
            Track(work, """{"sessionUpdate":"usage_update","used":100,"size":1000}""", inTurn: true);

            File.AppendAllLines(transcriptPath,
            [
                """{"step_index":5,"source":"SYSTEM","type":"SYSTEM_MESSAGE","status":"DONE","content":"<SYSTEM_MESSAGE>\n[Message] timestamp=2026-11-22T18:43:12Z sender=a79021aa-2a69-4c8b-a2ef-5218be5224fd/task-2 priority=MESSAGE_PRIORITY_HIGH content=Task id \"a79021aa-2a69-4c8b-a2ef-5218be5224fd/task-2\" finished with result:\nCommand exited with code 0.\n</SYSTEM_MESSAGE>"}""",
            ]);

            Track(work, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Command done, still waiting for subagents."}}""", inTurn: true);
            Track(work, """{"sessionUpdate":"usage_update","used":200,"size":1000}""", inTurn: true);
            work.EndTurn(promote: true);

            // Neither subagent was falsely closed by the command wake-up.
            lock (events) Assert.Empty(events.OfType<SubagentStateEvent>());

            // Subagent 1 sends its report in transcript.jsonl -> closed immediately with its report.
            File.AppendAllLines(transcriptPath,
            [
                """{"step_index":7,"source":"SYSTEM","type":"SYSTEM_MESSAGE","status":"DONE","content":"<SYSTEM_MESSAGE>\n[Message] timestamp=2026-11-22T18:43:34Z sender=11111111-1111-1111-1111-111111111111 priority=MESSAGE_PRIORITY_HIGH content=Review complete: looks good.\n</SYSTEM_MESSAGE>"}""",
            ]);
            work.SyncTranscript();

            lock (events)
            {
                var done = Assert.Single(events.OfType<SubagentStateEvent>());
                Assert.Equal("sub-1", done.AgentId);
                Assert.Equal("completed", done.Status);
                Assert.Equal("Review complete: looks good.", done.Message);
            }

            // Sending a follow-up message resumes sub-1, then manage_subagents(list) showing both idle clears both.
            Track(work, """{"sessionUpdate":"tool_call","toolCallId":"msg-1","name":"send_message","status":"completed","rawInput":{"Recipient":"11111111-1111-1111-1111-111111111111","Message":"Check tests too"}}""");
            lock (events)
            {
                Assert.Contains(events.OfType<SubagentStateEvent>(), s => s.AgentId == "sub-1" && s.Status == "running");
            }

            File.AppendAllLines(transcriptPath,
            [
                """{"step_index":9,"source":"MODEL","type":"GENERIC","status":"DONE","content":"You have 2 active subagent(s):\n[{\"role\":\"Independent Code Reviewer\",\"conversationId\":\"11111111-1111-1111-1111-111111111111\",\"state\":\"idle\"},{\"role\":\"Doc Checker\",\"conversationId\":\"22222222-2222-2222-2222-222222222222\",\"state\":\"idle\"}]"}""",
            ]);
            Track(work, """{"sessionUpdate":"tool_call","toolCallId":"list-1","name":"manage_subagents","status":"completed","rawInput":{"Action":"list"}}""");

            lock (events)
            {
                var completed = events.OfType<SubagentStateEvent>().Where(s => s.Status == "completed").ToList();
                Assert.Contains(completed, s => s.AgentId == "sub-1");
                Assert.Contains(completed, s => s.AgentId == "sub-2");
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Live transcript completion messages and empty <c>manage_task(list)</c> outputs clear background commands even when
    /// ACP drops their <c>tool_call_update</c>, while resumed sessions ignore historical lines written before attach.
    /// </summary>
    [Fact]
    public void ClearsBackgroundCommandsFromTranscriptAndSkipsHistoricalLinesOnResume()
    {
        var tempDir = Directory.CreateTempSubdirectory("nomad-agy-resume-");
        try
        {
            var transcriptPath = Path.Combine(tempDir.FullName, "transcript.jsonl");
            // Pre-existing historical line before attach should be skipped.
            File.WriteAllLines(transcriptPath,
            [
                """{"step_index":0,"source":"SYSTEM","type":"SYSTEM_MESSAGE","status":"DONE","content":"<SYSTEM_MESSAGE>\n[Message] timestamp=2026-11-22T17:00:00Z sender=system priority=MESSAGE_PRIORITY_LOW content=[Notice] All your subagents and background tasks have been stopped due to server restart.\n</SYSTEM_MESSAGE>"}""",
            ]);

            var events = new List<AgentEvent>();
            using var work = new AntigravityBackgroundWork(e => { lock (events) events.Add(e); }, () => AntigravityBackend.DefaultModel);
            work.AttachSession("fed92dce-9d36-447c-980c-0bbd84e41646", transcriptPath);

            work.BeginTurn();
            Track(work, """{"sessionUpdate":"tool_call","toolCallId":"cmd-startup","name":"run_command","kind":"execute","status":"in_progress","rawInput":{"CommandLine":"git status","toolSummary":"Session startup ledger and git checks"}}""", inTurn: true);
            Track(work, """{"sessionUpdate":"tool_call","toolCallId":"cmd-build","name":"run_command","kind":"execute","status":"in_progress","rawInput":{"CommandLine":"dotnet build","toolSummary":"Build check"}}""", inTurn: true);
            File.AppendAllLines(transcriptPath,
            [
                """{"step_index":2,"source":"MODEL","type":"GENERIC","status":"RUNNING","content":"Tool is running as a background task with task id: fed92dce-9d36-447c-980c-0bbd84e41646/task-2\nTask Description: git status"}""",
                """{"step_index":3,"source":"MODEL","type":"GENERIC","status":"RUNNING","content":"Tool is running as a background task with task id: fed92dce-9d36-447c-980c-0bbd84e41646/task-3\nTask Description: dotnet build"}""",
            ]);
            work.EndTurn(promote: true);

            // Historical restart notice was not replayed, so both commands promoted cleanly.
            lock (events) Assert.Equal(2, events.OfType<TaskStartedEvent>().Count());

            // Command 1 finishes in transcript.jsonl even though ACP never sent tool_call_update.
            File.AppendAllLines(transcriptPath,
            [
                """{"step_index":5,"source":"SYSTEM","type":"SYSTEM_MESSAGE","status":"DONE","content":"<SYSTEM_MESSAGE>\n[Message] timestamp=2026-11-22T18:46:01Z sender=fed92dce-9d36-447c-980c-0bbd84e41646/task-2 priority=MESSAGE_PRIORITY_HIGH content=Task id \"fed92dce-9d36-447c-980c-0bbd84e41646/task-2\" finished with result:\nCommand exited with code 0.\n</SYSTEM_MESSAGE>"}""",
            ]);
            work.SyncTranscript();

            lock (events)
            {
                var ended = Assert.Single(events.OfType<ForegroundTaskEndedEvent>());
                Assert.Equal("cmd-startup", ended.ToolUseId);
                Assert.Equal("completed", ended.Status);
            }

            // manage_task(list) reports no running tasks -> clears cmd-build.
            File.AppendAllLines(transcriptPath,
            [
                """{"step_index":6,"source":"MODEL","type":"GENERIC","status":"DONE","content":"No background tasks are currently running."}""",
            ]);
            Track(work, """{"sessionUpdate":"tool_call","toolCallId":"list-tasks","name":"manage_task","status":"completed","rawInput":{"Action":"list"}}""");

            lock (events)
            {
                Assert.Contains(events.OfType<ForegroundTaskEndedEvent>(), e => e.ToolUseId == "cmd-build" && e.Status == "completed");
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static void Track(AntigravityBackgroundWork work, string update, bool inTurn)
    {
        using var document = JsonDocument.Parse(update);
        work.Track(document.RootElement, inTurn);
    }

    private static void Send(AntigravityTurn turn, string update)
    {
        using var document = JsonDocument.Parse(update);
        turn.Update(document.RootElement);
    }
}
