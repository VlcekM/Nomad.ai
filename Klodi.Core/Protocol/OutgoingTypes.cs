using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klodi.Core.Protocol;

// Lines Klodi writes to the CLI's stdin.

/// <summary><c>{"type":"user","message":{...},"parent_tool_use_id":null,"session_id":""}</c></summary>
public sealed class OutgoingUserMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "user";
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
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

public sealed class OutgoingTextBlock
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

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
}

/// <summary>Session-scoped settings overlay for <c>apply_flag_settings</c>.</summary>
public sealed class FlagSettings
{
    [JsonPropertyName("effortLevel")] public string? EffortLevel { get; set; }
    [JsonPropertyName("fastMode")] public bool? FastMode { get; set; }
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
