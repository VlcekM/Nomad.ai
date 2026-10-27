using System.Collections.Concurrent;
using System.Text.Json;
using Nomad.Core.Process;
using Nomad.Core.Protocol;
using Nomad.Core.Sessions;

namespace Nomad.Core.Agents;

/// <summary>
/// <see cref="IAgentBackend"/> over the Claude Code CLI's stream-json protocol: one
/// <see cref="ClaudeCliProcess"/> per session, kept alive across turns.
/// </summary>
public sealed class ClaudeCodeBackend : IAgentBackend
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlResponseBody?>> _pendingControl = new();
    private readonly ConcurrentDictionary<string, PermissionRequest> _pendingPermissions = new();
    private readonly ConcurrentDictionary<string, byte> _sentUuids = new(StringComparer.Ordinal);
    private ClaudeCliProcess? _cli;
    private int _requestCounter;
    private volatile bool _turnRunning;
    private volatile bool _stopping;
    private bool _interruptRequested;

    // Streaming state of the current assistant message (touched only on the stdout thread).
    private string? _messageId;
    private int _openBlockIndex = -1;
    private int _syntheticMessageCounter;
    private string? _assistantError;

    public string DisplayName => "Claude Code";

    public string? SessionId { get; private set; }

    public bool IsProcessRunning => _cli?.IsRunning == true;

    public bool IsTurnRunning => _turnRunning;

    /// <summary>The underlying process (for diagnostics: PID, arguments).</summary>
    public ClaudeCliProcess? Process => _cli;

    /// <summary>The initialize response, once received.</summary>
    public InitializeResponse? InitializeInfo { get; private set; }

    public event Action<AgentEvent>? EventReceived;

    /// <summary>Every raw protocol line; the flag is true for lines we wrote to stdin.</summary>
    public event Action<string, bool>? RawLine;

    public Task StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default)
    {
        if (_cli is not null)
        {
            throw new InvalidOperationException("The backend was already started.");
        }

        var cliOptions = new ClaudeCliOptions
        {
            CliPath = string.IsNullOrWhiteSpace(options.CliPath) ? "claude" : options.CliPath,
            WorkingDirectory = options.WorkingDirectory,
            Model = options.Model,
            PermissionMode = options.PermissionMode,
            Effort = options.Effort,
            ThinkingDisplay = options.ThinkingDisplay,
            PromptSuggestions = options.PromptSuggestions,
            ResumeSessionId = options.ResumeSessionId,
            ResumeSessionAt = options.ResumeSessionAt,
            ForkSession = options.ForkSession,
            NewSessionId = string.IsNullOrWhiteSpace(options.ResumeSessionId) || options.ForkSession ? Guid.NewGuid().ToString() : null,
            AllowBypassPermissions = options.PermissionMode == "bypassPermissions",
            ExtraArguments = options.ExtraArguments,
        };

        SessionId = options.ForkSession ? null : options.ResumeSessionId;
        var cli = new ClaudeCliProcess(cliOptions);
        cli.StdoutLine += HandleLine;
        cli.Exited += OnExited;
        _cli = cli;
        cli.Start();

        // The SDK always opens with initialize; it also gives us the account and model catalogue.
        _ = InitializeAsync(options.FastMode, options.PromptSuggestions);
        return Task.CompletedTask;
    }

    public async Task SendPromptAsync(string text, string? uuid = null, IReadOnlyList<PromptImage>? images = null, CancellationToken cancellationToken = default)
    {
        var cli = RequireRunning();

        // The replayed copy comes back with this uuid, which tells it apart from Remote Control prompts.
        uuid ??= Guid.NewGuid().ToString();
        _sentUuids[uuid] = 0;
        _turnRunning = true;
        _interruptRequested = false;
        _assistantError = null;
        Raise(new TurnStartedEvent(text));
        try
        {
            await WriteAsync(cli, CliProtocol.UserMessage(text, uuid, images), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _turnRunning = false;
            throw;
        }
    }

    public async Task RespondAsync(PermissionRequest request, PermissionDecision decision, CancellationToken cancellationToken = default)
    {
        var cli = RequireRunning();
        if (!_pendingPermissions.TryRemove(request.RequestId, out _))
        {
            return; // Already answered or withdrawn by the CLI.
        }

        PermissionResultPayload payload;
        if (decision.Choice == PermissionChoice.Deny)
        {
            payload = new PermissionResultPayload
            {
                Behavior = "deny",
                Message = decision.DenyMessage ?? "The user denied this action.",
                ToolUseId = request.ToolUseId,
                DecisionClassification = "user_reject",
            };
        }
        else
        {
            payload = new PermissionResultPayload
            {
                Behavior = "allow",
                UpdatedInput = decision.UpdatedInput ?? request.Input,
                ToolUseId = request.ToolUseId,
                DecisionClassification = decision.Choice == PermissionChoice.AllowForSession ? "user_permanent" : "user_temporary",
            };

            if (decision.Choice == PermissionChoice.AllowForSession)
            {
                payload.UpdatedPermissions = SessionPermissionUpdates(request);
            }
        }

        await WriteAsync(cli, CliProtocol.PermissionResponse(request.RequestId, payload), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// "Allow for session": the CLI's own suggestions scoped to this session (so nothing is written to
    /// settings files), or an allow rule for the whole tool when the CLI suggested nothing.
    /// </summary>
    internal static List<JsonElement> SessionPermissionUpdates(PermissionRequest request)
    {
        var updates = new List<JsonElement>();
        if (request.Suggestions.ValueKind == JsonValueKind.Array)
        {
            foreach (var suggestion in request.Suggestions.EnumerateArray())
            {
                if (suggestion.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (var property in suggestion.EnumerateObject())
                    {
                        if (property.NameEquals("destination"))
                        {
                            continue;
                        }

                        property.WriteTo(writer);
                    }

                    writer.WriteString("destination", "session");
                    writer.WriteEndObject();
                }

                updates.Add(JsonDocument.Parse(buffer.ToArray()).RootElement.Clone());
            }
        }

        if (updates.Count == 0)
        {
            var rule = new PermissionRulesUpdate { Rules = [new PermissionRuleValue { ToolName = request.ToolName }] };
            var json = JsonSerializer.Serialize(rule, ProtocolJsonContext.Default.PermissionRulesUpdate);
            updates.Add(JsonDocument.Parse(json).RootElement.Clone());
        }

        return updates;
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_cli is not { IsRunning: true } || !_turnRunning)
        {
            return;
        }

        _interruptRequested = true;
        await SendControlAsync(new OutgoingControlPayload { Subtype = "interrupt" }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetModelAsync(string model, CancellationToken cancellationToken = default)
    {
        if (_cli is not { IsRunning: true })
        {
            return false;
        }

        var response = await SendControlAsync(new OutgoingControlPayload { Subtype = "set_model", Model = model }, cancellationToken).ConfigureAwait(false);
        if (response?.Subtype == "success")
        {
            Raise(new ModelChangedEvent(model));
            return true;
        }

        return false;
    }

    public async Task<bool> SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (_cli is not { IsRunning: true })
        {
            return false;
        }

        var response = await SendControlAsync(new OutgoingControlPayload { Subtype = "set_permission_mode", Mode = mode }, cancellationToken).ConfigureAwait(false);
        if (response?.Subtype == "success")
        {
            Raise(new PermissionModeChangedEvent(mode));
            return true;
        }

        return false;
    }

    public async Task<bool> SetEffortAsync(string? effort, CancellationToken cancellationToken = default)
    {
        if (_cli is not { IsRunning: true } || string.IsNullOrEmpty(effort))
        {
            return false;
        }

        var payload = new OutgoingControlPayload { Subtype = "apply_flag_settings", Settings = new FlagSettings { EffortLevel = effort } };
        var response = await SendControlAsync(payload, cancellationToken).ConfigureAwait(false);
        return response?.Subtype == "success";
    }

    public async Task<bool> SetFastModeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_cli is not { IsRunning: true })
        {
            return false;
        }

        var payload = new OutgoingControlPayload { Subtype = "apply_flag_settings", Settings = new FlagSettings { FastMode = enabled } };
        var response = await SendControlAsync(payload, cancellationToken).ConfigureAwait(false);
        return response?.Subtype == "success";
    }

    public async Task<string?> GenerateTitleAsync(string description, bool persist, CancellationToken cancellationToken = default)
    {
        var payload = new OutgoingControlPayload { Subtype = "generate_session_title", Description = description, Persist = persist };
        var response = await SendControlAsync(payload, cancellationToken, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        return response is { Subtype: "success", Response.ValueKind: JsonValueKind.Object } &&
            response.Response.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(title.GetString())
            ? title.GetString()!.Trim()
            : null;
    }

    public async Task<RemoteControlInfo?> SetRemoteControlAsync(bool enabled, string? name, string? reattachSessionId = null, CancellationToken cancellationToken = default)
    {
        if (_cli is not { IsRunning: true })
        {
            return null;
        }

        var payload = new OutgoingControlPayload
        {
            Subtype = "remote_control",
            Enabled = enabled,
            Name = enabled && !string.IsNullOrWhiteSpace(name) ? name : null,
            KeepSessionOnExit = enabled ? true : null,
            ReattachSessionId = enabled ? reattachSessionId : null,
        };
        var response = await SendControlAsync(payload, cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (response?.Subtype != "success")
        {
            return null;
        }

        if (!enabled || response.Response.ValueKind != JsonValueKind.Object)
        {
            return new RemoteControlInfo(null, null);
        }

        return new RemoteControlInfo(String(response.Response, "session_url"), String(response.Response, "bridge_session_id"));

        static string? String(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public async Task StopAsync()
    {
        _stopping = true;
        if (_cli is not null)
        {
            await _cli.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_cli is not null)
        {
            await _cli.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- outgoing

    private async Task InitializeAsync(bool fastMode, bool promptSuggestions)
    {
        var response = await SendControlAsync(new OutgoingControlPayload { Subtype = "initialize", PromptSuggestions = promptSuggestions ? true : null }, CancellationToken.None).ConfigureAwait(false);
        if (response is { Subtype: "success", Response.ValueKind: JsonValueKind.Object })
        {
            try
            {
                InitializeInfo = response.Response.Deserialize(ProtocolJsonContext.Default.InitializeResponse);
            }
            catch (JsonException)
            {
            }

            if (InitializeInfo is not null)
            {
                Raise(new AccountInfoEvent(
                    InitializeInfo.Account?.SubscriptionType,
                    InitializeInfo.Account?.Email,
                    InitializeInfo.Models ?? [],
                    InitializeInfo.Commands));
            }
        }

        if (fastMode)
        {
            await SetFastModeAsync(true).ConfigureAwait(false);
        }
    }

    private async Task<ControlResponseBody?> SendControlAsync(OutgoingControlPayload payload, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var cli = _cli;
        if (cli is not { IsRunning: true })
        {
            return null;
        }

        var requestId = $"nomad-{Interlocked.Increment(ref _requestCounter)}";
        var tcs = new TaskCompletionSource<ControlResponseBody?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingControl[requestId] = tcs;
        try
        {
            await WriteAsync(cli, CliProtocol.ControlRequest(requestId, payload), cancellationToken).ConfigureAwait(false);
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? ControlTimeout, cancellationToken)).ConfigureAwait(false);
            return finished == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingControl.TryRemove(requestId, out _);
        }
    }

    private async Task WriteAsync(ClaudeCliProcess cli, string line, CancellationToken cancellationToken)
    {
        RawLine?.Invoke(line, true);
        await cli.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
    }

    private ClaudeCliProcess RequireRunning() =>
        _cli is { IsRunning: true } cli ? cli : throw new InvalidOperationException("The Claude Code process is not running.");

    // ---------------------------------------------------------------- incoming

    /// <summary>Handles one stdout line (internal so tests can replay captured transcripts).</summary>
    internal void HandleLine(string line)
    {
        RawLine?.Invoke(line, false);
        var parsed = CliProtocol.ParseLine(line);
        switch (parsed)
        {
            case SystemLine system:
                HandleSystem(system);
                break;
            case StreamEventLine streamEvent:
                HandleStreamEvent(streamEvent);
                break;
            case AssistantLine assistant:
                HandleAssistant(assistant);
                break;
            case UserLine user:
                HandleUser(user);
                break;
            case ResultLine result:
                HandleResult(result);
                break;
            case ControlRequestLine request:
                HandleControlRequest(request);
                break;
            case ControlResponseLine response:
                if (response.Response?.RequestId is { } id && _pendingControl.TryGetValue(id, out var tcs))
                {
                    tcs.TrySetResult(response.Response);
                }

                break;
            case PromptSuggestionLine { Suggestion: { Length: > 0 } suggestion }:
                Raise(new PromptSuggestionEvent(suggestion.Trim()));
                break;
            case ControlCancelLine cancel:
                if (cancel.RequestId is { } cancelled && _pendingPermissions.TryRemove(cancelled, out _))
                {
                    Raise(new PermissionCancelledEvent(cancelled));
                }

                break;
        }
    }

    private void HandleSystem(SystemLine system)
    {
        switch (system.Subtype)
        {
            case "init":
                if (!string.IsNullOrEmpty(system.SessionId))
                {
                    SessionId = system.SessionId;
                }

                Raise(new SessionStartedEvent(
                    system.SessionId ?? SessionId ?? "",
                    system.Model,
                    system.Cwd,
                    system.PermissionMode,
                    system.Tools ?? [],
                    system.ClaudeCodeVersion,
                    system.McpServers?.Where(s => !string.IsNullOrEmpty(s.Name)).Select(s => new McpServerInfo(s.Name!, s.Status ?? "")).ToList()));
                break;
            case "status":
                if (!string.IsNullOrEmpty(system.PermissionMode))
                {
                    Raise(new PermissionModeChangedEvent(system.PermissionMode));
                }

                Raise(new StatusEvent(system.Status));
                break;
            case "bridge_state" when !string.IsNullOrEmpty(system.State):
                Raise(new RemoteControlStateEvent(system.State));
                break;
        }
    }

    private void HandleStreamEvent(StreamEventLine line)
    {
        var e = line.Event;
        if (e is null)
        {
            return;
        }

        var parent = line.ParentToolUseId;
        switch (e.Type)
        {
            case "message_start":
                _messageId = e.Message?.Id ?? $"local-{++_syntheticMessageCounter}";
                _openBlockIndex = -1;
                Raise(new AssistantMessageStartedEvent(_messageId, e.Message?.Model, parent));
                break;
            case "content_block_start" when _messageId is not null && e.Index is { } index:
                _openBlockIndex = index;
                var block = e.ContentBlock;
                Raise(new BlockStartedEvent(_messageId, index, KindOf(block?.Type), block?.Id, block?.Name, parent));
                break;
            case "content_block_delta" when _messageId is not null && e.Index is { } index && e.Delta is { } delta:
                if (delta.Type == "text_delta" && !string.IsNullOrEmpty(delta.Text))
                {
                    Raise(new TextDeltaEvent(_messageId, index, delta.Text, parent));
                }
                else if (delta.Type == "thinking_delta" && !string.IsNullOrEmpty(delta.Thinking))
                {
                    Raise(new ThinkingDeltaEvent(_messageId, index, delta.Thinking, parent));
                }

                break;
        }
    }

    private void HandleAssistant(AssistantLine line)
    {
        var message = line.Message;
        if (message?.Content is not { Count: > 0 } content)
        {
            return;
        }

        if (line.Error is not null)
        {
            // An API failure turned into a synthetic assistant message: surface it with the result.
            _assistantError = string.Join("\n", content.Where(b => b.Type == "text").Select(b => b.Text));
            if (string.IsNullOrWhiteSpace(_assistantError))
            {
                _assistantError = line.Error;
            }

            return;
        }

        var messageId = message.Id ?? _messageId ?? $"local-{++_syntheticMessageCounter}";
        var streamed = messageId == _messageId && _openBlockIndex >= 0 && content.Count == 1;
        for (var i = 0; i < content.Count; i++)
        {
            var block = content[i];
            var index = streamed ? _openBlockIndex : i;
            var kind = KindOf(block.Type);
            switch (kind)
            {
                case AgentBlockKind.Text:
                    Raise(new BlockCompletedEvent(messageId, index, kind, block.Text ?? "", line.ParentToolUseId));
                    break;
                case AgentBlockKind.Thinking:
                    Raise(new BlockCompletedEvent(messageId, index, kind, block.Thinking ?? "", line.ParentToolUseId));
                    break;
                case AgentBlockKind.ToolUse when block.Id is not null:
                    Raise(new ToolCallEvent(block.Id, block.Name ?? "tool", block.Input, line.ParentToolUseId));
                    Raise(new BlockCompletedEvent(messageId, index, kind, null, line.ParentToolUseId));
                    break;
            }
        }
    }

    private void HandleUser(UserLine line)
    {
        if (line.Message?.Content is not { } content)
        {
            return;
        }

        // A replayed prompt we did not send came from Remote Control: it starts a turn of its own.
        if (line.IsReplay == true && line.ParentToolUseId is null && content.All(b => b.Type == "text")
            && !(line.Uuid is { } uuid && _sentUuids.ContainsKey(uuid)))
        {
            var text = string.Join("\n", content.Select(b => b.Text).Where(t => !string.IsNullOrEmpty(t)));
            // A slash command's echo (its name, a caveat, its output) starts no turn.
            if (LocalCommandEcho.TryParse(text, out var output))
            {
                if (output is not null)
                {
                    Raise(new CommandOutputEvent(output));
                }
            }
            else if (text.Length > 0)
            {
                _turnRunning = true;
                _interruptRequested = false;
                _assistantError = null;
                Raise(new RemotePromptEvent(text, line.Uuid));
            }

            return;
        }

        foreach (var block in content)
        {
            if (block.Type == "tool_result" && block.ToolUseId is not null)
            {
                Raise(new ToolResultEvent(
                    block.ToolUseId,
                    block.IsError == true,
                    CliProtocol.ToolResultText(block.Content),
                    line.ToolUseResult,
                    line.ParentToolUseId));
            }
        }
    }

    private void HandleResult(ResultLine line)
    {
        _turnRunning = false;
        foreach (var id in _pendingPermissions.Keys)
        {
            if (_pendingPermissions.TryRemove(id, out _))
            {
                Raise(new PermissionCancelledEvent(id));
            }
        }

        if (!string.IsNullOrEmpty(line.SessionId))
        {
            SessionId = line.SessionId;
        }

        var usage = line.Usage;

        // An interrupted turn ends with error_during_execution / terminal_reason "aborted_streaming"
        // (or "aborted_tools"); that is not an error from the user's point of view.
        var interrupted = (_interruptRequested && line.Subtype != "success")
            || line.TerminalReason?.StartsWith("aborted", StringComparison.Ordinal) == true;
        string? error = null;
        if (!interrupted && (line.IsError || line.Subtype != "success" || _assistantError is not null))
        {
            error = _assistantError
                ?? (line.Errors is { Count: > 0 } errors ? string.Join("\n", errors) : null)
                ?? (string.IsNullOrWhiteSpace(line.Result) ? $"The turn ended with {line.Subtype}." : line.Result);
        }

        Raise(new TurnCompletedEvent(new TurnResult
        {
            Subtype = line.Subtype ?? "success",
            IsError = error is not null,
            Interrupted = interrupted,
            DurationMs = line.DurationMs ?? 0,
            NumTurns = line.NumTurns ?? 0,
            ResultText = line.Result,
            ErrorMessage = error,
            CostUsd = line.TotalCostUsd ?? 0,
            InputTokens = usage?.InputTokens ?? 0,
            OutputTokens = usage?.OutputTokens ?? 0,
            CacheReadTokens = usage?.CacheReadInputTokens ?? 0,
            CacheCreationTokens = usage?.CacheCreationInputTokens ?? 0,
        }));

        _interruptRequested = false;
        _assistantError = null;
    }

    private void HandleControlRequest(ControlRequestLine line)
    {
        var requestId = line.RequestId;
        var body = line.Request;
        if (requestId is null || body is null)
        {
            return;
        }

        if (body.Subtype == "can_use_tool" && body.ToolName is not null)
        {
            var request = new PermissionRequest
            {
                RequestId = requestId,
                ToolName = body.ToolName,
                DisplayName = body.DisplayName,
                Title = body.Title,
                Description = body.Description,
                Input = body.Input,
                ToolUseId = body.ToolUseId,
                DecisionReason = body.DecisionReason,
                BlockedPath = body.BlockedPath,
                Suggestions = body.PermissionSuggestions,
                SuppressAllowForSession = body.SuppressAlwaysAllowRule == true,
            };
            _pendingPermissions[requestId] = request;
            Raise(new PermissionRequestedEvent(request));
            return;
        }

        // Hooks, MCP tunnels, dialogs, …: not offered by Nomad. Answer so the CLI does not wait.
        var cli = _cli;
        if (cli is not null)
        {
            _ = WriteSafeAsync(cli, CliProtocol.ErrorResponse(requestId, $"Unsupported control request subtype: {body.Subtype}"));
        }
    }

    private async Task WriteSafeAsync(ClaudeCliProcess cli, string line)
    {
        try
        {
            await WriteAsync(cli, line, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Raise(new AgentErrorEvent(ex.Message));
        }
    }

    private void OnExited(int exitCode)
    {
        foreach (var pending in _pendingControl.Values)
        {
            pending.TrySetResult(null);
        }

        _pendingPermissions.Clear();
        var stderr = _cli?.StderrTail ?? "";
        if (_turnRunning)
        {
            _turnRunning = false;
            var message = string.IsNullOrWhiteSpace(stderr)
                ? $"Claude Code exited unexpectedly (exit code {exitCode})."
                : $"Claude Code exited unexpectedly (exit code {exitCode}):\n{stderr}";
            Raise(new TurnCompletedEvent(new TurnResult { Subtype = "process_exited", IsError = true, ErrorMessage = message }));
        }

        Raise(new ProcessExitedEvent(exitCode, _stopping, stderr));
    }

    private static AgentBlockKind KindOf(string? type) => type switch
    {
        "text" => AgentBlockKind.Text,
        "thinking" or "redacted_thinking" => AgentBlockKind.Thinking,
        "tool_use" or "server_tool_use" => AgentBlockKind.ToolUse,
        _ => AgentBlockKind.Other,
    };

    private void Raise(AgentEvent e)
    {
        try
        {
            EventReceived?.Invoke(e);
        }
        catch (Exception)
        {
            // A faulty subscriber must not kill the protocol pump.
        }
    }
}
