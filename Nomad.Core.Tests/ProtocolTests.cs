using Nomad.Core.Agents;
using Nomad.Core.Protocol;

namespace Nomad.Core.Tests;

/// <summary>Stream-json parsing against transcripts captured from claude 2.1.145 (see Fixtures).</summary>
public class ProtocolTests
{
    private static string[] Fixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParsesEveryLineOfASimpleTurn()
    {
        var lines = Fixture("pong.jsonl").Select(CliProtocol.ParseLine).ToList();

        Assert.All(lines, Assert.NotNull);
        var init = Assert.Single(lines.OfType<SystemLine>(), l => l.Subtype == "init");
        Assert.Equal("claude-haiku-5-5", init.Model);
        Assert.False(string.IsNullOrEmpty(init.SessionId));

        var delta = lines.OfType<StreamEventLine>().Single(l => l.Event?.Delta?.Type == "text_delta");
        Assert.Equal("pong", delta.Event!.Delta!.Text);

        var assistant = Assert.Single(lines.OfType<AssistantLine>());
        Assert.Equal("pong", assistant.Message!.Content![0].Text);

        var result = Assert.Single(lines.OfType<ResultLine>());
        Assert.Equal("success", result.Subtype);
        Assert.True(result.Usage!.CacheCreationInputTokens > 0);

        Assert.Contains(lines, l => l is UnknownLine { Type: "rate_limit_event" });
        Assert.Contains(lines, l => l is ControlResponseLine { Response.Subtype: "success" });
    }

    [Fact]
    public void ReplayedPermissionTurnRaisesTheExpectedEvents()
    {
        var backend = new ClaudeCodeBackend();
        var events = new List<AgentEvent>();
        backend.EventReceived += events.Add;

        foreach (var line in Fixture("write-allow.jsonl"))
        {
            backend.HandleLine(line);
        }

        var permission = Assert.Single(events.OfType<PermissionRequestedEvent>()).Request;
        Assert.Equal("Write", permission.ToolName);
        Assert.Equal("hello", permission.Input.GetProperty("content").GetString());
        var sessionUpdate = Assert.Single(ClaudeCodeBackend.SessionPermissionUpdates(permission));
        Assert.Equal("session", sessionUpdate.GetProperty("destination").GetString());

        var call = Assert.Single(events.OfType<ToolCallEvent>());
        var result = Assert.Single(events.OfType<ToolResultEvent>());
        Assert.Equal(call.ToolUseId, result.ToolUseId);
        Assert.False(result.IsError);
        Assert.Equal("create", result.Structured.GetProperty("type").GetString());

        var done = Assert.Single(events.OfType<TurnCompletedEvent>()).Result;
        Assert.False(done.IsError);
        Assert.Equal(2, done.NumTurns);
        Assert.Contains(events, e => e is TextDeltaEvent);
    }

    [Fact]
    public void InterruptedTurnIsNotAnError()
    {
        var backend = new ClaudeCodeBackend();
        var events = new List<AgentEvent>();
        backend.EventReceived += events.Add;

        foreach (var line in Fixture("interrupt.jsonl"))
        {
            backend.HandleLine(line);
        }

        var results = events.OfType<TurnCompletedEvent>().Select(e => e.Result).ToList();
        Assert.Equal(2, results.Count);
        Assert.True(results[0].Interrupted);
        Assert.False(results[0].IsError);
        Assert.False(results[1].Interrupted);
    }

    [Fact]
    public void BuildsUserMessageAndPermissionLines()
    {
        using var user = System.Text.Json.JsonDocument.Parse(CliProtocol.UserMessage("hi \"there\" čau"));
        Assert.Equal("user", user.RootElement.GetProperty("type").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, user.RootElement.GetProperty("parent_tool_use_id").ValueKind);
        Assert.Equal("hi \"there\" čau", user.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());

        var deny = CliProtocol.PermissionResponse("r1", new PermissionResultPayload { Behavior = "deny", Message = "no" });
        Assert.Equal("""{"type":"control_response","response":{"subtype":"success","request_id":"r1","response":{"behavior":"deny","message":"no"}}}""", deny);
    }
}
