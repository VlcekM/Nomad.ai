using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;
using Nomad.Core.Codex;
using Nomad.Core.Protocol;

namespace Nomad.Core.Antigravity;

/// <summary>
/// Runs an Antigravity coding session through its ACP server.
/// </summary>
public sealed class AntigravityBackend : IAgentBackend
{
    #region Session

    /// <summary>
    /// The default account model requested for a new Antigravity session.
    /// </summary>
    public const string DefaultModel = "gemini-4-neon";

    /// <summary>
    /// The running ACP process.
    /// </summary>
    private AcpConnection? _connection;

    /// <summary>
    /// The session's current model option, if the server offers one.
    /// </summary>
    private string? _modelOption;

    /// <summary>
    /// Model IDs the CLI offers to this signed-in account.
    /// </summary>
    private HashSet<string> _availableModels = [];

    /// <summary>
    /// The CLI's thinking-level selector for the selected model.
    /// </summary>
    private JsonElement _thinkingLevel;

    /// <summary>
    /// The CLI's execution-mode selector and its current value.
    /// </summary>
    private JsonElement _executionMode;

    /// <summary>
    /// The model associated with streamed messages.
    /// </summary>
    private string? _model;

    /// <summary>
    /// The agent's name in Nomad.
    /// </summary>
    public string DisplayName => "Antigravity";

    /// <summary>
    /// Delivers transcript and session updates to Nomad.
    /// </summary>
    public event Action<AgentEvent>? EventReceived;

    /// <summary>
    /// The agent's persistent session identifier.
    /// </summary>
    public string? SessionId { get; private set; }

    /// <summary>
    /// Whether initialization has completed and the process is running.
    /// </summary>
    public bool IsProcessRunning => SessionId is not null && _connection?.IsRunning == true;

    /// <summary>
    /// Initializes ACP and opens or loads a session in the selected workspace.
    /// </summary>
    public async Task StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default)
    {
        if (options.ForkSession || options.ResumeSessionAt is not null)
            throw new InvalidOperationException("Antigravity does not support forking or rewinding in Nomad.");

        try
        {
            _connection = new AcpConnection(ExecutablePath, options.WorkingDirectory, ReceiveAsync, ProcessExited, options.AdditionalDirectories);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Could not start Antigravity. Install Antigravity CLI and run agy acp --login.", ex);
        }

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await OpenSessionAsync(options, startup.Token).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Shuts down the session's subprocess.
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _connection, null) is { } connection)
            await connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Releases process resources.
    /// </summary>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Changes the model using the session's advertised config option.
    /// </summary>
    public async Task<bool> SetModelAsync(string model, CancellationToken cancellationToken = default)
    {
        if (_model == model) return true;
        if (_availableModels.Count > 0 && !_availableModels.Contains(model))
            throw new InvalidOperationException($"Antigravity does not offer {model} for the signed-in account.");

        if (_modelOption is null || IsTurnRunning) return false;

        var args = SessionParameters();
        args["configId"] = _modelOption;
        args["value"] = model;
        var result = await Connection.CallAsync("session/set_config_option", args, cancellationToken).ConfigureAwait(false);
        ReadThinkingLevel(result);

        _model = model;
        Raise(new ModelChangedEvent(model));
        return true;
    }

    /// <summary>
    /// The live transport, required for RPC calls.
    /// </summary>
    private AcpConnection Connection => _connection ?? throw new InvalidOperationException("Antigravity is not running.");

    /// <summary>
    /// Negotiates the ACP version and selects the requested model.
    /// </summary>
    private async Task OpenSessionAsync(AgentStartOptions options, CancellationToken cancellationToken)
    {
        var hello = await Connection.CallAsync("initialize", InitializeParameters(), cancellationToken).ConfigureAwait(false);
        if (CodexJson.Long(hello, "protocolVersion") != 1) throw new InvalidOperationException("Antigravity needs a different ACP version.");

        var args = new JsonObject { ["cwd"] = options.WorkingDirectory, ["mcpServers"] = new JsonArray() };
        var method = "session/new";
        if (options.ResumeSessionId is { } id)
        {
            var capabilities = CodexJson.Obj(hello, "agentCapabilities");
            var sessionCapabilities = CodexJson.Obj(capabilities, "sessionCapabilities");
            method = CodexJson.Obj(sessionCapabilities, "resume").ValueKind == JsonValueKind.Object ? "session/resume" : "session/load";
            args["sessionId"] = id;
        }

        var session = await Connection.CallAsync(method, args, cancellationToken).ConfigureAwait(false);
        ReadThinkingLevel(session);
        _executionMode = ConfigOption(session, "mode");
        SessionId = options.ResumeSessionId ?? CodexJson.Str(session, "sessionId")
            ?? throw new InvalidOperationException("Antigravity did not return a session ID.");

        var model = ModelOption(session);
        _modelOption = CodexJson.Str(model, "id");
        _availableModels = [.. CodexJson.Arr(model, "options").Select(option => CodexJson.Str(option, "value")).OfType<string>()];
        _model = CodexJson.Str(model, "currentValue");
        var requested = string.IsNullOrWhiteSpace(options.Model) ? DefaultModel : options.Model;
        if (!await SetModelAsync(requested, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Antigravity did not expose model selection. Update the CLI and check your Google sign-in.");

        if (options.Effort is { Length: > 0 } effort && !await SetEffortAsync(effort, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Antigravity did not expose thinking-level selection. Update the CLI.");

        var mode = options.PermissionMode ?? "default";
        if (!await SetPermissionModeAsync(mode, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Antigravity could not apply the selected execution mode.");

        Raise(new SessionStartedEvent(SessionId, _model, options.WorkingDirectory, mode, [], DisplayName));
    }

    /// <summary>
    /// Creates the shared parameters for session-scoped requests.
    /// </summary>
    private JsonObject SessionParameters() => new() { ["sessionId"] = SessionId };

    /// <summary>
    /// Finds the CLI at Google's default Windows installation or on PATH.
    /// </summary>
    internal static string ExecutablePath
    {
        get
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
            return File.Exists(installed) ? installed : "agy";
        }
    }

    /// <summary>
    /// The ACP version and capabilities supported by Nomad.
    /// </summary>
    internal static JsonObject InitializeParameters() => new()
    {
        ["protocolVersion"] = 1,
        ["clientCapabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "nomad", ["title"] = "Nomad" },
    };

    /// <summary>
    /// Finds the model selector in the session's advertised configuration.
    /// </summary>
    internal static JsonElement ModelOption(JsonElement session) => ConfigOption(session, "model");

    /// <summary>
    /// Finds a configuration selector by its ACP category.
    /// </summary>
    private static JsonElement ConfigOption(JsonElement session, string category) =>
        CodexJson.Arr(session, "configOptions").FirstOrDefault(option => CodexJson.Str(option, "category") == category);

    /// <summary>
    /// Propagates unexpected exits to the session view.
    /// </summary>
    private void ProcessExited(int code, bool expected) =>
        Raise(new ProcessExitedEvent(code, expected, expected ? "" : "Antigravity CLI stopped. Check agy acp --login."));

    /// <summary>
    /// Publishes a backend-neutral event.
    /// </summary>
    private void Raise(AgentEvent e) => EventReceived?.Invoke(e);

    #endregion

    #region Turns

    /// <summary>
    /// The current prompt's streaming state.
    /// </summary>
    private AntigravityTurn? _turn;

    /// <summary>
    /// Whether a prompt is still running.
    /// </summary>
    public bool IsTurnRunning => Volatile.Read(ref _turn) is not null;

    /// <summary>
    /// Sends prompt content and streams updates until the agent returns its stop reason.
    /// </summary>
    public async Task SendPromptAsync(string text, string? uuid = null, IReadOnlyList<PromptImage>? images = null,
        CancellationToken cancellationToken = default)
    {
        var connection = Connection;
        var turn = new AntigravityTurn(_model, Raise);
        if (Interlocked.CompareExchange(ref _turn, turn, null) is not null)
            throw new InvalidOperationException("Antigravity is already working on a prompt.");

        var start = Stopwatch.GetTimestamp();
        var result = new TurnResult();
        try
        {
            Raise(new TurnStartedEvent(text));
            var parameters = SessionParameters();
            parameters["prompt"] = PromptContent(text, images);
            var reply = await connection.CallAsync("session/prompt", parameters, cancellationToken).ConfigureAwait(false);
            var reason = CodexJson.Str(reply, "stopReason") ?? "end_turn";
            result = new TurnResult { Subtype = reason, Interrupted = reason == "cancelled" };
        }
        catch (OperationCanceledException)
        {
            await InterruptAsync().ConfigureAwait(false);
            result = new TurnResult { Subtype = "cancelled", Interrupted = true };
        }
        catch (Exception ex)
        {
            result = new TurnResult { Subtype = "error", IsError = true, ErrorMessage = ex.Message };
        }
        finally
        {
            turn.FinishBlock();
            foreach (var id in _permissions.Keys)
            {
                if (_permissions.TryRemove(id, out _)) Raise(new PermissionCancelledEvent(id));
            }

            Volatile.Write(ref _turn, null);
            Raise(new TurnCompletedEvent(result with { DurationMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds }));
        }
    }

    /// <summary>
    /// Asks the agent to stop the current prompt.
    /// </summary>
    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        IsTurnRunning && _connection is { IsRunning: true } connection
            ? connection.NotifyAsync("session/cancel", SessionParameters(), cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Encodes text and image attachments as ACP content blocks.
    /// </summary>
    private static JsonArray PromptContent(string text, IReadOnlyList<PromptImage>? images)
    {
        var content = new JsonArray();
        if (text.Length > 0) content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });

        foreach (var image in images ?? [])
        {
            content.Add((JsonNode)new JsonObject { ["type"] = "image", ["mimeType"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data) });
        }

        return content;
    }

    #endregion

    #region Agent requests

    /// <summary>
    /// ACP requests awaiting the user's approval decision.
    /// </summary>
    private readonly ConcurrentDictionary<string, JsonElement> _permissions = new();

    /// <summary>
    /// Selects the permission option the agent offered for the user's decision.
    /// </summary>
    public Task RespondAsync(PermissionRequest request, PermissionDecision decision, CancellationToken cancellationToken = default)
    {
        if (!_permissions.TryRemove(request.RequestId, out var message)) return Task.CompletedTask;

        var kind = decision.Choice switch
        {
            PermissionChoice.Allow => "allow_once",
            PermissionChoice.AllowForSession => "allow_once",
            _ => "reject_once",
        };
        var option = CodexJson.Arr(CodexJson.Obj(message, "params"), "options")
            .FirstOrDefault(o => CodexJson.Str(o, "kind") == kind);
        var optionId = CodexJson.Str(option, "optionId");
        var outcome = optionId is null
            ? new JsonObject { ["outcome"] = "cancelled" }
            : new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId };

        return Connection.ReplyAsync(CodexJson.Any(message, "id"), new JsonObject { ["outcome"] = outcome }, cancellationToken);
    }

    /// <summary>
    /// Routes live updates and permission requests from the subprocess.
    /// </summary>
    private Task ReceiveAsync(JsonElement message)
    {
        var method = CodexJson.Str(message, "method");
        var parameters = CodexJson.Obj(message, "params");
        var id = CodexJson.Any(message, "id");
        if (method == "session/update")
        {
            Volatile.Read(ref _turn)?.Update(CodexJson.Obj(parameters, "update"));
        }
        else if (method == "session/request_permission" && IsTurnRunning)
        {
            RequestPermission(message, id, parameters);
        }
        else if (id.ValueKind != JsonValueKind.Undefined && _connection is { IsRunning: true } connection)
        {
            return connection.UnsupportedAsync(id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Converts the permission prompt to a Nomad approval card.
    /// </summary>
    private void RequestPermission(JsonElement message, JsonElement id, JsonElement parameters)
    {
        var key = id.GetRawText();
        var tool = CodexJson.Obj(parameters, "toolCall");
        var title = CodexJson.Str(tool, "title") ?? "Antigravity action";
        _permissions[key] = message.Clone();
        Raise(new PermissionRequestedEvent(new PermissionRequest
        {
            RequestId = key,
            ToolUseId = CodexJson.Str(tool, "toolCallId"),
            ToolName = CodexJson.Str(tool, "kind") ?? "Antigravity",
            Title = title,
            Description = title,
            Input = CodexJson.Keep(CodexJson.Any(tool, "rawInput")),
            SuppressAllowForSession = true,
        }));
    }

    #endregion

    #region Optional capabilities

    /// <summary>
    /// Applies the corresponding Antigravity CLI execution mode.
    /// </summary>
    public async Task<bool> SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        var value = mode switch
        {
            "acceptEdits" => "accept-edits",
            "default" or "auto" or "plan" => mode,
            _ => throw new InvalidOperationException($"Antigravity does not support mode {mode}."),
        };

        if (CodexJson.Str(_executionMode, "id") is not { } id)
        {
            if (mode == "default") return true;

            throw new InvalidOperationException("Antigravity did not expose execution modes. Update the CLI.");
        }

        if (CodexJson.Str(_executionMode, "currentValue") == value) return true;
        if (!CodexJson.Arr(_executionMode, "options").Any(option => CodexJson.Str(option, "value") == value))
            throw new InvalidOperationException($"Antigravity does not offer {value} mode with the current CLI configuration.");

        var args = SessionParameters();
        args["configId"] = id;
        args["value"] = value;
        var result = await Connection.CallAsync("session/set_config_option", args, cancellationToken).ConfigureAwait(false);
        _executionMode = ConfigOption(result, "mode");
        return CodexJson.Str(_executionMode, "currentValue") == value;
    }

    /// <summary>
    /// Sets the reasoning effort through the CLI's advertised thinking-level option.
    /// </summary>
    public async Task<bool> SetEffortAsync(string? effort, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(effort)) return true;
        if (IsTurnRunning || CodexJson.Str(_thinkingLevel, "id") is not { } id) return false;
        if (CodexJson.Str(_thinkingLevel, "currentValue") == effort) return true;
        if (!CodexJson.Arr(_thinkingLevel, "options").Any(option => CodexJson.Str(option, "value") == effort))
            throw new InvalidOperationException($"Antigravity does not offer thinking level {effort} for {_model}.");

        var args = SessionParameters();
        args["configId"] = id;
        args["value"] = effort;
        var result = await Connection.CallAsync("session/set_config_option", args, cancellationToken).ConfigureAwait(false);
        ReadThinkingLevel(result);
        return CodexJson.Str(_thinkingLevel, "currentValue") == effort;
    }

    /// <summary>
    /// Reads the updated thinking levels returned after session creation or model selection.
    /// </summary>
    private void ReadThinkingLevel(JsonElement session)
    {
        var option = ConfigOption(session, "thought_level");
        if (option.ValueKind != JsonValueKind.Undefined) _thinkingLevel = option;
    }

    /// <summary>
    /// This backend does not implement fast mode.
    /// </summary>
    public Task<bool> SetFastModeAsync(bool enabled, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// This backend does not implement Claude workflows.
    /// </summary>
    public Task<bool> SetWorkflowSettingsAsync(bool? ultracode, string? size, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// This backend cancels at the turn level.
    /// </summary>
    public Task<bool> StopTaskAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Leaves title generation to Nomad.
    /// </summary>
    public Task<string?> GenerateTitleAsync(string description, bool persist, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// This backend does not implement Claude Remote Control.
    /// </summary>
    public Task<RemoteControlInfo?> SetRemoteControlAsync(bool enabled, string? name, string? reattachSessionId = null,
        CancellationToken cancellationToken = default) => Task.FromResult<RemoteControlInfo?>(null);

    #endregion
}
