using System.Text.Json;
using System.Text.Json.Nodes;
using static Nomad.Core.Agents.NomadSessionServer;

namespace Nomad.Core.Agents;

/// <summary>
/// One chat another chat can message: its id, title, agent, folder and what it is doing.
/// </summary>
public sealed record ChatInfo(string Id, string Title, string Agent, string? Folder, string State);

/// <summary>
/// The chats open in Nomad, as the chat tools see them. The app implements it; <paramref name="callerKey"/> is the calling chat's
/// <see cref="AgentStartOptions.ChatKey"/>.
/// </summary>
public interface IChatDirectory
{
    /// <summary>
    /// The open chats other than the caller's.
    /// </summary>
    Task<IReadOnlyList<ChatInfo>> ListAsync(string? callerKey);

    /// <summary>
    /// Delivers a message; returns what to tell the sender, or throws <see cref="ChatMessageException"/>.
    /// </summary>
    Task<string> SendAsync(string? callerKey, string target, string message);
}

/// <summary>
/// A message that could not be delivered, with the reason for the sender.
/// </summary>
public sealed class ChatMessageException(string message) : Exception(message);

/// <summary>
/// The tools that let one chat list the others and send one a message: <c>list_chats</c> and <c>send_to_chat</c>. Claude Code
/// reaches them on the in-process nomad_session server; Antigravity through <see cref="ChatBridge"/>.
/// </summary>
public static class ChatTools
{
    /// <summary>
    /// The listing tool's name on the server.
    /// </summary>
    public const string ListName = "list_chats";
    /// <summary>
    /// The sending tool's name on the server.
    /// </summary>
    public const string SendName = "send_to_chat";

    /// <summary>
    /// The app's chats; null until the app sets it (the tools then answer that no chats are reachable).
    /// </summary>
    public static IChatDirectory? Directory { get; set; }

    /// <summary>
    /// Appended to Claude's system prompt.
    /// </summary>
    public const string Instructions = "Other chats open in Nomad (Claude Code or other agents) can be listed with " +
        "mcp__nomad_session__list_chats and messaged with mcp__nomad_session__send_to_chat. Use them when the user asks you to " +
        "hand something to, ask, or coordinate with another chat. A message you receive from another chat starts with " +
        "\"Message from chat\"; reply with send_to_chat only when a reply is asked for or clearly needed, never just to acknowledge.";

    /// <summary>
    /// Whether <paramref name="name"/> is one of the chat tools, as the server names them.
    /// </summary>
    public static bool Owns(string? name) => name is ListName or SendName;

    /// <summary>
    /// The tools as tools/list describes them.
    /// </summary>
    public static JsonNode[] Descriptors() =>
    [
        JsonNode.Parse("""
            {"name":"list_chats","description":"List the other chats open in Nomad that can be messaged with send_to_chat: id, title, agent, folder and state (working, idle, stopped).","inputSchema":{"type":"object","properties":{},"additionalProperties":false}}
            """)!,
        JsonNode.Parse("""
            {"name":"send_to_chat","description":"Send a message to another chat open in Nomad. It arrives there as a prompt from you (with your chat's id and title), right away when that chat is idle or after its current turn. Returns once delivered; it does not wait for an answer. Any reply comes back as a new prompt in this chat.","inputSchema":{"type":"object","properties":{"chat":{"type":"string","description":"The chat's id from list_chats, or its exact title."},"message":{"type":"string","description":"The message, self-contained: the other chat knows nothing of this conversation."}},"required":["chat","message"],"additionalProperties":false}}
            """)!,
    ];

    /// <summary>
    /// Runs one tool call for the chat with <paramref name="callerKey"/>.
    /// </summary>
    public static async Task<JsonObject> CallAsync(string? name, JsonElement parameters, string? callerKey)
    {
        if (Directory is not { } directory) return ToolResult("No other chats are reachable from here.", error: true);

        try
        {
            if (name == ListName)
            {
                var chats = await directory.ListAsync(callerKey).ConfigureAwait(false);
                var list = chats.Count == 0 ? "No other chats are open in Nomad." : string.Join("\n", chats.Select(Describe));
                return ToolResult(list);
            }

            var input = parameters.TryGetProperty("arguments", out var arguments) ? arguments : default;
            if (Str(input, "chat") is not { Length: > 0 } target || Str(input, "message") is not { Length: > 0 } message)
            {
                return ToolResult("Give the chat (an id from list_chats, or its title) and the message.", error: true);
            }

            return ToolResult(await directory.SendAsync(callerKey, target, message).ConfigureAwait(false));
        }
        catch (ChatMessageException ex)
        {
            return ToolResult(ex.Message, error: true);
        }
    }

    /// <summary>
    /// One line of the list: the chat's id, title, agent, state and folder.
    /// </summary>
    private static string Describe(ChatInfo chat)
    {
        var folder = chat.Folder is { Length: > 0 } path ? " in " + path : "";
        return $"- id {chat.Id}: \"{chat.Title}\" ({chat.Agent}, {chat.State}){folder}";
    }

    /// <summary>
    /// The prompt the receiving chat gets.
    /// </summary>
    public static string Envelope(ChatInfo? sender, string message)
    {
        if (sender is null) return $"Message from another chat, sent with Nomad's send_to_chat (it cannot be answered).\n\n{message}";

        return $"Message from chat \"{sender.Title}\" ({sender.Agent}, id {sender.Id}), sent with Nomad's send_to_chat. Reply " +
            $"with send_to_chat to that id only if a reply is asked for or needed, not to acknowledge.\n\n{message}";
    }

    /// <summary>
    /// A minimal MCP server over JSON-RPC messages, offering only the chat tools: what Antigravity gets through the bridge.
    /// </summary>
    public static async Task<JsonObject?> HandleAsync(JsonElement message, string? callerKey)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("id", out var id)) return null;

        var method = Str(message, "method");
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        JsonNode? result = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = Str(parameters, "protocolVersion") ?? "2024-11-05",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "nomad_chats", ["version"] = "1.0.0" },
            },
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = new JsonArray(Descriptors()) },
            "tools/call" => await CallAsync(Str(parameters, "name"), parameters, callerKey).ConfigureAwait(false),
            _ => null,
        };

        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        if (result is null)
        {
            response["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Unknown MCP method." };
        }
        else
        {
            response["result"] = result;
        }

        return response;
    }
}
