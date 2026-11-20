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

    private static void Send(AntigravityTurn turn, string update)
    {
        using var document = JsonDocument.Parse(update);
        turn.Update(document.RootElement);
    }
}
