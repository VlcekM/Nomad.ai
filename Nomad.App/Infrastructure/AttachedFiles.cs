namespace Nomad.App.Infrastructure;

/// <summary>
/// Files attached to a prompt go in as @-mentions after its text, one per line, as Claude Code reads them; a
/// transcript's prompt splits back into the text and its file chips.
/// </summary>
public static class AttachedFiles
{
    public static string AddTo(string text, IEnumerable<string> paths)
    {
        var mentions = paths.Select(Mention).ToList();
        if (mentions.Count == 0)
        {
            return text;
        }

        return (text.Length > 0 ? text + "\n\n" : "") + string.Join("\n", mentions);
    }

    /// <summary>The prompt's own text, and the files mentioned on its trailing lines.</summary>
    public static (string Text, List<Attachment> Files) Split(string prompt)
    {
        var lines = prompt.ReplaceLineEndings("\n").Split('\n').ToList();
        var files = new List<Attachment>();
        while (lines.Count > 0 && Parse(lines[^1]) is { } path)
        {
            files.Insert(0, Attachment.ForFile(path));
            lines.RemoveAt(lines.Count - 1);
        }

        if (files.Count == 0)
        {
            return (prompt, files);
        }

        return (string.Join("\n", lines).TrimEnd(), files);
    }

    private static string Mention(string path) => path.Contains(' ') ? $"@\"{path}\"" : "@" + path;

    // Only absolute paths, so a prompt that happens to end in "@someone" stays text.
    private static string? Parse(string line)
    {
        line = line.Trim();
        if (!line.StartsWith('@'))
        {
            return null;
        }

        var path = line.Length > 2 && line[1] == '"' && line[^1] == '"' ? line[2..^1] : line[1..];
        return path.Length > 0 && (path.Contains(' ') == line.StartsWith("@\"")) && Path.IsPathFullyQualified(path) ? path : null;
    }
}
