using System.Text.Json;
using Nomad.Core.Agents;
using Nomad.Core.Hub;
using Nomad.Core.Protocol;

namespace Nomad.Core.Tests;

/// <summary>The relay's events and inputs survive the trip through JSON (and unset JSON elements stay unset).</summary>
public sealed class RelayCodecTests
{
    private static readonly JsonElement Input = JsonDocument.Parse("""{"command":"ls","n":[1,2]}""").RootElement.Clone();

    public static TheoryData<AgentEvent> Events() =>
    [
        new SessionStartedEvent("s1", "opus", @"C:\x", "default", ["Bash", "Read"], "2.1", [new McpServerInfo("docs", "connected")]),
        new TurnStartedEvent("hi"),
        new RemotePromptEvent("from B", "u1"),
        new TaskNotificationEvent("t", null, "completed", "done"),
        new WorkflowStartedEvent("t", "tu", "review", null, "export const meta = {}"),
        new WorkflowProgressEvent("t", null, null, 1200, 3, 5000),
        new TaskUpdatedEvent("t", "killed"),
        new CommandOutputEvent("Set model to opus"),
        new PromptSuggestionEvent("run the tests"),
        new AssistantMessageStartedEvent("m", "opus", null),
        new BlockStartedEvent("m", 0, AgentBlockKind.ToolUse, "tu", "Bash", null),
        new TextDeltaEvent("m", 1, "Hel", "p"),
        new ThinkingDeltaEvent("m", 0, "hmm", null),
        new BlockCompletedEvent("m", 1, AgentBlockKind.Text, "Hello", null),
        new ToolCallEvent("tu", "Bash", Input, null),
        new ToolResultEvent("tu", false, "ok", default, null) { Images = ["data:image/png;base64,AA=="] },
        new ToolResultEvent("tu", true, "no", Input, null),
        new PermissionRequestedEvent(new PermissionRequest { RequestId = "r", ToolName = "Bash", Input = Input, SuppressAllowForSession = true }),
        new PermissionCancelledEvent("r"),
        new FolderMoveApprovedEvent(@"R:\y", "tu"),
        new PermissionModeChangedEvent("plan"),
        new ModelChangedEvent("haiku"),
        new StatusEvent(null),
        new TurnCompletedEvent(new TurnResult { DurationMs = 5, InputTokens = 10, OutputTokens = 2, ContextTokens = 9, Interrupted = true }),
        new AgentErrorEvent("bad"),
        new ProcessExitedEvent(1, false, "tail"),
        new RelayAnsweredEvent("r", "PC-B"),
    ];

    [Theory]
    [MemberData(nameof(Events))]
    public void EveryRelayedEventRoundTrips(AgentEvent e)
    {
        var json = RelayCodec.Encode(e);
        Assert.NotNull(json);
        var back = RelayCodec.Decode(json);
        Assert.IsType(e.GetType(), back);
        Assert.Equal(json, RelayCodec.Encode(back!)); // JSON elements compare by document, so compare the JSON.
    }

    [Fact]
    public void UnsetElementsStayUnsetAndSetOnesKeepTheirValue()
    {
        var back = (ToolResultEvent)RelayCodec.Decode(RelayCodec.Encode(new ToolResultEvent("tu", false, "ok", default, null))!)!;
        Assert.Equal(JsonValueKind.Undefined, back.Structured.ValueKind);
        var call = (ToolCallEvent)RelayCodec.Decode(RelayCodec.Encode(new ToolCallEvent("tu", "Bash", Input, null))!)!;
        Assert.Equal("ls", call.Input.GetProperty("command").GetString());
    }

    [Fact]
    public void HostOnlyEventsAreNotRelayedAndJunkDecodesToNothing()
    {
        Assert.False(RelayCodec.IsRelayed(new RemoteControlStateEvent("ready")));
        Assert.Null(RelayCodec.Encode(new WorkflowSettingsEvent(true, false)));
        Assert.Null(RelayCodec.Decode("""{"t":"NoSuchEvent","e":{}}"""));
        Assert.Null(RelayCodec.Decode("not json"));
    }

    [Fact]
    public void InputsRoundTrip()
    {
        var answer = new RelayInput(RelayInput.Answer,
            Request: new PermissionRequest { RequestId = "r", ToolName = "AskUserQuestion", Input = Input },
            Decision: new PermissionDecision(PermissionChoice.Allow, null, Input));
        var back = RelayCodec.DecodeInput(RelayCodec.EncodeInput(answer))!;
        Assert.Equal(("r", PermissionChoice.Allow), (back.Request!.RequestId, back.Decision!.Choice));
        Assert.Equal("ls", back.Decision.UpdatedInput!.Value.GetProperty("command").GetString());

        var prompt = RelayCodec.DecodeInput(RelayCodec.EncodeInput(new RelayInput(RelayInput.Prompt, "hi", "u", [new PromptImage([1, 2, 3], "image/png")])))!;
        Assert.Equal(("hi", "u"), (prompt.Text, prompt.Uuid));
        Assert.Equal([1, 2, 3], prompt.Images![0].Data);
        Assert.Null(RelayCodec.DecodeInput(RelayCodec.EncodeInput(new RelayInput(RelayInput.Interrupt)))!.Decision);
    }
}
