using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;

namespace Nomad.Core.OpenCode;

/// <summary>Maps v2 events onto Nomad's assistant, tool, and permission events.</summary>
public sealed partial class OpenCodeTranslator
{
    /// <summary>Processes v2 events for this session only.</summary>
    private IEnumerable<AgentEvent> HandleV2(string? type, JsonElement data)
    {
        var form = OpenCodeJson.Obj(data, "form");
        var sessionId = OpenCodeJson.Str(data, "sessionID") ?? OpenCodeJson.Str(form, "sessionID");
        if (sessionId != SessionId) return [];

        return type switch
        {
            "session.execution.started" => [new StatusEvent("requesting")],
            "session.execution.succeeded" => Idle(),
            "session.execution.failed" => FinishV2(OpenCodeJson.Obj(data, "error"), false),
            "session.execution.interrupted" => FinishV2(default, true),
            "session.renamed" => Updated(data),
            "session.step.started" => StartV2Message(data),
            "session.step.ended" => UpdateV2Usage(data),
            "session.retry.scheduled" => [new StatusEvent("retrying")],
            "session.text.started" or "session.text.delta" or "session.text.ended" => V2Text(type, data, AgentBlockKind.Text),
            "session.reasoning.started" or "session.reasoning.delta" or "session.reasoning.ended" => V2Text(type, data, AgentBlockKind.Thinking),
            "session.tool.input.started" or "session.tool.called" or "session.tool.success" or "session.tool.failed" => V2Tool(type, data),
            "permission.asked" => V2Permission(data),
            "permission.replied" => Withdrawn(OpenCodeJson.Str(data, "requestID")),
            "form.created" => V2Form(form),
            "form.replied" or "form.cancelled" => Withdrawn(OpenCodeJson.Str(data, "id")),
            _ => [],
        };
    }

    /// <summary>Records v2 completion data before closing the turn.</summary>
    private IEnumerable<AgentEvent> FinishV2(JsonElement error, bool interrupted)
    {
        InterruptRequested |= interrupted;
        if (error.ValueKind == JsonValueKind.Object) _turnError = ErrorText(error) ?? "OpenCode reported an error.";

        return Idle();
    }

    /// <summary>Announces an assistant message on the first event that references it.</summary>
    private IEnumerable<AgentEvent> StartV2Message(JsonElement data)
    {
        if (OpenCodeJson.Str(data, "assistantMessageID") is not { } id) return [];

        var model = OpenCodeJson.Obj(data, "model");
        return MessageUpdated(OpenCodeTools.ToElement(new JsonObject
        {
            ["id"] = id,
            ["role"] = "assistant",
            ["providerID"] = OpenCodeJson.Str(model, "providerID"),
            ["modelID"] = OpenCodeJson.Str(model, "id"),
        }));
    }

    /// <summary>Accumulates usage reported by a completed v2 step.</summary>
    private IEnumerable<AgentEvent> UpdateV2Usage(JsonElement data)
    {
        if (OpenCodeJson.Str(data, "assistantMessageID") is not { } messageId) return [];

        var part = JsonObject.Create(data)!;
        part["id"] = messageId + ":usage";
        part["messageID"] = messageId;
        part["type"] = "step-finish";
        return PartUpdated(OpenCodeTools.ToElement(part));
    }

    /// <summary>Maps a v2 text or reasoning block and its deltas.</summary>
    private IEnumerable<AgentEvent> V2Text(string type, JsonElement data, AgentBlockKind kind)
    {
        if (OpenCodeJson.Str(data, "assistantMessageID") is not { } id || _closedMessages.Contains(id)) yield break;

        foreach (var message in StartV2Message(data)) yield return message;

        var partId = $"{id}:{kind}:{OpenCodeJson.Long(data, "ordinal")}";
        var (index, isNew) = IndexOf(id, partId);
        if (isNew) yield return new BlockStartedEvent(id, index, kind, null, null, null);

        if (OpenCodeJson.Str(data, "delta") is { Length: > 0 } delta && !_completedParts.Contains(partId))
        {
            yield return kind == AgentBlockKind.Text ? new TextDeltaEvent(id, index, delta, null) : new ThinkingDeltaEvent(id, index, delta, null);
        }

        if (type.EndsWith(".ended", StringComparison.Ordinal) && _completedParts.Add(partId))
        {
            yield return new BlockCompletedEvent(id, index, kind, OpenCodeJson.Str(data, "text") ?? "", null);
        }
    }

    /// <summary>Tracks names until a v2 tool call supplies its arguments.</summary>
    private readonly Dictionary<string, string> _v2ToolNames = new(StringComparer.Ordinal);

    /// <summary>Translates a v2 tool's start, input and result.</summary>
    private IEnumerable<AgentEvent> V2Tool(string type, JsonElement data)
    {
        if (OpenCodeJson.Str(data, "id") is not { } id || OpenCodeJson.Str(data, "assistantMessageID") is not { } messageId) yield break;

        foreach (var message in StartV2Message(data)) yield return message;

        if (type == "session.tool.input.started" && OpenCodeJson.Str(data, "name") is { } name) _v2ToolNames[id] = name;

        var state = new JsonObject
        {
            ["status"] = type switch
            {
                "session.tool.called" => "running",
                "session.tool.success" => "completed",
                "session.tool.failed" => "error",
                _ => "pending",
            },
            ["input"] = V2Node(data, "input"),
            ["metadata"] = V2Node(data, "metadata"),
            ["output"] = OpenCodeClient.V2ToolOutput(data),
            ["error"] = ErrorText(OpenCodeJson.Obj(data, "error")),
        };

        var part = new JsonObject
        {
            ["id"] = "tool:" + id,
            ["messageID"] = messageId,
            ["type"] = "tool",
            ["callID"] = id,
            ["tool"] = _v2ToolNames.GetValueOrDefault(id),
            ["state"] = state,
        };
        foreach (var item in PartUpdated(OpenCodeTools.ToElement(part))) yield return item;

        if (type is "session.tool.success" or "session.tool.failed") _v2ToolNames.Remove(id);
    }

    /// <summary>Maps a v2 permission action and resource list onto the shared permission prompt.</summary>
    private IEnumerable<AgentEvent> V2Permission(JsonElement data) => PermissionAsked(OpenCodeTools.ToElement(new JsonObject
    {
        ["id"] = OpenCodeJson.Str(data, "id"),
        ["permission"] = OpenCodeJson.Str(data, "action"),
        ["patterns"] = V2Node(data, "resources"),
        ["metadata"] = V2Node(data, "metadata"),
        ["tool"] = new JsonObject { ["callID"] = OpenCodeJson.Str(OpenCodeJson.Obj(data, "source"), "id") },
    }), suppressAllowForSession: true);

    /// <summary>Presents OpenCode's tool form using Nomad's question cards.</summary>
    private IEnumerable<AgentEvent> V2Form(JsonElement form)
    {
        if (OpenCodeJson.Arr(form, "fields") is not { } fields) return [];

        var questions = new JsonArray();
        foreach (var field in fields.EnumerateArray())
        {
            var type = OpenCodeJson.Str(field, "type");
            var options = new JsonArray();
            if (OpenCodeJson.Arr(field, "options") is { } choices)
            {
                foreach (var option in choices.EnumerateArray())
                {
                    options.Add((JsonNode)new JsonObject
                    {
                        ["label"] = OpenCodeJson.Str(option, "value"),
                        ["description"] = OpenCodeJson.Str(option, "label") ?? OpenCodeJson.Str(option, "description"),
                    });
                }
            }

            if (type == "boolean")
            {
                options.Add((JsonNode)new JsonObject { ["label"] = "true" });
                options.Add((JsonNode)new JsonObject { ["label"] = "false" });
            }

            questions.Add((JsonNode)new JsonObject
            {
                ["question"] = OpenCodeJson.Str(field, "title") ?? OpenCodeJson.Str(field, "key"),
                ["header"] = OpenCodeJson.Str(form, "title"),
                ["multiple"] = type == "multiselect",
                ["options"] = options,
            });
        }

        return QuestionAsked(OpenCodeTools.ToElement(new JsonObject { ["id"] = OpenCodeJson.Str(form, "id"), ["questions"] = questions }));
    }

    /// <summary>Copies an optional v2 JSON value for translation.</summary>
    private static JsonNode? V2Node(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) ? JsonNode.Parse(value.GetRawText()) : null;
}
