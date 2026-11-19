using System.Text.Json;
using System.Text.Json.Nodes;
using static Nomad.Core.Agents.NomadSessionServer;

namespace Nomad.Core.Agents;

/// <summary>
/// The nomad_session tool that suggests a side task, or a request for a different task, for a chat of its own. It answers at
/// once: the transcript shows the call as a card, which the user may start as a new chat in the same folder, now or later.
/// </summary>
internal static class NewChatTool
{
    /// <summary>
    /// The tool's name on the server.
    /// </summary>
    public const string Name = "suggest_new_chat";

    /// <summary>
    /// The tool's name as Claude calls it.
    /// </summary>
    public const string ToolName = "mcp__nomad_session__suggest_new_chat";

    /// <summary>
    /// When to use it, appended to Claude's system prompt.
    /// </summary>
    public const string Instructions = "Keep each conversation to one task. When a new user message asks for a task unrelated to the one " +
        "this conversation started with (or to one the user has since told you to do here), don't start on it: call " +
        "mcp__nomad_session__suggest_new_chat with that task as a self-contained prompt, tell the user in one line that it has a " +
        "card of its own, and end your turn. Do it here only if the user then says so. A follow-up, refinement or fix of the " +
        "current task is not a new task, nor is the conversation's first request or a question you can answer in a few sentences.\n\n" +
        "When you notice work worth doing that falls outside what the user asked for (an unrelated bug, a follow-up, a cleanup), " +
        "don't do it here either: suggest it the same way with a short title and a self-contained prompt, then carry on with the " +
        "current task. Use it sparingly, and never to split up the current task.";

    /// <summary>
    /// Sent with every prompt after a conversation's first (a separate text block, which transcripts don't show): Claude weighs
    /// the system prompt's rule against a direct request too lightly, and does the new task in place.
    /// </summary>
    public const string Reminder = "<system-reminder>If this message asks for a task unrelated to the one this conversation is " +
        "about (not a follow-up, refinement or fix of it, nor a question you can answer in a few sentences), don't start on it: " +
        "call mcp__nomad_session__suggest_new_chat with it as a self-contained prompt, say in one line that it has a card of its " +
        "own, and end your turn. Do it here only if the user says so.</system-reminder>";

    /// <summary>
    /// The tool as tools/list describes it. Always loaded (not deferred behind ToolSearch), so Claude sees it when a new task comes.
    /// </summary>
    public static JsonNode Descriptor() => JsonNode.Parse("""
        {"name":"suggest_new_chat","description":"Suggest a task for a conversation of its own: work noticed along the way that falls outside what the user asked for (an unrelated bug, a follow-up, a cleanup), or a task the user asked for that differs from the one this conversation is about. Nomad shows it to the user as a card they may start as a new chat in this folder; that chat has none of this conversation's context. Returns at once: don't do the suggested work here.","inputSchema":{"type":"object","properties":{"title":{"type":"string","description":"A few words naming the task, like a chat title."},"prompt":{"type":"string","description":"The new chat's first prompt. Self-contained: what to do, where (files, symbols) and what you found, since the new chat knows nothing of this one."},"reason":{"type":"string","description":"One short sentence for the user: why it deserves a chat of its own."}},"required":["title","prompt"],"additionalProperties":false},"_meta":{"anthropic/alwaysLoad":true}}
        """)!;

    /// <summary>
    /// Checks the suggestion and tells Claude it was shown; the card itself comes from the call in the transcript.
    /// </summary>
    public static JsonObject Call(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("arguments", out var input) || Str(input, "title") is not { Length: > 0 } ||
            Str(input, "prompt") is not { Length: > 0 })
        {
            return ToolResult("Provide a title and a self-contained prompt for the new chat.", error: true);
        }

        return ToolResult("The suggestion is shown to the user, who may start it as a new chat. Don't do the suggested work here: " +
            "carry on with the current task, or, if the suggestion was the user's request, end your turn.");
    }
}
