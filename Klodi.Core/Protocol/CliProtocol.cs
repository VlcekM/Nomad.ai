using System.Text;
using System.Text.Json;

namespace Klodi.Core.Protocol;

/// <summary>Parses stdout lines and builds stdin lines of the stream-json protocol.</summary>
public static class CliProtocol
{
    /// <summary>
    /// Parses one stdout line. Returns null for blank or malformed lines; unknown <c>type</c> values
    /// come back as <see cref="UnknownLine"/> so callers can ignore them gracefully.
    /// </summary>
    public static CliLine? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(line);
        try
        {
            var type = PeekType(bytes);
            CliLine? parsed = type switch
            {
                "system" => JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.SystemLine),
                "stream_event" => JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.StreamEventLine),
                "assistant" => JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.AssistantLine),
                "user" => JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.UserLine),
                "result" => JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.ResultLine),
                "control_request" => JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.ControlRequestLine),
                "control_response" => JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.ControlResponseLine),
                "control_cancel_request" => JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.ControlCancelLine),
                _ => new UnknownLine { Type = type, Raw = line },
            };
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the top-level <c>type</c> property (normally the first one) without a full parse.</summary>
    internal static string? PeekType(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A protocol line must be a JSON object.");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isType = reader.ValueTextEquals("type"u8);
            if (!reader.Read())
            {
                break;
            }

            if (isType)
            {
                return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }

            reader.Skip();
        }

        return null;
    }

    public static string UserMessage(string text)
    {
        var message = new OutgoingUserMessage();
        message.Message.Content.Add(new OutgoingTextBlock { Text = text });
        return JsonSerializer.Serialize(message, ProtocolJsonContext.Default.OutgoingUserMessage);
    }

    public static string ControlRequest(string requestId, OutgoingControlPayload payload) =>
        JsonSerializer.Serialize(
            new OutgoingControlRequest { RequestId = requestId, Request = payload },
            ProtocolJsonContext.Default.OutgoingControlRequest);

    public static string PermissionResponse(string requestId, PermissionResultPayload result) =>
        JsonSerializer.Serialize(
            new OutgoingControlResponse { Response = new OutgoingControlResponseBody { RequestId = requestId, Response = result } },
            ProtocolJsonContext.Default.OutgoingControlResponse);

    public static string ErrorResponse(string requestId, string error) =>
        JsonSerializer.Serialize(
            new OutgoingControlResponse { Response = new OutgoingControlResponseBody { Subtype = "error", RequestId = requestId, Error = error } },
            ProtocolJsonContext.Default.OutgoingControlResponse);

    /// <summary>Concatenates the text parts of a tool_result <c>content</c> (string or block array).</summary>
    public static string ToolResultText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? "";
            case JsonValueKind.Array:
                var sb = new StringBuilder();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (type == "text" && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }

                        sb.Append(text.GetString());
                    }
                    else if (type == "image")
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }

                        sb.Append("[image]");
                    }
                }

                return sb.ToString();
            default:
                return "";
        }
    }
}
