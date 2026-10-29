using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nomad.Core.Codex;

/// <summary>Lenient readers for Codex's JSON: a missing or mistyped field reads as null.</summary>
internal static class CodexJson
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? (long)d
            : null;

    public static double? Double(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True
            : null;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    /// <summary>Any value (object, array, string…), or undefined when missing or null.</summary>
    public static JsonElement Any(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : default;

    /// <summary>A copy that outlives its document; undefined stays undefined (which <c>Clone</c> refuses).</summary>
    public static JsonElement Keep(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? default : e.Clone();

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static IEnumerable<string> Strings(JsonElement e, string name) =>
        Arr(e, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!);

    public static JsonElement ToElement(JsonNode? node)
    {
        using var doc = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return doc.RootElement.Clone();
    }

    public static JsonNode? ToNode(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(e.GetRawText());
}
