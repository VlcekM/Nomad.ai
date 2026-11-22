using System.Text.Json;

namespace Nomad.Core.Sessions;

/// <summary>
/// Claude Code saves the system prompt a session started with in its transcript (<c>prompt_snapshot</c> attachments) and uses
/// that copy whenever the session is resumed, whatever <c>--system-prompt-file</c> says then. So a changed prompt file never
/// reaches a resumed session unless the saved copy goes: without one, the CLI renders the prompt afresh from its arguments.
/// </summary>
public static class PromptSnapshots
{
    private const string Marker = "\"prompt_snapshot\"";

    /// <summary>
    /// When the transcript's latest saved prompt does not begin with <paramref name="systemPrompt"/> (the prompt file's text,
    /// which the CLI keeps as the first part; null when there is no file), or holds no part equal to <paramref name="appended"/>
    /// (what Nomad appends to it; null to leave that unchecked), removes every saved prompt so the next resume uses the current
    /// one. Returns whether it removed any. Only for a session whose process is not running.
    /// </summary>
    public static bool DropIfStale(string transcript, string? systemPrompt, string? appended = null)
    {
        if (!File.Exists(transcript))
        {
            return false;
        }

        string? latest = null;
        foreach (var line in File.ReadLines(transcript))
        {
            if (line.Contains(Marker, StringComparison.Ordinal))
            {
                latest = line;
            }
        }

        if (latest is null || Parts(latest) is not { Count: > 0 } parts
            || ((systemPrompt is null || Same(parts[0], systemPrompt)) && (appended is null || parts.Any(part => Same(part, appended)))))
        {
            return false;
        }

        var temp = transcript + ".prompt-refresh";
        var dropped = 0;
        using (var writer = new StreamWriter(temp, append: false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" })
        {
            foreach (var line in File.ReadLines(transcript))
            {
                if (line.Contains(Marker, StringComparison.Ordinal) || line.Contains("\"prompt_render_point\"", StringComparison.Ordinal))
                {
                    if (IsSnapshotRecord(line))
                    {
                        dropped++;
                        continue;
                    }
                }

                writer.WriteLine(line);
            }
        }

        File.Move(temp, transcript, overwrite: true);
        return dropped > 0;
    }

    private static bool Same(string saved, string current) =>
        string.Equals(saved.ReplaceLineEndings("\n").TrimEnd(), current.ReplaceLineEndings("\n").TrimEnd(), StringComparison.Ordinal);

    /// <summary>
    /// The parts of a saved prompt: first the prompt file's text (or the CLI's own prompt), last what was appended to it. Null
    /// when the line is not a readable snapshot.
    /// </summary>
    private static List<string>? Parts(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("attachment", out var attachment)
                && attachment.TryGetProperty("type", out var type) && type.GetString() == "prompt_snapshot"
                && attachment.TryGetProperty("systemPrompt", out var parts) && parts.ValueKind == JsonValueKind.Array
                ? [.. parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!)] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSnapshotRecord(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "attachment"
                && doc.RootElement.TryGetProperty("attachment", out var attachment)
                && attachment.TryGetProperty("type", out var kind) && kind.GetString() is "prompt_snapshot" or "prompt_render_point";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
