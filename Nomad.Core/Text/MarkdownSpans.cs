namespace Nomad.Core.Text;

/// <summary>How a stretch of prompt text is drawn while it is being typed (several can combine).</summary>
[Flags]
public enum SpanStyle : ushort
{
    None = 0,

    /// <summary>Syntax that is kept but quiet: <c>**</c>, <c>#</c>, <c>&gt;</c>, fences, a link's address.</summary>
    Marker = 1 << 0,
    Bold = 1 << 1,
    Italic = 1 << 2,

    /// <summary>Inline <c>`code`</c>.</summary>
    Code = 1 << 3,
    Strike = 1 << 4,
    Link = 1 << 5,
    Heading = 1 << 6,
    Quote = 1 << 7,

    /// <summary>A list item's <c>-</c>, <c>*</c> or <c>+</c>, drawn as a dot.</summary>
    Bullet = 1 << 8,

    /// <summary>An ordered item's <c>1.</c>.</summary>
    Number = 1 << 9,

    /// <summary>A fenced code block: its fences, lines and the breaks between them.</summary>
    CodeBlock = 1 << 10,
}

public readonly record struct MarkdownSpan(int Start, int Length, SpanStyle Style);

/// <summary>
/// A forgiving, line-by-line reading of Markdown for highlighting the composer as it is typed: headings, quotes,
/// list markers, fenced code, inline code, bold, italic, strikethrough and links. The text itself is never
/// changed, so every span indexes straight into it (new lines may be <c>\r\n</c>).
/// </summary>
public static class MarkdownSpans
{
    public static List<MarkdownSpan> Highlight(string? text)
    {
        var spans = new List<MarkdownSpan>();
        if (string.IsNullOrEmpty(text))
        {
            return spans;
        }

        var styles = new SpanStyle[text.Length];
        var inFence = false;
        var fence = "";
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            var contentEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;
            var wasInFence = inFence;
            Line(text, styles, start, contentEnd, ref inFence, ref fence);

            // A code block's lines, fences and breaks included, are one stretch (drawn on one background).
            if (wasInFence || inFence)
            {
                Mark(styles, start, newline < 0 ? end : newline + 1, SpanStyle.CodeBlock);
            }

            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        var runStart = 0;
        for (var i = 1; i <= styles.Length; i++)
        {
            if (i == styles.Length || styles[i] != styles[runStart])
            {
                if (styles[runStart] != SpanStyle.None)
                {
                    spans.Add(new MarkdownSpan(runStart, i - runStart, styles[runStart]));
                }

                runStart = i;
            }
        }

        return spans;
    }

    private static void Line(string text, SpanStyle[] styles, int start, int end, ref bool inFence, ref string fence)
    {
        var pos = start;
        while (pos < end && text[pos] is ' ' or '\t')
        {
            pos++;
        }

        // ``` or ~~~ opens a code block; the same fence (or a longer one) closes it.
        if (end - pos >= 3 && text[pos] is '`' or '~' && text[pos + 1] == text[pos] && text[pos + 2] == text[pos])
        {
            var length = 3;
            while (pos + length < end && text[pos + length] == text[pos])
            {
                length++;
            }

            var mark = new string(text[pos], length);
            if (!inFence)
            {
                inFence = true;
                fence = mark;
                Mark(styles, pos, end, SpanStyle.Marker);
                return;
            }

            if (mark.Length >= fence.Length && text[pos] == fence[0] && IsBlank(text, pos + length, end))
            {
                inFence = false;
                Mark(styles, pos, end, SpanStyle.Marker);
                return;
            }
        }

        if (inFence)
        {
            Mark(styles, start, end, SpanStyle.CodeBlock);
            return;
        }

        if (pos >= end)
        {
            return;
        }

        // # Heading
        if (text[pos] == '#')
        {
            var level = 0;
            while (pos + level < end && text[pos + level] == '#')
            {
                level++;
            }

            if (level <= 6 && (pos + level == end || text[pos + level] == ' '))
            {
                Mark(styles, pos, pos + level, SpanStyle.Marker);
                Mark(styles, pos + level, end, SpanStyle.Heading | SpanStyle.Bold);
                Inline(text, styles, pos + level, end);
                return;
            }
        }

        // > Quote (markers may repeat: > > nested)
        if (text[pos] == '>')
        {
            var body = pos;
            while (body < end && text[body] is '>' or ' ')
            {
                if (text[body] == '>')
                {
                    Mark(styles, body, body + 1, SpanStyle.Marker);
                }

                body++;
            }

            Mark(styles, body, end, SpanStyle.Quote);
            Inline(text, styles, body, end);
            return;
        }

        // ---, ***, ___ on their own: a rule.
        if (IsRule(text, pos, end))
        {
            Mark(styles, pos, end, SpanStyle.Marker);
            return;
        }

        if (MarkdownLists.ParseItem(text.AsSpan(start, end - start)) is { } item)
        {
            var marker = start + item.Indent;
            Mark(styles, marker, marker + item.Marker.Length, item.IsOrdered ? SpanStyle.Number : SpanStyle.Bullet);
            if (item.IsTask)
            {
                var box = marker + item.Marker.Length + 1;
                Mark(styles, box, box + 3, SpanStyle.Marker);
            }

            pos = start + item.ContentStart;
        }

        Inline(text, styles, pos, end);
    }

    private static void Inline(string text, SpanStyle[] styles, int start, int end)
    {
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < end && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
            {
                Mark(styles, i, i + 1, SpanStyle.Marker);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var ticks = Run(text, i, end, '`');
                var close = FindRun(text, i + ticks, end, '`', ticks);
                if (close >= 0)
                {
                    Mark(styles, i, i + ticks, SpanStyle.Marker | SpanStyle.Code);
                    Mark(styles, i + ticks, close, SpanStyle.Code);
                    Mark(styles, close, close + ticks, SpanStyle.Marker | SpanStyle.Code);
                    i = close + ticks;
                    continue;
                }

                i += ticks;
                continue;
            }

            if (c == '[' && Link(text, styles, i, end) is var after and > 0)
            {
                i = after;
                continue;
            }

            if ((c is '*' or '_' or '~') && Emphasis(text, styles, i, end) is var past and > 0)
            {
                i = past;
                continue;
            }

            i++;
        }
    }

    /// <summary>[text](address): the text as a link, the brackets and address quiet. Returns where it ends, or 0.</summary>
    private static int Link(string text, SpanStyle[] styles, int open, int end)
    {
        var close = text.IndexOf(']', open + 1, end - open - 1);
        if (close < 0 || close + 1 >= end || text[close + 1] != '(')
        {
            return 0;
        }

        var paren = text.IndexOf(')', close + 2, end - close - 2);
        if (paren < 0 || close == open + 1)
        {
            return 0;
        }

        Mark(styles, open, open + 1, SpanStyle.Marker);
        Mark(styles, open + 1, close, SpanStyle.Link);
        Inline(text, styles, open + 1, close);
        Mark(styles, close, paren + 1, SpanStyle.Marker);
        return paren + 1;
    }

    /// <summary>**bold**, __bold__, *italic*, _italic_, ~~struck~~. Returns where it ends, or 0.</summary>
    private static int Emphasis(string text, SpanStyle[] styles, int i, int end)
    {
        var c = text[i];
        var run = Run(text, i, end, c);

        // Inside a word, _ is just part of it (snake_case), and a lone ~ is just a tilde.
        if (c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]))
        {
            return i + run;
        }

        if (c == '~' && run != 2)
        {
            return 0;
        }

        var width = c == '~' ? 2 : Math.Min(run, 3);
        var open = i + run - width;
        var bodyStart = open + width;
        if (bodyStart >= end || char.IsWhiteSpace(text[bodyStart]))
        {
            return 0;
        }

        // The closing run: the same character, as many or more, right after something that isn't a space.
        for (var j = bodyStart + 1; j < end; j++)
        {
            if (text[j] == '`')
            {
                var ticks = Run(text, j, end, '`');
                var skip = FindRun(text, j + ticks, end, '`', ticks);
                j = skip >= 0 ? skip + ticks - 1 : j + ticks - 1;
                continue;
            }

            if (text[j] != c || char.IsWhiteSpace(text[j - 1]))
            {
                continue;
            }

            var closeRun = Run(text, j, end, c);
            if (closeRun < width)
            {
                j += closeRun - 1;
                continue;
            }

            if (c == '_' && j + width < end && char.IsLetterOrDigit(text[j + width]))
            {
                j += closeRun - 1;
                continue;
            }

            var style = c == '~'
                ? SpanStyle.Strike
                : width switch { 1 => SpanStyle.Italic, 2 => SpanStyle.Bold, _ => SpanStyle.Bold | SpanStyle.Italic };
            Mark(styles, open, bodyStart, SpanStyle.Marker);
            Mark(styles, bodyStart, j, style);
            Inline(text, styles, bodyStart, j);
            Mark(styles, j, j + width, SpanStyle.Marker);
            return j + width;
        }

        return 0;
    }

    private static bool IsRule(string text, int pos, int end)
    {
        var c = text[pos];
        if (c is not ('-' or '*' or '_'))
        {
            return false;
        }

        var count = 0;
        for (var i = pos; i < end; i++)
        {
            if (text[i] == c)
            {
                count++;
            }
            else if (text[i] != ' ')
            {
                return false;
            }
        }

        return count >= 3;
    }

    private static int Run(string text, int i, int end, char c)
    {
        var n = 0;
        while (i + n < end && text[i + n] == c)
        {
            n++;
        }

        return n;
    }

    /// <summary>The next run of exactly <paramref name="length"/> <paramref name="c"/>s from <paramref name="from"/>, or -1.</summary>
    private static int FindRun(string text, int from, int end, char c, int length)
    {
        for (var j = from; j < end; j++)
        {
            if (text[j] != c)
            {
                continue;
            }

            var n = Run(text, j, end, c);
            if (n == length)
            {
                return j;
            }

            j += n - 1;
        }

        return -1;
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void Mark(SpanStyle[] styles, int start, int end, SpanStyle style)
    {
        for (var i = start; i < end; i++)
        {
            styles[i] |= style;
        }
    }
}
