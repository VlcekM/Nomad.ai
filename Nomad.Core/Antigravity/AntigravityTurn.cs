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
    /// Tag delimiters for optional follow-up prompt suggestions appended at the end of a turn.
    /// </summary>
    internal const string SuggestionOpen = "<nomad-suggestion>";
    internal const string SuggestionClose = "</nomad-suggestion>";

    /// <summary>
    /// Text held back while checking for a trailing <c>&lt;nomad-suggestion&gt;</c> tag so it never flickers in the chat.
    /// </summary>
    private readonly StringBuilder _pending = new();

    /// <summary>
    /// Whether the stream is currently inside a suggestion tag.
    /// </summary>
    private bool _inSuggestion;

    /// <summary>
    /// Whether a suggestion tag was stripped in the current text block (so trailing whitespace after it is dropped).
    /// </summary>
    private bool _strippedSuggestion;

    /// <summary>
    /// The extracted follow-up prompt suggestion for this turn, if any.
    /// </summary>
    public string? Suggestion { get; private set; }

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
        FlushPendingText();
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

        if (kind == AgentBlockKind.Text)
        {
            AppendText(text);
            return;
        }

        EmitChunk(kind, text);
    }

    /// <summary>
    /// Streams visible assistant text while buffering and stripping any <c>&lt;nomad-suggestion&gt;</c> tag.
    /// </summary>
    private void AppendText(string text)
    {
        _pending.Append(text);
        while (_pending.Length > 0)
        {
            if (_inSuggestion)
            {
                var close = IndexOf(_pending, SuggestionClose, 0);
                if (close < 0) return;

                var suggestion = _pending.ToString(0, close).Trim();
                if (suggestion.Length > 0) Suggestion = suggestion;

                _pending.Remove(0, close + SuggestionClose.Length);
                _inSuggestion = false;
                _strippedSuggestion = true;
                continue;
            }

            var search = 0;
            var holdFrom = -1;
            while (search < _pending.Length)
            {
                var lt = IndexOfChar(_pending, '<', search);
                if (lt < 0) break;

                var remaining = _pending.Length - lt;
                if (remaining >= SuggestionOpen.Length && MatchesAt(_pending, lt, SuggestionOpen))
                {
                    var emitEnd = TrimTrailingWhitespaceEnd(_pending, lt);
                    if (emitEnd > 0) EmitChunk(AgentBlockKind.Text, _pending.ToString(0, emitEnd));

                    _pending.Remove(0, lt + SuggestionOpen.Length);
                    _inSuggestion = true;
                    holdFrom = -2;
                    break;
                }

                if (remaining < SuggestionOpen.Length && IsPrefixOf(_pending, lt, SuggestionOpen))
                {
                    holdFrom = TrimTrailingWhitespaceEnd(_pending, lt);
                    break;
                }

                search = lt + 1;
            }

            if (holdFrom == -2) continue;

            if (holdFrom < 0)
            {
                holdFrom = TrimTrailingWhitespaceEnd(_pending, _pending.Length);
            }

            if (holdFrom > 0)
            {
                EmitChunk(AgentBlockKind.Text, _pending.ToString(0, holdFrom));
                _pending.Remove(0, holdFrom);
                _strippedSuggestion = false;
            }

            return;
        }
    }

    /// <summary>
    /// Emits any buffered non-suggestion text when a text block ends.
    /// </summary>
    private void FlushPendingText()
    {
        if (!_inSuggestion && _pending.Length > 0)
        {
            var remaining = _pending.ToString();
            if (!_strippedSuggestion || !string.IsNullOrWhiteSpace(remaining))
            {
                EmitChunk(AgentBlockKind.Text, remaining);
            }
        }

        _pending.Clear();
        _inSuggestion = false;
        _strippedSuggestion = false;
    }

    /// <summary>
    /// Opens or continues a block and raises its delta event.
    /// </summary>
    private void EmitChunk(AgentBlockKind kind, string text)
    {
        if (_kind != kind)
        {
            if (_kind is not null)
            {
                raise(new BlockCompletedEvent(_message, _index++, _kind.Value, _text.ToString(), null));
                _text.Clear();
            }

            StartMessage();
            _kind = kind;
            raise(new BlockStartedEvent(_message, _index, kind, null, null, null));
        }

        _text.Append(text);
        raise(kind == AgentBlockKind.Text
            ? new TextDeltaEvent(_message, _index, text, null)
            : new ThinkingDeltaEvent(_message, _index, text, null));
    }

    private static int IndexOfChar(StringBuilder sb, char c, int start)
    {
        for (var i = start; i < sb.Length; i++)
        {
            if (sb[i] == c) return i;
        }

        return -1;
    }

    private static int IndexOf(StringBuilder sb, string value, int start)
    {
        var limit = sb.Length - value.Length;
        for (var i = start; i <= limit; i++)
        {
            if (MatchesAt(sb, i, value)) return i;
        }

        return -1;
    }

    private static bool MatchesAt(StringBuilder sb, int index, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (sb[index + i] != value[i]) return false;
        }

        return true;
    }

    private static bool IsPrefixOf(StringBuilder sb, int index, string value)
    {
        var count = sb.Length - index;
        for (var i = 0; i < count; i++)
        {
            if (sb[index + i] != value[i]) return false;
        }

        return true;
    }

    private static int TrimTrailingWhitespaceEnd(StringBuilder sb, int end)
    {
        while (end > 0 && char.IsWhiteSpace(sb[end - 1])) end--;
        return end;
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

        var name = title;
        var isSubagentLaunch = CodexJson.Str(update, "name") == "invoke_subagent";
        if (isSubagentLaunch)
        {
            name = "Task";
            input = SubagentInput(input);
        }

        raise(new BlockStartedEvent(_message, _index++, AgentBlockKind.ToolUse, id, name, null));
        raise(new ToolCallEvent(id, name, input.Clone(), null)
        {
            IsShellCommand = CodexJson.Str(update, "kind") == "execute",
            IsSubagentLaunch = isSubagentLaunch,
        });
    }

    /// <summary>
    /// Maps Antigravity's invocation fields to the shared subagent card.
    /// </summary>
    private static JsonElement SubagentInput(JsonElement input) => CodexJson.ToElement(new JsonObject
    {
        ["description"] = CodexJson.Str(input, "Role"),
        ["prompt"] = CodexJson.Str(input, "Prompt"),
        ["subagent_type"] = CodexJson.Str(input, "TypeName"),
        ["model"] = CodexJson.Str(input, "Model") is { } model && model != "inherit" ? model : null,
    });

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
