using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nomad.Core.Agents;

/// <summary>
/// The SDK-hosted MCP server Nomad gives Claude ("nomad_session"): it speaks the protocol and hands each call to its tool, a folder
/// move (which Nomad approves, even in bypass mode) or a suggested new chat.
/// </summary>
internal sealed class NomadSessionServer(string workingDirectory, string? chatKey = null, bool suggestNewChats = true)
{
    /// <summary>
    /// The server's name, as the CLI's mcp_message control requests carry it.
    /// </summary>
    public const string ServerName = "nomad_session";

    /// <summary>
    /// Appended to Claude's system prompt: when to use each tool (the new-chat one only where it is offered).
    /// </summary>
    public static string InstructionsFor(bool suggestNewChats) => suggestNewChats
        ? FolderMoveTool.Instructions + "\n\n" + NewChatTool.Instructions + "\n\n" + ChatTools.Instructions
        : FolderMoveTool.Instructions + "\n\n" + ChatTools.Instructions;

    /// <summary>
    /// The folder-move tool, whose approval and cancellation the backend drives.
    /// </summary>
    public FolderMoveTool FolderMove { get; } = new(workingDirectory);

    /// <summary>
    /// Whether <paramref name="toolName"/> is one of this server's tools, as the CLI names it (mcp__nomad_session__…).
    /// </summary>
    public static bool Owns(string? toolName) =>
        toolName is FolderMoveTool.ToolName or NewChatTool.ToolName or ListChatsToolName or SendToChatToolName;

    /// <summary>
    /// The listing chat tool as Claude calls it.
    /// </summary>
    public const string ListChatsToolName = "mcp__nomad_session__" + ChatTools.ListName;

    /// <summary>
    /// The sending chat tool as Claude calls it.
    /// </summary>
    public const string SendToChatToolName = "mcp__nomad_session__" + ChatTools.SendName;

    /// <summary>
    /// Answers one JSON-RPC message from the CLI; null for a notification, which needs no response.
    /// </summary>
    public async Task<JsonObject?> HandleAsync(JsonElement message, bool mainAgent = true)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;

        var method = Str(message, "method");
        if (method == "notifications/cancelled") CancelCall(message);

        if (!message.TryGetProperty("id", out var id)) return null;

        var result = method switch
        {
            "initialize" => Initialize(message),
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = new JsonArray(Descriptors()) },
            "tools/call" => await CallAsync(message, mainAgent).ConfigureAwait(false),
            _ => null,
        };
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        if (result is null)
        {
            response["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Unknown MCP method." };
            return response;
        }

        response["result"] = result;
        return response;
    }

    /// <summary>
    /// The CLI gave up on a call (the turn was interrupted): a folder move waiting for the user is withdrawn.
    /// </summary>
    private void CancelCall(JsonElement message)
    {
        if (message.TryGetProperty("params", out var cancellation) && cancellation.TryGetProperty("requestId", out var cancelledId))
        {
            FolderMove.CancelIfPending(cancelledId.GetRawText());
        }
    }

    /// <summary>
    /// The initialize answer: this server, offering tools, on the protocol version the CLI asked for.
    /// </summary>
    private static JsonObject Initialize(JsonElement message)
    {
        var version = message.TryGetProperty("params", out var init) ? Str(init, "protocolVersion") : null;
        return new JsonObject
        {
            ["protocolVersion"] = version ?? "2024-11-05",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
        };
    }

    /// <summary>
    /// Every tool this server offers, as tools/list describes them.
    /// </summary>
    private JsonNode[] Descriptors() => suggestNewChats
        ? [FolderMoveTool.Descriptor(), NewChatTool.Descriptor(), .. ChatTools.Descriptors()]
        : [FolderMoveTool.Descriptor(), .. ChatTools.Descriptors()];

    /// <summary>
    /// A tools/call, handed to the tool it names; subagents may only list chats.
    /// </summary>
    private async Task<JsonObject> CallAsync(JsonElement message, bool mainAgent)
    {
        var name = message.TryGetProperty("params", out var parameters) ? Str(parameters, "name") : null;
        return name switch
        {
            FolderMoveTool.Name when !mainAgent =>
                ToolResult("Only the main conversation can request a folder move. Return this request to your parent agent.", error: true),
            FolderMoveTool.Name => await FolderMove.CallAsync(message).ConfigureAwait(false),
            NewChatTool.Name when !suggestNewChats =>
                ToolResult("Suggesting new chats is turned off here. Carry on in this conversation.", error: true),
            NewChatTool.Name when !mainAgent =>
                ToolResult("Only the main conversation can suggest a new chat. Mention the task to your parent agent instead.", error: true),
            NewChatTool.Name => NewChatTool.Call(parameters),
            ChatTools.SendName when !mainAgent =>
                ToolResult("Only the main conversation can message another chat. Return the message to your parent agent.", error: true),
            ChatTools.ListName or ChatTools.SendName => await ChatTools.CallAsync(name, parameters, chatKey).ConfigureAwait(false),
            _ => ToolResult($"Unknown tool: {name}.", error: true),
        };
    }

    /// <summary>
    /// A tools/call result carrying one text block.
    /// </summary>
    internal static JsonObject ToolResult(string text, bool error = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    };

    /// <summary>
    /// A string property, trimmed; null when it is missing or not a string.
    /// </summary>
    internal static string? Str(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() : null;

    /// <summary>
    /// The Claude tool_use id the CLI attaches to a tools/call, so the call can be matched to the transcript.
    /// </summary>
    internal static string? ToolUseId(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("params", out var parameters) &&
        parameters.TryGetProperty("_meta", out var meta) ? Str(meta, "claudecode/toolUseId") : null;
}
