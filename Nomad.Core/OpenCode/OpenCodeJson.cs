using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nomad.Core.OpenCode;

/// <summary>Lenient readers for OpenCode's JSON: a missing or mistyped field reads as null.</summary>
internal static class OpenCodeJson
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? (long)d
            : null;

    public static double? Double(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    public static JsonElement? Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;
}

/// <summary>OpenCode's session titles.</summary>
public static partial class OpenCodeTitles
{
    /// <summary>The placeholder a session has until OpenCode names it ("New session - 2026-10-29T09:03:44.589Z") reads as no title.</summary>
    public static string Clean(string? title) =>
        string.IsNullOrWhiteSpace(title) || Placeholder().IsMatch(title) ? "" : title.Trim();

    [GeneratedRegex(@"^(New session|Child session) - \d{4}-\d\d-\d\dT")]
    private static partial Regex Placeholder();
}
