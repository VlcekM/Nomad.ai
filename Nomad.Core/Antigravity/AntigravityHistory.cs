using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;
using Nomad.Core.Codex;
using Nomad.Core.Sessions;

namespace Nomad.Core.Antigravity;

/// <summary>
/// Reconstructs a saved chat from the ACP session/load replay.
/// </summary>
public static class AntigravityHistory
{
    /// <summary>
    /// Reads a saved conversation without submitting a prompt.
    /// </summary>
    public static async Task<SessionHistory> LoadAsync(string id, string cwd)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var items = new List<HistoryItem>();
        var user = new StringBuilder();
        var images = new List<HistoryImage>();
        var turn = new AntigravityTurn(null, e => Append(items, e));
        await using var connection = new AcpConnection(AntigravityBackend.ExecutablePath, cwd, Receive, (_, _) => { });
        await connection.CallAsync("initialize", AntigravityBackend.InitializeParameters(), timeout.Token).ConfigureAwait(false);
        var result = await connection.CallAsync("session/load", new JsonObject
        {
            ["sessionId"] = id,
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, timeout.Token).ConfigureAwait(false);

        FlushUser();
        turn.FinishBlock();
        return new SessionHistory
        {
            SessionId = id,
            WorkingDirectory = cwd,
            Model = CodexJson.Str(AntigravityBackend.ModelOption(result), "currentValue"),
            Items = items,
        };

        Task Receive(JsonElement message)
        {
            var update = CodexJson.Obj(CodexJson.Obj(message, "params"), "update");
            switch (CodexJson.Str(update, "sessionUpdate"))
            {
                case "user_message_chunk":
                    turn.FinishBlock();
                    var content = CodexJson.Obj(update, "content");
                    user.Append(CodexJson.Str(content, "text"));
                    if (CodexJson.Str(content, "mimeType") is { } mime && CodexJson.Str(content, "data") is { } data)
                        images.Add(new HistoryImage(mime, data));
                    break;
                case "agent_message_chunk":
                case "agent_thought_chunk":
                case "tool_call":
                case "tool_call_update":
                    FlushUser();
                    turn.Update(update);
                    break;
            }

            return Task.CompletedTask;
        }

        void FlushUser()
        {
            if (user.Length == 0 && images.Count == 0) return;

            items.Add(new HistoryUserMessage(user.ToString()) { Images = images.ToArray() });
            user.Clear();
            images.Clear();
            turn = new AntigravityTurn(null, e => Append(items, e));
        }
    }

    /// <summary>
    /// Saves completed blocks and tool events from the replay in display order.
    /// </summary>
    private static void Append(List<HistoryItem> items, AgentEvent e)
    {
        var item = e switch
        {
            BlockCompletedEvent { Kind: AgentBlockKind.Text, Text: { } text } block => (HistoryItem)new HistoryAssistantText(block.MessageId, text),
            BlockCompletedEvent { Kind: AgentBlockKind.Thinking, Text: { } text } => new HistoryThinking(text),
            ToolCallEvent call => new HistoryToolCall(call.ToolUseId, call.Name, call.Input),
            ToolResultEvent result => new HistoryToolResult(result.ToolUseId, result.IsError, result.Text, result.Structured),
            _ => null,
        };
        if (item is not null) items.Add(item);
    }
}
