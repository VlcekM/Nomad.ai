using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nomad.Core.Protocol;

// Shapes of the CLI's stream-json protocol (as emitted by claude 2.1.x). Only the fields Nomad
// uses are declared; everything else is ignored by the deserializer, so newer CLIs that add
// fields keep working. Free-form parts (tool inputs, tool results) stay JsonElement.

/// <summary>One content block of an API message: text, thinking, tool_use, tool_result, image.</summary>
public sealed class WireContentBlock
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("thinking")] public string? Thinking { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("input")] public JsonElement Input { get; set; }
    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; set; }

    /// <summary>tool_result content: a string or an array of text/image blocks.</summary>
    [JsonPropertyName("content")] public JsonElement Content { get; set; }

    [JsonPropertyName("is_error")] public bool? IsError { get; set; }
}

/// <summary>An API message (assistant reply or user turn).</summary>
public sealed class WireMessage
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }

    /// <summary>Either a plain string or an array of blocks on the wire; always a list here.</summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(ContentBlockListConverter))]
    public List<WireContentBlock>? Content { get; set; }

    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("usage")] public WireUsage? Usage { get; set; }
}

public sealed class WireUsage
{
    [JsonPropertyName("input_tokens")] public long? InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public long? OutputTokens { get; set; }
    [JsonPropertyName("cache_creation_input_tokens")] public long? CacheCreationInputTokens { get; set; }
    [JsonPropertyName("cache_read_input_tokens")] public long? CacheReadInputTokens { get; set; }
}

/// <summary>A raw Anthropic API streaming event (inside a <c>stream_event</c> line).</summary>
public sealed class WireStreamEvent
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("index")] public int? Index { get; set; }
    [JsonPropertyName("message")] public WireMessage? Message { get; set; }
    [JsonPropertyName("content_block")] public WireContentBlock? ContentBlock { get; set; }
    [JsonPropertyName("delta")] public WireDelta? Delta { get; set; }
    [JsonPropertyName("usage")] public WireUsage? Usage { get; set; }
}

public sealed class WireDelta
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("thinking")] public string? Thinking { get; set; }
    [JsonPropertyName("partial_json")] public string? PartialJson { get; set; }
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
}

/// <summary>Base of every stdout line.</summary>
public abstract class CliLine
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
}

/// <summary><c>system</c> lines: <c>init</c>, <c>status</c>, and others we mostly ignore.</summary>
public sealed class SystemLine : CliLine
{
    [JsonPropertyName("subtype")] public string? Subtype { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("permissionMode")] public string? PermissionMode { get; set; }
    [JsonPropertyName("tools")] public List<string>? Tools { get; set; }
    [JsonPropertyName("claude_code_version")] public string? ClaudeCodeVersion { get; set; }
    [JsonPropertyName("fast_mode_state")] public string? FastModeState { get; set; }
    [JsonPropertyName("effort")] public string? Effort { get; set; }

    /// <summary>For subtype <c>status</c>: requesting | compacting | null.</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }

    /// <summary>For subtype <c>bridge_state</c>: the Remote Control bridge's state.</summary>
    [JsonPropertyName("state")] public string? State { get; set; }
}

public sealed class StreamEventLine : CliLine
{
    [JsonPropertyName("event")] public WireStreamEvent? Event { get; set; }
    [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; set; }
}

public sealed class AssistantLine : CliLine
{
    [JsonPropertyName("message")] public WireMessage? Message { get; set; }
    [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; set; }

    /// <summary>Set when the CLI turned an API failure into a synthetic assistant message.</summary>
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>User-role lines: mostly tool results the CLI feeds back to the model.</summary>
public sealed class UserLine : CliLine
{
    [JsonPropertyName("message")] public WireMessage? Message { get; set; }
    [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; set; }

    /// <summary>Structured tool output (e.g. Edit's structuredPatch), shape depends on the tool.</summary>
    [JsonPropertyName("tool_use_result")] public JsonElement ToolUseResult { get; set; }

    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    [JsonPropertyName("isReplay")] public bool? IsReplay { get; set; }
    [JsonPropertyName("isSynthetic")] public bool? IsSynthetic { get; set; }
}

/// <summary>The final line of a turn.</summary>
public sealed class ResultLine : CliLine
{
    [JsonPropertyName("subtype")] public string? Subtype { get; set; }
    [JsonPropertyName("is_error")] public bool IsError { get; set; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; set; }
    [JsonPropertyName("duration_api_ms")] public long? DurationApiMs { get; set; }
    [JsonPropertyName("num_turns")] public int? NumTurns { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("terminal_reason")] public string? TerminalReason { get; set; }
    [JsonPropertyName("total_cost_usd")] public double? TotalCostUsd { get; set; }
    [JsonPropertyName("usage")] public WireUsage? Usage { get; set; }
    [JsonPropertyName("errors")] public List<string>? Errors { get; set; }
    [JsonPropertyName("api_error_status")] public int? ApiErrorStatus { get; set; }
}

/// <summary>A request from the CLI to us (permission prompts, hook callbacks, …).</summary>
public sealed class ControlRequestLine : CliLine
{
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("request")] public ControlRequestBody? Request { get; set; }
}

public sealed class ControlRequestBody
{
    [JsonPropertyName("subtype")] public string? Subtype { get; set; }
    [JsonPropertyName("tool_name")] public string? ToolName { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("input")] public JsonElement Input { get; set; }
    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; set; }
    [JsonPropertyName("agent_id")] public string? AgentId { get; set; }
    [JsonPropertyName("permission_suggestions")] public JsonElement PermissionSuggestions { get; set; }
    [JsonPropertyName("blocked_path")] public string? BlockedPath { get; set; }
    [JsonPropertyName("decision_reason")] public string? DecisionReason { get; set; }
    [JsonPropertyName("decision_reason_type")] public string? DecisionReasonType { get; set; }
    [JsonPropertyName("suppress_always_allow_rule")] public bool? SuppressAlwaysAllowRule { get; set; }
    [JsonPropertyName("requires_user_interaction")] public bool? RequiresUserInteraction { get; set; }
}

/// <summary>The CLI's answer to one of our control requests.</summary>
public sealed class ControlResponseLine : CliLine
{
    [JsonPropertyName("response")] public ControlResponseBody? Response { get; set; }
}

public sealed class ControlResponseBody
{
    [JsonPropertyName("subtype")] public string? Subtype { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("response")] public JsonElement Response { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>The CLI withdrew an earlier control request (e.g. a permission prompt after an interrupt).</summary>
public sealed class ControlCancelLine : CliLine
{
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
}

/// <summary>Any line type Nomad does not model (rate_limit_event, keep_alive, …).</summary>
public sealed class UnknownLine : CliLine
{
    [JsonIgnore] public string Raw { get; set; } = "";
}

// ---- initialize response payload (subset) ----

public sealed class InitializeResponse
{
    [JsonPropertyName("models")] public List<ModelInfo>? Models { get; set; }
    [JsonPropertyName("account")] public AccountInfo? Account { get; set; }
    [JsonPropertyName("output_style")] public string? OutputStyle { get; set; }
}

public sealed class ModelInfo
{
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("supportsEffort")] public bool? SupportsEffort { get; set; }
    [JsonPropertyName("supportedEffortLevels")] public List<string>? SupportedEffortLevels { get; set; }
    [JsonPropertyName("supportsFastMode")] public bool? SupportsFastMode { get; set; }
}

public sealed class AccountInfo
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("organization")] public string? Organization { get; set; }
    [JsonPropertyName("subscriptionType")] public string? SubscriptionType { get; set; }
    [JsonPropertyName("apiProvider")] public string? ApiProvider { get; set; }
}
