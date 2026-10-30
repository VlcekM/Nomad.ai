using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nomad.Core.Agents;

/// <summary>A small SDK-hosted MCP server. Approval belongs to Nomad, even in bypass mode.</summary>
internal sealed class FolderMoveTool(string workingDirectory)
{
    public const string ServerName = "nomad_session";
    public const string ToolName = "mcp__nomad_session__request_folder_move";
    public const string PermissionToolName = "MoveFolder";
    public const string Instructions = "When the user's task belongs in another repository or working folder, " +
        "use mcp__nomad_session__request_folder_move with its path and a short reason before working there. " +
        "Nomad asks the user and resumes this conversation in the approved folder with its project instructions. " +
        "After a move is approved, end your turn immediately; Nomad continues the task there. " +
        "Reading a reference file elsewhere does not require moving the conversation.";

    private readonly object _gate = new();
    private (string Id, string RpcId, TaskCompletionSource<PermissionDecision> Answer)? _pending;
    private bool _cancelled;

    public event Action<PermissionRequest>? PermissionRequested;
    public event Action<FolderMoveApprovedEvent>? MoveApproved;
    public event Action<string>? PermissionCancelled;

    public bool Respond(PermissionRequest request, PermissionDecision decision)
    {
        lock (_gate)
        {
            if (_pending is not { } pending || pending.Id != request.RequestId)
            {
                return false;
            }

            // Keep the slot until the call has finished, so concurrent requests cannot approve two moves.
            return pending.Answer.TrySetResult(decision);
        }
    }

    public void Cancel()
    {
        string? cancelled = null;
        lock (_gate)
        {
            _cancelled = true;
            if (_pending is { } pending && pending.Answer.TrySetResult(new PermissionDecision(PermissionChoice.Deny, "The folder move was cancelled.")))
            {
                cancelled = pending.Id;
            }
        }

        if (cancelled is not null)
        {
            PermissionCancelled?.Invoke(cancelled);
        }
    }

    public async Task<JsonObject?> HandleAsync(JsonElement message, bool mainAgent = true)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var method = Str(message, "method");
        if (method == "notifications/cancelled" && message.TryGetProperty("params", out var cancellation) &&
            cancellation.TryGetProperty("requestId", out var cancelledId))
        {
            bool matches;
            lock (_gate)
            {
                matches = _pending is { } pending && pending.RpcId == cancelledId.GetRawText();
            }

            if (matches)
            {
                Cancel();
            }
        }

        if (!message.TryGetProperty("id", out var id))
        {
            return null; // MCP notifications need no JSON-RPC response.
        }

        JsonNode result;
        switch (method)
        {
            case "initialize":
                var version = message.TryGetProperty("params", out var init) ? Str(init, "protocolVersion") : null;
                result = new JsonObject
                {
                    ["protocolVersion"] = version ?? "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
                };
                break;
            case "ping":
                result = new JsonObject();
                break;
            case "tools/list":
                result = JsonNode.Parse("""
                    {"tools":[{"name":"request_folder_move","description":"Ask the user to move this conversation to another repository or working folder. After approval, end the turn immediately: Nomad will resume the same conversation there, loading its project instructions. Do not use a shell cd to move the session. Never use this for a subagent or while background tasks are running.","inputSchema":{"type":"object","properties":{"path":{"type":"string","description":"Destination folder, preferably an absolute path. It must already exist."},"reason":{"type":"string","description":"Why the task belongs in this folder."}},"required":["path","reason"],"additionalProperties":false}}]}
                    """)!;
                break;
            case "tools/call":
                result = mainAgent ? await CallAsync(message).ConfigureAwait(false)
                    : ToolResult("Only the main conversation can request a folder move. Return this request to your parent agent.", error: true);
                break;
            default:
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Unknown MCP method." },
                };
        }

        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result };
    }

    private async Task<JsonObject> CallAsync(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters) || Str(parameters, "name") != "request_folder_move" ||
            !parameters.TryGetProperty("arguments", out var input) || Str(input, "path") is not { Length: > 0 } path ||
            Str(input, "reason") is not { Length: > 0 } reason)
        {
            return ToolResult("Provide a destination path and a reason for moving.", error: true);
        }

        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, workingDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return ToolResult("Invalid destination folder: " + ex.Message, error: true);
        }

        if (!Directory.Exists(path))
        {
            return ToolResult("The destination folder does not exist or cannot be accessed.", error: true);
        }

        if (string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory)), StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult("This conversation is already in that folder.", error: true);
        }

        var request = new PermissionRequest
        {
            RequestId = "nomad-folder-" + Guid.NewGuid().ToString("N"),
            ToolName = PermissionToolName,
            DisplayName = "folder move",
            Description = reason,
            Input = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["path"] = path, ["from"] = workingDirectory },
                Protocol.ProtocolJsonContext.Default.DictionaryStringString),
            ToolUseId = ToolUseId(message),
            SuppressAllowForSession = true,
        };
        var answer = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pending is not null)
            {
                return ToolResult("Another folder move is already waiting for an answer.", error: true);
            }

            _pending = (request.RequestId, message.GetProperty("id").GetRawText(), answer);
            _cancelled = false;
        }

        try
        {
            PermissionRequested?.Invoke(request);
            var decision = await answer.Task.ConfigureAwait(false);
            lock (_gate)
            {
                if (_cancelled)
                {
                    return ToolResult("The folder move was cancelled.", error: true);
                }
            }

            if (decision.Choice == PermissionChoice.Deny)
            {
                return ToolResult(decision.DenyMessage ?? "The user chose to stay in the current folder. Do not move or repeat the request unless asked.", error: true);
            }

            if (!Directory.Exists(path))
            {
                return ToolResult("The destination folder is no longer available. The conversation has not moved.", error: true);
            }

            MoveApproved?.Invoke(new FolderMoveApprovedEvent(path, request.ToolUseId));
            return ToolResult($"The user approved moving this conversation to {path}. End your turn now without running further tools. Nomad will resume the same conversation in that folder and continue the task.");
        }
        finally
        {
            lock (_gate)
            {
                _pending = null;
            }
        }
    }

    private static JsonObject ToolResult(string text, bool error = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    };

    private static string? Str(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() : null;

    internal static string? ToolUseId(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("params", out var parameters) &&
        parameters.TryGetProperty("_meta", out var meta) ? Str(meta, "claudecode/toolUseId") : null;
}
