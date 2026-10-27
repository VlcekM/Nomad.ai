using System.Text;

namespace Nomad.Core.Sessions;

/// <summary>Derives a sidebar title from transcript records.</summary>
public static class SessionTitles
{
    public const int MaxLength = 60;

    /// <summary>
    /// The user-visible text of a user record, or null when the record is not a typed prompt
    /// (sidechain, meta, compaction summary, tool results, desktop-app markup such as
    /// <c>&lt;command-name&gt;</c> or <c>&lt;ide_opened_file&gt;</c>).
    /// </summary>
    public static string? PromptText(TranscriptRecord record)
    {
        if (record.Type != "user" || record.IsSidechain == true || record.IsMeta == true || record.IsCompactSummary == true)
        {
            return null;
        }

        if (record.Message?.Content is not { } blocks)
        {
            return null;
        }

        foreach (var block in blocks)
        {
            if (block.Type != "text" || string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            var text = block.Text.TrimStart();
            if (text.StartsWith('<') || text.StartsWith("[Request interrupted", StringComparison.Ordinal))
            {
                continue;
            }

            return block.Text.Trim();
        }

        return null;
    }

    /// <summary>Collapses whitespace and truncates to <see cref="MaxLength"/> characters with an ellipsis.</summary>
    public static string Shorten(string text, int maxLength = MaxLength)
    {
        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 8));
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
            if (sb.Length > maxLength)
            {
                break;
            }
        }

        if (sb.Length <= maxLength)
        {
            return sb.ToString();
        }

        sb.Length = maxLength;
        return sb.ToString().TrimEnd() + "…";
    }

    /// <summary>What a title is generated from, as the desktop app sends it: the first prompt trimmed, at most 2000 characters (no half surrogate pair at the end).</summary>
    public static string Description(string prompt)
    {
        var text = prompt.Trim();
        if (text.Length > 2000)
        {
            text = text[..2000];
        }

        return text.Length > 0 && char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
    }

    /// <summary>A generated title cleaned as the desktop app does: NFKC, whitespace collapsed; null when empty or over 200 characters.</summary>
    public static string? Clean(string? title)
    {
        if (title is null)
        {
            return null;
        }

        var text = string.Join(' ', title.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length is 0 or > 200 ? null : text;
    }
}
