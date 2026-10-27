using System.Collections.Concurrent;
using System.Text;

namespace Nomad.Core.Sessions;

/// <summary>Where a search found its words in a session's conversation: a line of text around the first one.</summary>
public sealed record TextMatch(string Before, string Match, string After);

/// <summary>
/// The conversation text of each transcript (typed prompts and Claude's replies; no tool calls, tool results, thinking
/// or system reminders), kept in memory for the session search (Ctrl+T). Read once, then only what was appended:
/// transcripts only grow, and a file that changed otherwise is read again. Safe to use from any thread.
/// </summary>
public sealed class TranscriptIndex
{
    /// <summary>Between two messages in <see cref="Entry.Text"/>: a match never spans them.</summary>
    public const char Separator = '\u001F';

    private sealed record Entry(long Size, DateTime Mtime, long Consumed, string Text);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The indexed text of a transcript; null if it has not been read yet.</summary>
    public string? TextOf(string file) => _entries.TryGetValue(file, out var entry) ? entry.Text : null;

    /// <summary>The transcript is indexed as it is on disk now.</summary>
    public bool IsCurrent(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return _entries.TryGetValue(file, out var entry) && info.Exists && entry.Size == info.Length && entry.Mtime == info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // Nothing better to read.
        }
    }

    /// <summary>Reads what is new in a transcript (all of it the first time). Returns whether its text changed.</summary>
    public bool Update(string file)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
            {
                return _entries.TryRemove(file, out _);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        _entries.TryGetValue(file, out var old);
        if (old is not null && old.Size == info.Length && old.Mtime == info.LastWriteTimeUtc)
        {
            return false;
        }

        // Appended to: read on from the last complete line. Shorter, or the old end was mid-line: read it all again.
        var from = old is not null && info.Length >= old.Size && old.Consumed > 0 ? old.Consumed : 0;
        try
        {
            var (text, consumed) = Read(file, from, info.Length);
            var combined = from > 0 && old is not null
                ? text.Length == 0 ? old.Text : old.Text.Length == 0 ? text : old.Text + Separator + text
                : text;
            _entries[file] = new Entry(info.Length, info.LastWriteTimeUtc, consumed, combined);
            return old is null || !ReferenceEquals(combined, old.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string Text, long Consumed) Read(string file, long from, long size)
    {
        using var stream = SessionStore.OpenShared(file);
        var sb = new StringBuilder();
        foreach (var line in SessionStore.ReadLines(stream, from, size - from, skipFirstPartial: false))
        {
            if (MessageText(line) is { } text)
            {
                if (sb.Length > 0)
                {
                    sb.Append(Separator);
                }

                sb.Append(text);
            }
        }

        // The next read starts after the last newline; a file ending mid-line is read whole next time.
        var consumed = 0L;
        if (size > 0)
        {
            stream.Seek(size - 1, SeekOrigin.Begin);
            consumed = stream.ReadByte() == '\n' ? size : 0;
        }

        return (sb.ToString(), consumed);
    }

    /// <summary>The searchable text of one transcript line: a typed prompt or a reply, else null.</summary>
    internal static string? MessageText(string line)
    {
        // Only prompts (text blocks, or plain string content) and replies with a text block are parsed; tool results are
        // skipped unread (they are most of the bytes).
        if (line.Contains("\"type\":\"tool_result\"", StringComparison.Ordinal)
            || !(line.Contains("\"type\":\"text\"", StringComparison.Ordinal) || line.Contains("\"type\":\"user\"", StringComparison.Ordinal)))
        {
            return null;
        }

        var record = TranscriptRecord.Parse(line);
        if (record is null || record.IsSidechain == true || record.IsMeta == true || record.IsCompactSummary == true)
        {
            return null;
        }

        if (record.Type == "user")
        {
            return SessionTitles.PromptText(record)?.Replace(Separator, ' ');
        }

        if (record.Type != "assistant" || record.Message?.Content is not { } blocks)
        {
            return null;
        }

        var texts = blocks.Where(b => b.Type == "text" && !string.IsNullOrWhiteSpace(b.Text)).Select(b => b.Text!.Trim()).ToList();
        return texts.Count == 0 ? null : string.Join("\n", texts).Replace(Separator, ' ');
    }

    /// <summary>The words of a query, split on whitespace.</summary>
    public static string[] Terms(string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Every term is in the text (any case).</summary>
    public static bool ContainsAll(string text, IReadOnlyList<string> terms) =>
        terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The first message that holds the first term, as one line around it; null if none does. A message holding every
    /// term wins over one with only some.
    /// </summary>
    public static TextMatch? Find(string text, IReadOnlyList<string> terms, int before = 40, int after = 160)
    {
        if (terms.Count == 0)
        {
            return null;
        }

        var lead = terms[0];
        (int Start, int End, int At)? first = null;
        for (var at = text.IndexOf(lead, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(lead, at + lead.Length, StringComparison.OrdinalIgnoreCase))
        {
            var start = text.LastIndexOf(Separator, at) + 1;
            var end = text.IndexOf(Separator, at);
            end = end < 0 ? text.Length : end;
            first ??= (start, end, at);
            if (terms.Count == 1 || ContainsAll(text[start..end], terms))
            {
                return Excerpt(text, start, end, at, lead.Length, before, after);
            }

            at = end; // On to the next message.
        }

        return first is { } f ? Excerpt(text, f.Start, f.End, f.At, lead.Length, before, after) : null;
    }

    /// <summary>The match with up to <paramref name="before"/> and <paramref name="after"/> characters around it, on one line.</summary>
    public static TextMatch Excerpt(string text, int start, int end, int at, int length, int before, int after)
    {
        var from = Math.Max(start, at - before * 2);
        var to = Math.Min(end, at + length + after * 2);
        var head = Collapse(text[from..at]).TrimStart();
        if (head.Length > before)
        {
            var cut = head.IndexOf(' ', head.Length - before);
            head = "…" + (cut >= 0 && cut < head.Length - 1 ? head[(cut + 1)..] : head[^before..]);
        }
        else if (from > start)
        {
            head = "…" + head;
        }

        var tail = Collapse(text[(at + length)..to]).TrimEnd();
        if (tail.Length > after)
        {
            tail = tail[..after];
        }

        return new TextMatch(head, Collapse(text.Substring(at, length)), tail);
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(ch);
        }

        if (space)
        {
            sb.Append(' ');
        }

        return sb.ToString();
    }
}
