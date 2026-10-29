using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;
using Nomad.Core.Protocol;

namespace Nomad.Core.OpenCode;

/// <summary>
/// <see cref="IAgentBackend"/> over OpenCode: one session on the shared <see cref="OpenCodeServer"/>, driven over its
/// HTTP API, its events read from the server's event stream for the session's folder.
/// </summary>
public sealed class OpenCodeBackend : IAgentBackend
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private OpenCodeServer? _server;
    private OpenCodeTranslator? _translator;
    private Task? _events;
    private string _directory = "";
    private string? _model;
    private string _mode = "default";
    private volatile bool _listening;
    private bool _stopping;

    public string DisplayName => "OpenCode";

    public string? SessionId { get; private set; }

    public bool IsProcessRunning => _listening && _server?.IsRunning == true;

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
        _model = IsOpenCodeModel(options.Model) ? options.Model : null;
        _mode = options.PermissionMode ?? "default";
        var server = await OpenCodeServer.GetAsync(cancellationToken).ConfigureAwait(false);
        _server = server;

        string id;
        if (options.ResumeSessionId is { } resume && options.ForkSession)
        {
            id = await OpenCodeClient.ForkSessionAsync(server, _directory, resume, options.ResumeSessionAt, cancellationToken).ConfigureAwait(false);
            await OpenCodeClient.UpdateSessionAsync(server, _directory, id, null, Rules(_mode), cancellationToken).ConfigureAwait(false);
        }
        else if (options.ResumeSessionId is { } existing)
        {
            id = existing;
            await OpenCodeClient.UpdateSessionAsync(server, _directory, id, null, Rules(_mode), cancellationToken).ConfigureAwait(false);

            // Rewind: Nomad's history gives each prompt its own message id as the point to resume "at", which here
            // means just before it.
            if (options.ResumeSessionAt is { } at)
            {
                await OpenCodeClient.RevertAsync(server, _directory, id, at, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            id = await OpenCodeClient.CreateSessionAsync(server, _directory, Rules(_mode), cancellationToken).ConfigureAwait(false);
        }

        SessionId = id;
        _translator = new OpenCodeTranslator(id, _directory);

        // Listen before anything can happen in the session, so no event is missed.
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _events = Task.Run(() => ReadEventsAsync(server, connected));
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        Raise(new SessionStartedEvent(id, _model, _directory, _mode, [], server.Version is { } v ? $"OpenCode {v}" : "OpenCode"));
    }

    public async Task SendPromptAsync(string text, string? uuid = null, IReadOnlyList<PromptImage>? images = null, CancellationToken cancellationToken = default)
    {
        var (server, id) = Require();
        lock (_gate)
        {
            _translator!.BeginTurn();
        }

        Raise(new TurnStartedEvent(text));
        try
        {
            await OpenCodeClient.PromptAsync(server, _directory, id, text, images, _model, AgentFor(_mode), null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _translator!.InterruptRequested = true;
            }

            throw;
        }
    }

    public async Task RespondAsync(PermissionRequest request, PermissionDecision decision, CancellationToken cancellationToken = default)
    {
        var (server, _) = Require();
        lock (_gate)
        {
            if (!_translator!.TakePending(request.RequestId, out _))
            {
                return; // Answered or withdrawn already.
            }
        }

        if (request.RequestId.StartsWith("que", StringComparison.Ordinal))
        {
            if (decision.Choice == PermissionChoice.Deny || decision.UpdatedInput is not { } answered)
            {
                await OpenCodeClient.RejectQuestionAsync(server, _directory, request.RequestId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await OpenCodeClient.ReplyQuestionAsync(server, _directory, request.RequestId, OpenCodeTools.Answers(request.Input, answered), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var reply = decision.Choice switch
        {
            PermissionChoice.Deny => "reject",
            PermissionChoice.AllowForSession => "always",
            _ => "once",
        };
        await OpenCodeClient.ReplyPermissionAsync(server, _directory, request.RequestId, reply, decision.Choice == PermissionChoice.Deny ? decision.DenyMessage : null, cancellationToken).ConfigureAwait(false);
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_server is null || SessionId is null || !IsTurnRunning)
        {
            return;
        }

        lock (_gate)
        {
            _translator!.InterruptRequested = true;
        }

        await OpenCodeClient.AbortAsync(_server, _directory, SessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next prompt goes to this model ("provider/model").</summary>
    public Task<bool> SetModelAsync(string model, CancellationToken cancellationToken = default)
    {
        if (!IsOpenCodeModel(model))
        {
            return Task.FromResult(false);
        }

        _model = model;
        Raise(new ModelChangedEvent(model));
        return Task.FromResult(true);
    }

    public async Task<bool> SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (_server is null || SessionId is null)
        {
            return false;
        }

        await OpenCodeClient.UpdateSessionAsync(_server, _directory, SessionId, null, Rules(mode), cancellationToken).ConfigureAwait(false);
        _mode = mode;
        Raise(new PermissionModeChangedEvent(mode));
        return true;
    }

    public Task<bool> SetEffortAsync(string? effort, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> SetFastModeAsync(bool enabled, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> SetWorkflowSettingsAsync(bool? ultracode, string? size, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> StopTaskAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>OpenCode names a session itself after its first prompt: this waits for that name (up to 20 s).</summary>
    public async Task<string?> GenerateTitleAsync(string description, bool persist, CancellationToken cancellationToken = default)
    {
        if (_server is null || SessionId is null)
        {
            return null;
        }

        for (var i = 0; i < 40 && !cancellationToken.IsCancellationRequested; i++)
        {
            string title;
            lock (_gate)
            {
                title = _translator!.Title;
            }

            if (title.Length == 0)
            {
                try
                {
                    title = OpenCodeTitles.Clean(await OpenCodeClient.GetTitleAsync(_server, _directory, SessionId, cancellationToken).ConfigureAwait(false));
                }
                catch (HttpRequestException)
                {
                }
            }

            if (title.Length > 0)
            {
                return title;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public Task<RemoteControlInfo?> SetRemoteControlAsync(bool enabled, string? name, string? reattachSessionId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<RemoteControlInfo?>(null);

    /// <summary>Stops listening to the session. A running turn goes on in OpenCode; Nomad sleeps only idle sessions.</summary>
    public async Task StopAsync()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _stop.Cancel();
        if (_events is not null)
        {
            await Task.WhenAny(_events, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>
    /// The permission rules a Nomad mode stands for. Manual asks before edits, commands and fetches; Accept edits
    /// lets edits through; Bypass allows everything; Plan runs OpenCode's plan agent, which may not edit at all.
    /// </summary>
    internal static JsonArray Rules(string mode)
    {
        static JsonObject Rule(string permission, string action) => new() { ["permission"] = permission, ["pattern"] = "*", ["action"] = action };

        return mode switch
        {
            "bypassPermissions" => [Rule("edit", "allow"), Rule("bash", "allow"), Rule("webfetch", "allow"), Rule("external_directory", "allow")],
            "acceptEdits" => [Rule("edit", "allow"), Rule("bash", "ask"), Rule("webfetch", "ask"), Rule("external_directory", "ask")],
            _ => [Rule("edit", "ask"), Rule("bash", "ask"), Rule("webfetch", "ask"), Rule("external_directory", "ask")],
        };
    }

    internal static string AgentFor(string mode) => mode == "plan" ? "plan" : "build";

    /// <summary>An OpenCode model id names its provider ("openrouter/…"); Claude Code's ids ("claude-opus-5-5") do not.</summary>
    public static bool IsOpenCodeModel(string? model) => OpenCodeClient.SplitModel(model) is not null;

    private async Task ReadEventsAsync(OpenCodeServer server, TaskCompletionSource connected)
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await OpenCodeClient.ReadEventsAsync(server, _directory, OnEvent, () =>
                {
                    _listening = true;
                    failures = 0;
                    connected.TrySetResult();
                }, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                connected.TrySetException(ex);
            }

            // The stream ended: the server went away, or dropped the connection. Reconnect while it still runs.
            _listening = false;
            if (_stop.IsCancellationRequested || !server.IsRunning || ++failures > 5)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300 * failures)).ConfigureAwait(false);
        }

        _listening = false;
        var expected = _stopping;
        if (!expected)
        {
            List<AgentEvent> ended = [];
            lock (_gate)
            {
                if (_translator is { IsTurnRunning: true })
                {
                    ended.Add(new TurnCompletedEvent(new TurnResult { Subtype = "process_exited", IsError = true, ErrorMessage = "OpenCode stopped responding." }));
                }
            }

            foreach (var e in ended)
            {
                Raise(e);
            }
        }

        Raise(new ProcessExitedEvent(0, expected, ""));
    }

    private void OnEvent(JsonElement ev)
    {
        List<AgentEvent> events;
        lock (_gate)
        {
            events = [.. _translator!.Handle(ev)];
        }

        foreach (var e in events)
        {
            Raise(e);
        }
    }

    private (OpenCodeServer Server, string SessionId) Require() =>
        _server is { IsRunning: true } server && SessionId is { } id && !_stopping
            ? (server, id)
            : throw new InvalidOperationException("OpenCode is not running for this session.");

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
