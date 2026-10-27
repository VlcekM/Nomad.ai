using Nomad.Core.Protocol;

namespace Nomad.Core.Agents;

/// <summary>
/// One live agent session (one process). Events are raised on a background thread, in protocol
/// order; UI code must marshal them to its own thread.
/// </summary>
public interface IAgentBackend : IAsyncDisposable
{
    /// <summary>Human-readable backend name, e.g. "Claude Code".</summary>
    string DisplayName { get; }

    /// <summary>The session id once known (from the agent or the resume id).</summary>
    string? SessionId { get; }

    bool IsProcessRunning { get; }

    bool IsTurnRunning { get; }

    event Action<AgentEvent>? EventReceived;

    /// <summary>Starts the agent process. Throws when the agent executable cannot be started.</summary>
    Task StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default);

    Task SendPromptAsync(string text, string? uuid = null, IReadOnlyList<PromptImage>? images = null, CancellationToken cancellationToken = default);

    Task RespondAsync(PermissionRequest request, PermissionDecision decision, CancellationToken cancellationToken = default);

    /// <summary>Stops the running turn (Esc / stop button).</summary>
    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>Switches the model live. False when the running process cannot do it.</summary>
    Task<bool> SetModelAsync(string model, CancellationToken cancellationToken = default);

    Task<bool> SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default);

    Task<bool> SetEffortAsync(string? effort, CancellationToken cancellationToken = default);

    Task<bool> SetFastModeAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Asks the agent for a short session title; <paramref name="persist"/> stores it with the session.</summary>
    Task<string?> GenerateTitleAsync(string description, bool persist, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns Remote Control on or off, so the session can be driven from claude.ai or the Claude
    /// mobile app. <paramref name="reattachSessionId"/> continues an earlier process's link. Null when
    /// the process could not do it.
    /// </summary>
    Task<RemoteControlInfo?> SetRemoteControlAsync(bool enabled, string? name, string? reattachSessionId = null, CancellationToken cancellationToken = default);

    /// <summary>Ends the process gracefully (closes stdin), killing it after a grace period.</summary>
    Task StopAsync();
}
