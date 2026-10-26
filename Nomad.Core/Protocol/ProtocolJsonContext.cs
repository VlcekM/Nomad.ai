using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nomad.Core.Protocol;

/// <summary>Source-generated (reflection-free, AOT-safe) serializer metadata for the protocol.</summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SystemLine))]
[JsonSerializable(typeof(StreamEventLine))]
[JsonSerializable(typeof(AssistantLine))]
[JsonSerializable(typeof(UserLine))]
[JsonSerializable(typeof(ResultLine))]
[JsonSerializable(typeof(ControlRequestLine))]
[JsonSerializable(typeof(ControlResponseLine))]
[JsonSerializable(typeof(ControlCancelLine))]
[JsonSerializable(typeof(WireContentBlock))]
[JsonSerializable(typeof(InitializeResponse))]
[JsonSerializable(typeof(OutgoingUserMessage))]
[JsonSerializable(typeof(OutgoingControlRequest))]
[JsonSerializable(typeof(OutgoingControlResponse))]
[JsonSerializable(typeof(PermissionRulesUpdate))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
