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
    /// A tool call closes the preceding streamed text before the next block starts.
    /// </summary>
    [Fact]
    public void StreamsTextAndToolsInTranscriptOrder()
    {
        var events = new List<AgentEvent>();
        var turn = new AntigravityTurn(AntigravityBackend.DefaultModel, events.Add);
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Checking "}}""");
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"the file."}}""");
        Send(turn, """{"sessionUpdate":"tool_call","toolCallId":"read-1","title":"Read file","status":"in_progress"}""");
        Send(turn, """{"sessionUpdate":"tool_call_update","toolCallId":"read-1","status":"completed","rawOutput":"contents"}""");
        Send(turn, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Done."}}""");
        turn.FinishBlock();

        var contents = events.Where(e => e is BlockCompletedEvent or ToolCallEvent or ToolResultEvent).ToArray();
        Assert.Collection(contents,
            e => Assert.Equal("Checking the file.", Assert.IsType<BlockCompletedEvent>(e).Text),
            e => Assert.Equal("read-1", Assert.IsType<ToolCallEvent>(e).ToolUseId),
            e => Assert.Equal("contents", Assert.IsType<ToolResultEvent>(e).Text),
            e => Assert.Equal("Done.", Assert.IsType<BlockCompletedEvent>(e).Text));
        Assert.Equal(AntigravityBackend.DefaultModel, Assert.Single(events.OfType<AssistantMessageStartedEvent>()).Model);
    }

    /// <summary>
    /// Passes a scoped document to the translator to catch retained JSON references.
    /// </summary>
    private static void Send(AntigravityTurn turn, string update)
    {
        using var document = JsonDocument.Parse(update);
        turn.Update(document.RootElement);
    }
}
