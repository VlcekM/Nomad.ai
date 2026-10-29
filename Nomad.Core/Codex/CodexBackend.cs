using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;
using Nomad.Core.Protocol;

namespace Nomad.Core.Codex;

/// <summary>
/// <see cref="IAgentBackend"/> over Codex (OpenAI's agent, on a ChatGPT plan or an API key): one thread on the shared
/// <see cref="CodexServer"/>, driven over its JSON-RPC API, its notifications and requests heard as that thread's
/// listener.
/// </summary>
public sealed class CodexBackend : IAgentBackend, ICodexThreadListener
{
    private readonly Lock _gate = new();
    private CodexServer? _server;
    private CodexTranslator? _translator;
    private string _directory = "";
    private volatile string? _model;
    private volatile string? _effort;
    private volatile string _mode = "default";
    private volatile bool _stopping;
    private volatile bool _exited;

    public string DisplayName => "Codex";

    public string? SessionId { get; private set; }

    public bool IsProcessRunning => _server?.IsRunning == true && SessionId is not null && !_stopping && !_exited;

    public bool IsTurnRunning
    {
        get
        {
            lock (_gate)
            {
                return _translator?.IsTurnRunning == true;
            }
        }
    }

    public event Action<AgentEvent>? EventReceived;

    public async Task StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            throw new InvalidOperationException("The backend was already started.");
        }

        _directory = options.WorkingDirectory;
        _model = string.IsNullOrEmpty(options.Model) ? null : options.Model;
        _effort = string.IsNullOrEmpty(options.Effort) ? null : options.Effort;
        _mode = options.PermissionMode ?? "default";
        var server = await CodexServer.GetAsync(cancellationToken).ConfigureAwait(false);
        _server = server;

        var (approval, sandbox, reviewer) = Policy(_mode);
        var parameters = new JsonObject
        {
            ["cwd"] = _directory,
            ["model"] = _model,
            ["approvalPolicy"] = approval,
            ["approvalsReviewer"] = reviewer,
            ["sandbox"] = sandbox,
        };

        JsonElement result;
        if (options.ResumeSessionId is { } source && options.ForkSession)
        {
            parameters["threadId"] = source;
            parameters["excludeTurns"] = true;
            result = await server.CallAsync("thread/fork", parameters, cancellationToken).ConfigureAwait(false);
        }
        else if (options.ResumeSessionId is { } existing)
        {
            parameters["threadId"] = existing;
            parameters["excludeTurns"] = true;
            result = await server.CallAsync("thread/resume", parameters, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await server.CallAsync("thread/start", parameters, cancellationToken).ConfigureAwait(false);
        }

        var thread = CodexJson.Obj(result, "thread");
        var id = CodexJson.Str(thread, "id") ?? throw new InvalidOperationException("Codex did not start a thread.");
        var model = CodexJson.Str(result, "model");
        var modelGiven = _model is not null;
        _model ??= model;

        // Rewind or fork from a message: the thread loses that message's turn and everything after it.
        if (options.ResumeSessionAt is { } at)
        {
            await RevertAsync(server, id, at, cancellationToken).ConfigureAwait(false);
        }

        SessionId = id;
        lock (_gate)
        {
            _translator = new CodexTranslator(id, _directory) { Model = _model };
        }

        server.Attach(id, this);
        Raise(new SessionStartedEvent(id, _model, _directory, _mode, [], server.Version is { } v ? $"Codex {v}" : "Codex"));
        if (!modelGiven && _model is { } chosen)
        {
            Raise(new ModelChangedEvent(chosen));
        }
    }

    public async Task SendPromptAsync(string text, string? uuid = null, IReadOnlyList<PromptImage>? images = null, CancellationToken cancellationToken = default)
    {
        var (server, id) = Require();
        lock (_gate)
        {
            _translator!.BeginTurn();
        }

        Raise(new TurnStartedEvent(text));
        var input = new JsonArray();
        if (text.Length > 0)
        {
            input.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text, ["text_elements"] = new JsonArray() });
        }

        foreach (var image in images ?? [])
        {
            input.Add((JsonNode)new JsonObject { ["type"] = "image", ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" });
        }

        var (approval, _, reviewer) = Policy(_mode);
        var parameters = new JsonObject
        {
            ["threadId"] = id,
            ["clientUserMessageId"] = uuid,
            ["input"] = input,
            ["model"] = _model,
            ["effort"] = _effort,
            ["summary"] = "auto",
            ["approvalPolicy"] = approval,
            ["approvalsReviewer"] = reviewer,
            ["sandboxPolicy"] = SandboxPolicy(_mode),
        };

        // Plan mode is Codex's own collaboration mode; any other leaves it.
        if (_model is { } model)
        {
            parameters["collaborationMode"] = new JsonObject
            {
                ["mode"] = _mode == "plan" ? "plan" : "default",
                ["settings"] = new JsonObject { ["model"] = model, ["reasoning_effort"] = _effort, ["developer_instructions"] = null },
            };
        }

        try
        {
            var result = await server.CallAsync("turn/start", parameters, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (CodexJson.Str(CodexJson.Obj(result, "turn"), "id") is { } turnId && _translator!.IsTurnRunning && _translator.TurnId is null)
                {
                    _translator.Handle("turn/started", CodexJson.ToElement(new JsonObject { ["turn"] = new JsonObject { ["id"] = turnId } })).ToList();
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                _translator!.CancelTurn();
            }

            throw;
        }
    }

    public async Task RespondAsync(PermissionRequest request, PermissionDecision decision, CancellationToken cancellationToken = default)
    {
        var (server, _) = Require();
        CodexPendingRequest? pending;
        lock (_gate)
        {
            if (!_translator!.TakePending(request.RequestId, out pending) || pending is null)
            {
                return; // Answered or withdrawn already.
            }
        }

        await server.RespondAsync(pending.Id, CodexTranslator.Reply(pending, decision), cancellationToken).ConfigureAwait(false);
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_server is null || SessionId is null || !IsTurnRunning)
        {
            return;
        }

        string? turnId;
        lock (_gate)
        {
            _translator!.InterruptRequested = true;
            turnId = _translator.TurnId;
        }

        // Stopped the moment it was sent: the turn has no id yet, so wait for it briefly.
        for (var i = 0; turnId is null && i < 20 && IsTurnRunning; i++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                turnId = _translator!.TurnId;
            }
        }

        if (turnId is not null)
        {
            await _server.CallAsync("turn/interrupt", new JsonObject { ["threadId"] = SessionId, ["turnId"] = turnId }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The next prompt goes to this model.</summary>
    public Task<bool> SetModelAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(model))
        {
            return Task.FromResult(false);
        }

        _model = model;
        lock (_gate)
        {
            _translator?.Model = model;
        }

        Raise(new ModelChangedEvent(model));
        return Task.FromResult(true);
    }

    /// <summary>The next prompt runs under this mode's approval policy and sandbox.</summary>
    public Task<bool> SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        _mode = mode;
        Raise(new PermissionModeChangedEvent(mode));
        return Task.FromResult(true);
    }

    /// <summary>The next prompt runs at this reasoning effort, or the model's default for null / empty.</summary>
    public Task<bool> SetEffortAsync(string? effort, CancellationToken cancellationToken = default)
    {
        _effort = string.IsNullOrEmpty(effort) ? null : effort;
        return Task.FromResult(true);
    }

    public Task<bool> SetFastModeAsync(bool enabled, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> SetWorkflowSettingsAsync(bool? ultracode, string? size, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> StopTaskAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>The thread's own name if Codex gave it one; Codex doesn't name threads by itself, so mostly null.</summary>
    public Task<string?> GenerateTitleAsync(string description, bool persist, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_translator?.Title is { Length: > 0 } title ? title : null);
        }
    }

    public Task<RemoteControlInfo?> SetRemoteControlAsync(bool enabled, string? name, string? reattachSessionId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<RemoteControlInfo?>(null);

    /// <summary>Stops listening to the thread. A running turn goes on in Codex; Nomad sleeps only idle sessions.</summary>
    public Task StopAsync()
    {
        if (_stopping)
        {
            return Task.CompletedTask;
        }

        _stopping = true;
        if (_server is { } server && SessionId is { } id)
        {
            server.Detach(id, this);
            if (server.IsRunning && !IsTurnRunning)
            {
                _ = UnsubscribeAsync(server, id);
            }

            Raise(new ProcessExitedEvent(0, true, ""));
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    void ICodexThreadListener.OnNotification(string method, JsonElement parameters)
    {
        List<AgentEvent> events;
        lock (_gate)
        {
            if (_translator is null || _stopping)
            {
                return;
            }

            events = [.. _translator.Handle(method, parameters)];
        }

        foreach (var e in events)
        {
            Raise(e);
        }
    }

    void ICodexThreadListener.OnRequest(JsonElement id, string method, JsonElement parameters)
    {
        List<AgentEvent> events;
        bool unsupported;
        lock (_gate)
        {
            if (_translator is null || _stopping)
            {
                unsupported = true;
                events = [];
            }
            else
            {
                events = [.. _translator.Request(id, method, parameters, out unsupported)];
            }
        }

        if (unsupported)
        {
            _ = _server?.RespondErrorAsync(id, $"Nomad does not handle {method}.");
        }

        foreach (var e in events)
        {
            Raise(e);
        }
    }

    void ICodexThreadListener.OnServerExited()
    {
        if (_stopping || _exited)
        {
            return;
        }

        _exited = true;
        List<AgentEvent> events;
        lock (_gate)
        {
            events = _translator is null ? [] : [.. _translator.ServerExited("Codex stopped responding.")];
        }

        foreach (var e in events)
        {
            Raise(e);
        }

        // A turn that ended with it said so already; else the exit is the news.
        var said = events.Any(e => e is TurnCompletedEvent);
        Raise(new ProcessExitedEvent(said ? 0 : 1, false, said ? "" : _server?.StderrTail ?? ""));
    }

    /// <summary>
    /// The approval policy, sandbox and approvals reviewer a Nomad mode stands for. Manual reads freely but asks
    /// before any change (a read-only sandbox); Accept edits lets Codex edit and run commands in the folder, asking
    /// to go beyond it; Auto has Codex's reviewer decide those asks; Plan plans in a read-only sandbox; Bypass runs
    /// everything, unsandboxed, without asking.
    /// </summary>
    internal static (string Approval, string Sandbox, string Reviewer) Policy(string mode) => mode switch
    {
        "bypassPermissions" => ("never", "danger-full-access", "user"),
        "acceptEdits" => ("on-request", "workspace-write", "user"),
        "auto" => ("on-request", "workspace-write", "auto_review"),
        _ => ("on-request", "read-only", "user"),
    };

    private static JsonObject SandboxPolicy(string mode) => Policy(mode).Sandbox switch
    {
        "danger-full-access" => new JsonObject { ["type"] = "dangerFullAccess" },
        "workspace-write" => new JsonObject
        {
            ["type"] = "workspaceWrite",
            ["writableRoots"] = new JsonArray(),
            ["networkAccess"] = false,
            ["excludeTmpdirEnvVar"] = false,
            ["excludeSlashTmp"] = false,
        },
        _ => new JsonObject { ["type"] = "readOnly", ["networkAccess"] = false },
    };

    /// <summary>Cuts the thread just before the turn of the prompt Nomad knows as <paramref name="at"/> (its id, or its turn's).</summary>
    private static async Task RevertAsync(CodexServer server, string threadId, string at, CancellationToken cancellationToken)
    {
        var thread = CodexJson.Obj(await CodexClient.ReadThreadAsync(server, threadId, cancellationToken).ConfigureAwait(false), "thread");
        foreach (var turn in CodexJson.Arr(thread, "turns"))
        {
            var turnId = CodexJson.Str(turn, "id");
            var matches = turnId == at || CodexJson.Arr(turn, "items").Any(i => CodexJson.Str(i, "type") == "userMessage" && CodexJson.Str(i, "clientId") == at);
            if (matches && turnId is not null)
            {
                await server.CallAsync("thread/revert", new JsonObject { ["threadId"] = threadId, ["beforeTurnId"] = turnId }, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private static async Task UnsubscribeAsync(CodexServer server, string threadId)
    {
        try
        {
            await server.CallAsync("thread/unsubscribe", new JsonObject { ["threadId"] = threadId }, timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CodexException or InvalidOperationException or TimeoutException)
        {
        }
    }

    private (CodexServer Server, string ThreadId) Require() =>
        _server is { IsRunning: true } server && SessionId is { } id && !_stopping && !_exited
            ? (server, id)
            : throw new InvalidOperationException("Codex is not running for this session.");

    private void Raise(AgentEvent e)
    {
        try
        {
            EventReceived?.Invoke(e);
        }
        catch (Exception)
        {
            // A faulty subscriber must not kill the event pump.
        }
    }
}
