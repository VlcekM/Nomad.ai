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
            || ((systemPrompt is null || Same(parts[0], systemPrompt)) && (appended is null || parts.Any(part => Holds(part, appended)))))
        {
            return false;
        }

        // The records to remove sit in the conversation's chain (the next record names one as its parent), and the CLI rebuilds
        // a resumed conversation by walking parents back from the last record: a record whose parent is gone loses everything
        // before it. So a record that named a removed one is pointed at that one's own parent (through any run of them).
        var removed = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(transcript))
        {
            if (IsCandidate(line) && IsSnapshotRecord(line) && Link(line, "uuid") is { } uuid)
            {
                removed[uuid] = Link(line, "parentUuid");
            }
        }

        string? Survivor(string? parent)
        {
            for (var hops = 0; parent is not null && removed.TryGetValue(parent, out var above) && hops <= removed.Count; hops++)
            {
                parent = above;
            }

            return parent;
        }

        var original = new FileInfo(transcript);
        var (size, written) = (original.Length, original.LastWriteTimeUtc);
        var temp = transcript + ".prompt-refresh";
        var dropped = 0;
        using (var writer = new StreamWriter(temp, append: false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" })
        {
            foreach (var line in File.ReadLines(transcript))
            {
                if (IsCandidate(line) && IsSnapshotRecord(line))
                {
                    dropped++;
                    continue;
                }

                writer.WriteLine(Relink(Relink(line, "parentUuid", removed, Survivor), "logicalParentUuid", removed, Survivor));
            }
        }

        // Written to meanwhile (its chat started in the meantime): what it wrote would be lost, so the file stays as it is.
        original.Refresh();
        if (original.Length != size || original.LastWriteTimeUtc != written)
        {
            File.Delete(temp);
            return false;
        }

        File.Move(temp, transcript, overwrite: true);
        return dropped > 0;
    }

    private static bool IsCandidate(string line) =>
        line.Contains(Marker, StringComparison.Ordinal) || line.Contains("\"prompt_render_point\"", StringComparison.Ordinal);

    /// <summary>A record's top-level link field (its uuid, parentUuid…): the string, or null when absent, null or unreadable.</summary>
    private static string? Link(string line, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The line with its <paramref name="field"/> pointed past removed records, when it named one; otherwise unchanged. Only the
    /// field's own text is replaced, so the rest of the record stays byte for byte as the CLI wrote it.
    /// </summary>
    private static string Relink(string line, string field, Dictionary<string, string?> removed, Func<string?, string?> survivor)
    {
        if (removed.Count == 0 || !removed.Keys.Any(uuid => line.Contains(uuid, StringComparison.Ordinal))
            || Link(line, field) is not { } parent || !removed.ContainsKey(parent))
        {
            return line;
        }

        var key = $"\"{field}\":\"{parent}\"";
        var at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
        {
            return line; // Written with spacing this doesn't expect: left as it was.
        }

        var replacement = survivor(parent) is { } kept ? $"\"{field}\":\"{kept}\"" : $"\"{field}\":null";
        return string.Concat(line.AsSpan(0, at), replacement, line.AsSpan(at + key.Length));
    }

    /// <summary>
    /// Whether a saved part is <paramref name="appended"/>, alone or with more appended after it (the CLI joins another source's
    /// text on with a blank line).
    /// </summary>
    private static bool Holds(string saved, string appended)
    {
        var part = saved.ReplaceLineEndings("\n");
        var text = appended.ReplaceLineEndings("\n").TrimEnd();
        return Same(part, text) || part.StartsWith(text + "\n\n", StringComparison.Ordinal);
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
