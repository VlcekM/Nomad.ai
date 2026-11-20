using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;
using Nomad.Core.Codex;

namespace Nomad.Core.Antigravity;

/// <summary>
/// Converts one ACP prompt's streaming updates into Nomad transcript events.
/// </summary>
internal sealed class AntigravityTurn(string? model, Action<AgentEvent> raise)
{
    /// <summary>
    /// The transcript message containing this turn's blocks.
    /// </summary>
    private readonly string _message = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Text accumulated for the current block.
    /// </summary>
    private readonly StringBuilder _text = new();

    /// <summary>
    /// The index of the current or next block.
    /// </summary>
    private int _index;

    /// <summary>
    /// The current streamed block type, when open.
    /// </summary>
    private AgentBlockKind? _kind;

    /// <summary>
    /// Whether the transcript message has been announced.
    /// </summary>
    private bool _started;

    /// <summary>
    /// Handles text, reasoning and tool call updates.
    /// </summary>
    public void Update(JsonElement update)
    {
        switch (CodexJson.Str(update, "sessionUpdate"))
        {
            case "agent_message_chunk":
                Append(update, AgentBlockKind.Text);
                break;
            case "agent_thought_chunk":
                Append(update, AgentBlockKind.Thinking);
                break;
            case "tool_call":
                StartTool(update);
                FinishTool(update);
                break;
            case "tool_call_update":
                FinishTool(update);
                break;
        }
    }

    /// <summary>
    /// Closes the current text or reasoning block.
    /// </summary>
    public void FinishBlock()
    {
        if (_kind is not { } kind) return;

        raise(new BlockCompletedEvent(_message, _index++, kind, _text.ToString(), null));
        _text.Clear();
        _kind = null;
    }

    /// <summary>
    /// Adds a text delta to the matching open block.
    /// </summary>
    private void Append(JsonElement update, AgentBlockKind kind)
    {
        var content = CodexJson.Obj(update, "content");
        if (CodexJson.Str(content, "text") is not { Length: > 0 } text) return;

        if (_kind != kind)
        {
            FinishBlock();
            StartMessage();
            _kind = kind;
            raise(new BlockStartedEvent(_message, _index, kind, null, null, null));
        }

        _text.Append(text);
        raise(kind == AgentBlockKind.Text
            ? new TextDeltaEvent(_message, _index, text, null)
            : new ThinkingDeltaEvent(_message, _index, text, null));
    }

    /// <summary>
    /// Creates the transcript entry for a tool call.
    /// </summary>
    private void StartTool(JsonElement update)
    {
        if (CodexJson.Str(update, "toolCallId") is not { } id) return;

        FinishBlock();
        StartMessage();
        var title = CodexJson.Str(update, "title") ?? "Antigravity action";
        var input = CodexJson.Any(update, "rawInput");
        if (input.ValueKind != JsonValueKind.Object) input = CodexJson.ToElement(new JsonObject { ["description"] = title });

        raise(new BlockStartedEvent(_message, _index++, AgentBlockKind.ToolUse, id, title, null));
        raise(new ToolCallEvent(id, title, input.Clone(), null));
    }

    /// <summary>
    /// Adds terminal tool output and diff content to its transcript entry.
    /// </summary>
    private void FinishTool(JsonElement update)
    {
        var status = CodexJson.Str(update, "status");
        if (status is not ("completed" or "failed") || CodexJson.Str(update, "toolCallId") is not { } id) return;

        var output = CodexJson.Any(update, "rawOutput");
        var content = CodexJson.Arr(update, "content").Select(ToolContent).Where(text => text.Length > 0);
        var text = string.Join("\n", content);
        if (text.Length == 0 && output.ValueKind != JsonValueKind.Undefined) text = output.ToString();

        raise(new ToolResultEvent(id, status == "failed", text, CodexJson.Keep(output), null));
    }

    /// <summary>
    /// Renders the visible content of an ACP tool result.
    /// </summary>
    private static string ToolContent(JsonElement content) => CodexJson.Str(content, "type") switch
    {
        "content" => CodexJson.Str(CodexJson.Obj(content, "content"), "text") ?? "",
        "diff" => $"File: {CodexJson.Str(content, "path")}\n{CodexJson.Str(content, "newText")}",
        _ => "",
    };

    /// <summary>
    /// Announces the assistant message before sending its first block.
    /// </summary>
    private void StartMessage()
    {
        if (_started) return;

        _started = true;
        raise(new AssistantMessageStartedEvent(_message, model, null));
    }
}
