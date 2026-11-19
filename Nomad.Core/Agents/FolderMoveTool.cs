using System.Text.Json;
using System.Text.Json.Nodes;
using static Nomad.Core.Agents.NomadSessionServer;

namespace Nomad.Core.Agents;

/// <summary>
/// The nomad_session tool that moves the conversation to another folder. Approval belongs to Nomad, even in bypass mode.
/// </summary>
internal sealed class FolderMoveTool(string workingDirectory)
{
    /// <summary>
    /// The tool's name on the server.
    /// </summary>
    public const string Name = "request_folder_move";

    /// <summary>
    /// The tool's name as Claude calls it.
    /// </summary>
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

    /// <summary>
    /// Withdraws the move waiting for the user, if it is the JSON-RPC call <paramref name="rpcId"/> the CLI just cancelled.
    /// </summary>
    public void CancelIfPending(string rpcId)
    {
        bool matches;
        lock (_gate)
        {
            matches = _pending is { } pending && pending.RpcId == rpcId;
        }

        if (matches) Cancel();
    }

    /// <summary>
    /// The tool as tools/list describes it.
    /// </summary>
    public static JsonNode Descriptor() => JsonNode.Parse("""
        {"name":"request_folder_move","description":"Ask the user to move this conversation to another repository or working folder. After approval, end the turn immediately: Nomad will resume the same conversation there, loading its project instructions. Do not use a shell cd to move the session. Never use this for a subagent or while background tasks are running.","inputSchema":{"type":"object","properties":{"path":{"type":"string","description":"Destination folder, preferably an absolute path. It must already exist."},"reason":{"type":"string","description":"Why the task belongs in this folder."}},"required":["path","reason"],"additionalProperties":false}}
        """)!;

    /// <summary>
    /// Asks the user about the move and answers once they have; the server has checked the call names this tool.
    /// </summary>
    public async Task<JsonObject> CallAsync(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("arguments", out var input) || Str(input, "path") is not { Length: > 0 } path ||
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
}
