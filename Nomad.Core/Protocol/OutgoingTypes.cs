using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nomad.Core.Protocol;

// Lines Nomad writes to the CLI's stdin.

/// <summary><c>{"type":"user","message":{...},"parent_tool_use_id":null,"session_id":""}</c></summary>
public sealed class OutgoingUserMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "user";
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";

    /// <summary>The message's transcript uuid; the CLI keeps it, so rewind and fork can point at it.</summary>
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    [JsonPropertyName("message")] public OutgoingMessageBody Message { get; set; } = new();

    [JsonPropertyName("parent_tool_use_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ParentToolUseId { get; set; }
}

public sealed class OutgoingMessageBody
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public List<OutgoingTextBlock> Content { get; set; } = [];
}

/// <summary>A prompt's content block: <c>text</c>, or <c>image</c> with a base64 <see cref="Source"/>.</summary>
public sealed class OutgoingTextBlock
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("source")] public OutgoingImageSource? Source { get; set; }
}

public sealed class OutgoingImageSource
{
    [JsonPropertyName("type")] public string Type { get; set; } = "base64";
    [JsonPropertyName("media_type")] public string MediaType { get; set; } = "image/png";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}

/// <summary>An image attached to a prompt: its encoded bytes and their media type (image/png, image/jpeg, …).</summary>
public sealed record PromptImage(byte[] Data, string MediaType);

/// <summary>A client → CLI control request (initialize, interrupt, set_model, …).</summary>
public sealed class OutgoingControlRequest
{
    [JsonPropertyName("type")] public string Type { get; set; } = "control_request";
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    [JsonPropertyName("request")] public OutgoingControlPayload Request { get; set; } = new();
}

public sealed class OutgoingControlPayload
{
    [JsonPropertyName("subtype")] public string Subtype { get; set; } = "";
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("settings")] public FlagSettings? Settings { get; set; }

    /// <summary><c>stop_task</c>: the background task to stop (a workflow run's, say).</summary>
    [JsonPropertyName("task_id")] public string? TaskId { get; set; }

    /// <summary><c>generate_session_title</c>: what the session is about (the first prompt).</summary>
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary><c>generate_session_title</c>: also store the title in the transcript.</summary>
    [JsonPropertyName("persist")] public bool? Persist { get; set; }

    /// <summary><c>remote_control</c>: turn the session's Remote Control bridge on or off.</summary>
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

    /// <summary><c>remote_control</c>: the name shown on claude.ai and in the mobile app.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary><c>remote_control</c>: keep the claude.ai session when the process ends, so it can be reattached.</summary>
    [JsonPropertyName("keep_session_on_exit")] public bool? KeepSessionOnExit { get; set; }

    /// <summary><c>remote_control</c>: the bridge session of an earlier process to continue (same link).</summary>
    [JsonPropertyName("reattach_session_id")] public string? ReattachSessionId { get; set; }

    /// <summary><c>initialize</c>: a <c>prompt_suggestion</c> line after each turn (the flag alone is overridden by it).</summary>
    [JsonPropertyName("promptSuggestions")] public bool? PromptSuggestions { get; set; }
}

/// <summary>Session-scoped settings overlay for <c>apply_flag_settings</c>.</summary>
public sealed class FlagSettings
{
    [JsonPropertyName("effortLevel")] public string? EffortLevel { get; set; }
    [JsonPropertyName("fastMode")] public bool? FastMode { get; set; }

    /// <summary>Lets Claude start workflows (multi-agent runs) without being asked to in the prompt.</summary>
    [JsonPropertyName("ultracode")] public bool? Ultracode { get; set; }

    /// <summary>How many agents a workflow should keep under: "small" (under 5), "medium" (under 10, the default), "large" (under 50), "unrestricted".</summary>
    [JsonPropertyName("workflowSizeGuideline")] public string? WorkflowSizeGuideline { get; set; }
}

/// <summary>Our answer to a CLI control request.</summary>
public sealed class OutgoingControlResponse
{
    [JsonPropertyName("type")] public string Type { get; set; } = "control_response";
    [JsonPropertyName("response")] public OutgoingControlResponseBody Response { get; set; } = new();
}

public sealed class OutgoingControlResponseBody
{
    [JsonPropertyName("subtype")] public string Subtype { get; set; } = "success";
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    [JsonPropertyName("response")] public PermissionResultPayload? Response { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>The <c>PermissionResult</c> of a <c>can_use_tool</c> request.</summary>
public sealed class PermissionResultPayload
{
    [JsonPropertyName("behavior")] public string Behavior { get; set; } = "allow";
    [JsonPropertyName("updatedInput")] public JsonElement? UpdatedInput { get; set; }
    [JsonPropertyName("updatedPermissions")] public List<JsonElement>? UpdatedPermissions { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("interrupt")] public bool? Interrupt { get; set; }
    [JsonPropertyName("toolUseID")] public string? ToolUseId { get; set; }
    [JsonPropertyName("decisionClassification")] public string? DecisionClassification { get; set; }
}

/// <summary>A <c>PermissionUpdate</c> of type addRules (what "Allow for session" sends when the CLI made no suggestion).</summary>
public sealed class PermissionRulesUpdate
{
    [JsonPropertyName("type")] public string Type { get; set; } = "addRules";
    [JsonPropertyName("rules")] public List<PermissionRuleValue> Rules { get; set; } = [];
    [JsonPropertyName("behavior")] public string Behavior { get; set; } = "allow";
    [JsonPropertyName("destination")] public string Destination { get; set; } = "session";
}

public sealed class PermissionRuleValue
{
    [JsonPropertyName("toolName")] public string ToolName { get; set; } = "";
    [JsonPropertyName("ruleContent")] public string? RuleContent { get; set; }
}
