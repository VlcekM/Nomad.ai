using System.Text.Json;
using Nomad.Core.Protocol;

namespace Nomad.Core.Agents;

// Backend-neutral event stream. A Claude Code, Codex or OpenCode adapter translates its own
// protocol into these; the UI only ever sees these.

public enum AgentBlockKind
{
    Text,
    Thinking,
    ToolUse,
    Other,
}

public abstract record AgentEvent;

/// <summary>The agent process announced its session (Claude Code: <c>system/init</c>).</summary>
public sealed record SessionStartedEvent(
    string SessionId,
    string? Model,
    string? WorkingDirectory,
    string? PermissionMode,
    IReadOnlyList<string> Tools,
    string? AgentVersion,
    IReadOnlyList<McpServerInfo>? McpServers = null) : AgentEvent;

/// <summary>Account and model catalogue, when the backend reports them.</summary>
public sealed record AccountInfoEvent(string? SubscriptionType, string? Email, IReadOnlyList<ModelInfo> Models, IReadOnlyList<SlashCommand>? Commands = null) : AgentEvent;

/// <summary>We sent a prompt; a turn is now running.</summary>
public sealed record TurnStartedEvent(string Prompt) : AgentEvent;

/// <summary>A prompt typed elsewhere (Remote Control on claude.ai or the phone); a turn is now running.</summary>
public sealed record RemotePromptEvent(string Text, string? Uuid) : AgentEvent;

/// <summary>The Remote Control bridge's state: ready | connected | reconnecting | failed | disconnected.</summary>
public sealed record RemoteControlStateEvent(string State) : AgentEvent;

/// <summary>What a slash command printed ("Set model to …"), from its echo: shown as a notice.</summary>
public sealed record CommandOutputEvent(string Text) : AgentEvent;

/// <summary>The CLI's guess at the next prompt, after a turn: offered as ghost text in the composer.</summary>
public sealed record PromptSuggestionEvent(string Text) : AgentEvent;

/// <summary>A session's Remote Control link (claude.ai/code/session_…) and its bridge session id, for reattaching.</summary>
public sealed record RemoteControlInfo(string? SessionUrl, string? BridgeSessionId);

public sealed record AssistantMessageStartedEvent(string MessageId, string? Model, string? ParentToolUseId) : AgentEvent;

public sealed record BlockStartedEvent(
    string MessageId,
    int Index,
    AgentBlockKind Kind,
    string? ToolUseId,
    string? ToolName,
    string? ParentToolUseId) : AgentEvent;

public sealed record TextDeltaEvent(string MessageId, int Index, string Text, string? ParentToolUseId) : AgentEvent;

public sealed record ThinkingDeltaEvent(string MessageId, int Index, string Text, string? ParentToolUseId) : AgentEvent;

/// <summary>A content block is final. <paramref name="Text"/> is the full text for text/thinking blocks.</summary>
public sealed record BlockCompletedEvent(
    string MessageId,
    int Index,
    AgentBlockKind Kind,
    string? Text,
    string? ParentToolUseId) : AgentEvent;

/// <summary>A tool call with its complete input.</summary>
public sealed record ToolCallEvent(string ToolUseId, string Name, JsonElement Input, string? ParentToolUseId) : AgentEvent;

/// <summary>A tool finished. <paramref name="Structured"/> is the tool's structured output, if any.</summary>
public sealed record ToolResultEvent(
    string ToolUseId,
    bool IsError,
    string Text,
    JsonElement Structured,
    string? ParentToolUseId) : AgentEvent;

public sealed record PermissionRequestedEvent(PermissionRequest Request) : AgentEvent;

/// <summary>The agent withdrew a pending permission request (e.g. after an interrupt).</summary>
public sealed record PermissionCancelledEvent(string RequestId) : AgentEvent;

public sealed record PermissionModeChangedEvent(string Mode) : AgentEvent;

public sealed record ModelChangedEvent(string Model) : AgentEvent;

/// <summary>Coarse status: "requesting", "compacting", or null when idle.</summary>
public sealed record StatusEvent(string? Status) : AgentEvent;

public sealed record TurnCompletedEvent(TurnResult Result) : AgentEvent;

/// <summary>A problem outside the normal turn flow (failed write, unsupported request, …).</summary>
public sealed record AgentErrorEvent(string Message) : AgentEvent;

public sealed record ProcessExitedEvent(int ExitCode, bool Expected, string StderrTail) : AgentEvent;

/// <summary>Summary of a finished turn.</summary>
public sealed record TurnResult
{
    public string Subtype { get; init; } = "success";
    public bool IsError { get; init; }
    public bool Interrupted { get; init; }
    public long DurationMs { get; init; }
    public int NumTurns { get; init; }
    public string? ResultText { get; init; }
    public string? ErrorMessage { get; init; }
    public double CostUsd { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }

    /// <summary>
    /// The context in use at the end of the turn: what its last main-conversation API call read. The fields above add
    /// up every call of the turn, so they count the conversation again for each tool round.
    /// </summary>
    public long ContextTokens { get; init; }

    /// <summary>Tokens processed this turn (input incl. cache + output).</summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>A tool call waiting for the user's decision.</summary>
public sealed record PermissionRequest
{
    public required string RequestId { get; init; }
    public required string ToolName { get; init; }
    public string? DisplayName { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public JsonElement Input { get; init; }
    public string? ToolUseId { get; init; }
    public string? DecisionReason { get; init; }
    public string? BlockedPath { get; init; }

    /// <summary>The CLI's suggested permission updates (array of PermissionUpdate) or undefined.</summary>
    public JsonElement Suggestions { get; init; }

    /// <summary>When true, do not offer "Allow for session".</summary>
    public bool SuppressAllowForSession { get; init; }
}

public enum PermissionChoice
{
    Allow,
    AllowForSession,
    Deny,
}

/// <summary>
/// The user's answer. <paramref name="UpdatedInput"/> replaces the tool input on allow (used by
/// AskUserQuestion to carry the answers); otherwise the original input is echoed back.
/// </summary>
public sealed record PermissionDecision(PermissionChoice Choice, string? DenyMessage = null, JsonElement? UpdatedInput = null);

/// <summary>How to start an agent process for a session.</summary>
public sealed record AgentStartOptions
{
    public required string WorkingDirectory { get; init; }
    public string? Model { get; init; }
    public string? PermissionMode { get; init; }
    public string? Effort { get; init; }
    public string? ThinkingDisplay { get; init; }

    /// <summary>Ask the CLI for a guess at the next prompt after each turn.</summary>
    public bool PromptSuggestions { get; init; }
    public bool FastMode { get; init; }
    public string? ResumeSessionId { get; init; }

    /// <summary>Resume only up to and including this transcript record (rewind, fork from a message).</summary>
    public string? ResumeSessionAt { get; init; }

    /// <summary>Resume into a new session id, leaving the original as it is (fork).</summary>
    public bool ForkSession { get; init; }

    public string? CliPath { get; init; }
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
