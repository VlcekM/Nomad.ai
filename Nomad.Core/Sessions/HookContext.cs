using System.Text.Json;

namespace Nomad.Core.Sessions;

/// <summary>
/// What a UserPromptSubmit hook added to a prompt: live, from its <c>hook_response</c> output (JSON with
/// <c>hookSpecificOutput.additionalContext</c>, or plain text, which Claude Code adds as it is); in history, from the
/// <c>hook_additional_context</c> attachment the CLI saves.
/// </summary>
public static class HookContext
{
    public static string? FromOutput(string? stdout)
    {
        var text = stdout?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!text.StartsWith('{'))
        {
            return text;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("hookSpecificOutput", out var output) && output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty("additionalContext", out var added) && added.ValueKind == JsonValueKind.String
                && added.GetString() is { Length: > 0 } context ? context.Trim() : null;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public static string? FromAttachment(JsonElement attachment)
    {
        if (attachment.ValueKind != JsonValueKind.Object || !attachment.TryGetProperty("type", out var type)
            || type.GetString() != "hook_additional_context" || !attachment.TryGetProperty("content", out var content))
        {
            return null;
        }

        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join("\n", content.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString())),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
