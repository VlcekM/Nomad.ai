namespace Nomad.Core.Text;

/// <summary>A Markdown list item's start: <c>- </c>, <c>* </c>, <c>+ </c> or <c>12. </c> / <c>12) </c>, maybe a task box.</summary>
/// <param name="Indent">Characters before the marker.</param>
/// <param name="Marker">The bullet or the number with its <c>.</c> or <c>)</c>.</param>
/// <param name="ContentStart">Where the item's text starts in its line (after the space, and the task box).</param>
public sealed record ListItem(int Indent, string Marker, bool IsOrdered, int Number, bool IsTask, int ContentStart);

/// <summary>An edit for a text box: replace <see cref="Length"/> characters at <see cref="Start"/>, then select.</summary>
public readonly record struct TextEdit(int Start, int Length, string Insert, int SelectionStart, int SelectionEnd);

/// <summary>
/// Writing lists in the composer: a new line in an item starts the next one (numbered on), a new line in an
/// empty item ends the list (or steps it out a level), Tab and Shift+Tab nest and un-nest items, and Backspace
/// right after a marker takes it away. Ordered lists are renumbered as they change. A third backtick at the
/// start of a line opens a code block with its closing fence; inside one, none of this applies.
/// </summary>
public static class MarkdownLists
{
    public static ListItem? ParseItem(ReadOnlySpan<char> line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] is ' ' or '\t')
        {
            indent++;
        }

        var i = indent;
        if (i >= line.Length)
        {
            return null;
        }

        bool ordered;
        var number = 0;
        if (line[i] is '-' or '*' or '+')
        {
            ordered = false;
            i++;
        }
        else
        {
            var digits = 0;
            while (i < line.Length && char.IsAsciiDigit(line[i]) && digits < 9)
            {
                number = number * 10 + (line[i] - '0');
                i++;
                digits++;
            }

            if (digits == 0 || i >= line.Length || line[i] is not ('.' or ')'))
            {
                return null;
            }

            ordered = true;
            i++;
        }

        if (i >= line.Length || line[i] != ' ')
        {
            return null;
        }

        var marker = line[indent..i].ToString();
        var content = i + 1;
        var task = !ordered && content + 2 < line.Length && line[content] == '[' && line[content + 1] is ' ' or 'x' or 'X'
            && line[content + 2] == ']' && (content + 3 == line.Length || line[content + 3] == ' ');
        if (task)
        {
            content = Math.Min(line.Length, content + 4);
        }

        return new ListItem(indent, marker, ordered, number, task, content);
    }

    /// <summary>A new line at <paramref name="caret"/> inside a list item; null where it is just a new line.</summary>
    public static TextEdit? NewLine(string text, int caret, string newline)
    {
        var doc = new Doc(text);
        var (line, col) = doc.Locate(caret);
        if (doc.InCode(line))
        {
            // Code keeps the line's indentation.
            var indent = doc.Lines[line][..col].Length - doc.Lines[line][..col].TrimStart().Length;
            if (indent == 0)
            {
                return null;
            }

            var lead = doc.Lines[line][..indent];
            return new TextEdit(caret, 0, newline + lead, caret + newline.Length + lead.Length, caret + newline.Length + lead.Length);
        }

        if (ParseItem(doc.Lines[line]) is not { } item || col < item.ContentStart)
        {
            return null;
        }

        var current = doc.Lines[line];
        if (string.IsNullOrWhiteSpace(current[item.ContentStart..]))
        {
            // An empty item ends the list, or steps out to the level above.
            if (item.Indent > 0)
            {
                return Shift(doc, text, line, line, caret, caret, outdent: true);
            }

            doc.Lines[line] = "";
            doc.Caret = (line, 0);
            doc.Renumber(line);
            return doc.EditFrom(text);
        }

        var next = item.IsOrdered ? $"{item.Number + 1}{item.Marker[^1]}" : item.Marker;
        var prefix = current[..item.Indent] + next + " " + (item.IsTask ? "[ ] " : "");
        doc.Lines[line] = current[..col];
        doc.Insert(line + 1, prefix + current[col..].TrimStart(), newline);
        doc.Caret = (line + 1, prefix.Length);
        doc.Renumber(line + 1);
        return doc.EditFrom(text);
    }

    /// <summary>Tab (or Shift+Tab) with these list lines selected; null when none of them is a list item.</summary>
    public static TextEdit? Indent(string text, int selectionStart, int selectionEnd, bool outdent)
    {
        var doc = new Doc(text);
        var (from, _) = doc.Locate(Math.Min(selectionStart, selectionEnd));
        var (to, toCol) = doc.Locate(Math.Max(selectionStart, selectionEnd));
        if (to > from && toCol == 0)
        {
            to--;
        }

        return Shift(doc, text, from, to, selectionStart, selectionEnd, outdent);
    }

    /// <summary>Backspace right after an item's marker: out a level, or (at the top) the marker goes.</summary>
    public static TextEdit? Backspace(string text, int caret)
    {
        var doc = new Doc(text);
        var (line, col) = doc.Locate(caret);
        if (doc.InCode(line) || ParseItem(doc.Lines[line]) is not { } item || col != item.ContentStart)
        {
            return null;
        }

        if (item.Indent > 0)
        {
            return Shift(doc, text, line, line, caret, caret, outdent: true);
        }

        doc.Lines[line] = doc.Lines[line][item.ContentStart..];
        doc.Caret = (line, 0);
        doc.Renumber(line);
        return doc.EditFrom(text);
    }

    /// <summary>
    /// A third <c>`</c> typed at the start of a line (outside a code block) opens one: the closing fence goes in
    /// two lines down, and the caret on the empty line between. Null where the backtick is just typed.
    /// </summary>
    public static TextEdit? Backtick(string text, int caret, string newline)
    {
        var doc = new Doc(text);
        var (line, col) = doc.Locate(caret);
        var before = doc.Lines[line][..col];
        var indent = before[..(before.Length - before.TrimStart().Length)];
        if (before.TrimStart() != "``" || !string.IsNullOrWhiteSpace(doc.Lines[line][col..]) || doc.OpenFenceBefore(line))
        {
            return null;
        }

        var insert = "`" + doc.Lines[line][col..] + newline + indent + newline + indent + "```";
        var inside = caret + 1 + (doc.Lines[line].Length - col) + newline.Length + indent.Length;
        return new TextEdit(caret, doc.Lines[line].Length - col, insert, inside, inside);
    }

    private static TextEdit? Shift(Doc doc, string text, int from, int to, int selectionStart, int selectionEnd, bool outdent)
    {
        var any = false;
        var anchor = doc.Locate(selectionStart);
        var caret = doc.Locate(selectionEnd);
        for (var line = from; line <= to; line++)
        {
            if (doc.InCode(line) || ParseItem(doc.Lines[line]) is not { } item)
            {
                continue;
            }

            any = true;
            var indent = outdent ? doc.OuterIndent(line, item.Indent) : doc.InnerIndent(line, item.Indent);
            if (indent == item.Indent)
            {
                continue;
            }

            var rest = doc.Lines[line][item.Indent..];
            if (item.IsOrdered && !outdent)
            {
                // A new sublist counts from one (Renumber continues it if it joins one).
                rest = $"1{item.Marker[^1]}" + rest[item.Marker.Length..];
            }

            var delta = indent + rest.Length - doc.Lines[line].Length;
            doc.Lines[line] = new string(' ', indent) + rest;
            anchor = Moved(anchor, line, item.Indent, delta);
            caret = Moved(caret, line, item.Indent, delta);
        }

        if (!any)
        {
            return null;
        }

        doc.Anchor = anchor;
        doc.Caret = caret;
        for (var line = from; line <= to; line++)
        {
            doc.Renumber(line);
        }

        return doc.EditFrom(text);
    }

    private static (int Line, int Col) Moved((int Line, int Col) at, int line, int oldIndent, int delta) =>
        at.Line != line ? at : (line, at.Col < oldIndent ? Math.Max(0, at.Col + Math.Min(0, delta)) : Math.Max(0, at.Col + delta));

    /// <summary>The text as lines (without their breaks) and the breaks after them, with a caret to carry.</summary>
    private sealed class Doc
    {
        public readonly List<string> Lines = [];
        private readonly List<string> _breaks = [];

        public (int Line, int Col) Caret;
        public (int Line, int Col)? Anchor;

        public Doc(string text)
        {
            var start = 0;
            while (true)
            {
                var newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    Lines.Add(text[start..]);
                    _breaks.Add("");
                    break;
                }

                var cr = newline > start && text[newline - 1] == '\r';
                Lines.Add(text[start..(cr ? newline - 1 : newline)]);
                _breaks.Add(cr ? "\r\n" : "\n");
                start = newline + 1;
            }
        }

        public (int Line, int Col) Locate(int index)
        {
            var offset = 0;
            for (var i = 0; i < Lines.Count; i++)
            {
                var end = offset + Lines[i].Length;
                if (index <= end || i == Lines.Count - 1)
                {
                    return (i, Math.Clamp(index - offset, 0, Lines[i].Length));
                }

                offset = end + _breaks[i].Length;
            }

            return (0, 0);
        }

        public void Insert(int line, string content, string newline)
        {
            // The new line takes the break of the one before it (the last line has none), which gets a new one.
            Lines.Insert(line, content);
            _breaks.Insert(line, _breaks[line - 1]);
            _breaks[line - 1] = newline;
        }

        /// <summary>Tab: one level in, under the item above at this level (its text's column); else unchanged.</summary>
        public int InnerIndent(int line, int indent)
        {
            for (var i = line - 1; i >= 0 && InBlock(i); i--)
            {
                if (ParseItem(Lines[i]) is not { } above || above.Indent > indent)
                {
                    continue;
                }

                return above.Indent == indent ? above.Indent + above.Marker.Length + 1 : indent;
            }

            return indent;
        }

        /// <summary>Shift+Tab: out to the level of the nearest item above that sits further out.</summary>
        public int OuterIndent(int line, int indent)
        {
            if (indent == 0)
            {
                return 0;
            }

            for (var i = line - 1; i >= 0 && InBlock(i); i--)
            {
                if (ParseItem(Lines[i]) is { } above && above.Indent < indent)
                {
                    return above.Indent;
                }
            }

            return 0;
        }

        /// <summary>Numbers the ordered items of the list around <paramref name="line"/> one after another, per level.</summary>
        public void Renumber(int line)
        {
            if (!InBlock(line))
            {
                return;
            }

            var first = line;
            while (first > 0 && InBlock(first - 1))
            {
                first--;
            }

            var levels = new List<(int Indent, bool Ordered, int Number)>();
            for (var i = first; i < Lines.Count && InBlock(i); i++)
            {
                if (ParseItem(Lines[i]) is not { } item)
                {
                    continue;
                }

                while (levels.Count > 0 && levels[^1].Indent > item.Indent)
                {
                    levels.RemoveAt(levels.Count - 1);
                }

                if (levels.Count > 0 && levels[^1].Indent == item.Indent)
                {
                    var previous = levels[^1];
                    var number = item.Number;
                    if (previous.Ordered && item.IsOrdered && item.Number != previous.Number + 1)
                    {
                        number = previous.Number + 1;
                        var marker = $"{number}{item.Marker[^1]}";
                        var delta = marker.Length - item.Marker.Length;
                        Lines[i] = Lines[i][..item.Indent] + marker + Lines[i][(item.Indent + item.Marker.Length)..];
                        Caret = Moved(Caret, i, item.Indent + item.Marker.Length, delta);
                        if (Anchor is { } anchor)
                        {
                            Anchor = Moved(anchor, i, item.Indent + item.Marker.Length, delta);
                        }
                    }

                    levels[^1] = (item.Indent, item.IsOrdered, number);
                }
                else
                {
                    levels.Add((item.Indent, item.IsOrdered, item.Number));
                }
            }
        }

        /// <summary>The line is a fence, or inside a fenced code block.</summary>
        public bool InCode(int line) => Fence(Lines[line]) is not null || OpenFenceBefore(line);

        /// <summary>A code block opened above <paramref name="line"/> is still open there.</summary>
        public bool OpenFenceBefore(int line)
        {
            string? open = null;
            for (var i = 0; i < line; i++)
            {
                if (Fence(Lines[i]) is not { } fence)
                {
                    continue;
                }

                if (open is null)
                {
                    open = fence;
                }
                else if (fence[0] == open[0] && fence.Length >= open.Length)
                {
                    open = null;
                }
            }

            return open is not null;
        }

        /// <summary>The line's <c>```</c> or <c>~~~</c> run (three or more) when it starts with one.</summary>
        private static string? Fence(string line)
        {
            var text = line.TrimStart();
            if (text.Length < 3 || text[0] is not ('`' or '~') || text[1] != text[0] || text[2] != text[0])
            {
                return null;
            }

            var length = 3;
            while (length < text.Length && text[length] == text[0])
            {
                length++;
            }

            return text[..length];
        }

        /// <summary>A list's lines: its items and anything indented under them; a blank or unindented line ends it.</summary>
        private bool InBlock(int line) =>
            ParseItem(Lines[line]) is not null || (Lines[line].Length > 0 && Lines[line][0] is ' ' or '\t' && !string.IsNullOrWhiteSpace(Lines[line]));

        private int Offset((int Line, int Col) at)
        {
            var offset = 0;
            for (var i = 0; i < at.Line; i++)
            {
                offset += Lines[i].Length + _breaks[i].Length;
            }

            return offset + Math.Min(at.Col, Lines[at.Line].Length);
        }

        /// <summary>The smallest replacement that turns <paramref name="old"/> into this text.</summary>
        public TextEdit EditFrom(string old)
        {
            var text = string.Concat(Lines.Select((l, i) => l + _breaks[i]));
            var prefix = 0;
            while (prefix < old.Length && prefix < text.Length && old[prefix] == text[prefix])
            {
                prefix++;
            }

            var suffix = 0;
            while (suffix < old.Length - prefix && suffix < text.Length - prefix && old[^(suffix + 1)] == text[^(suffix + 1)])
            {
                suffix++;
            }

            var caret = Offset(Caret);
            return new TextEdit(prefix, old.Length - prefix - suffix, text[prefix..(text.Length - suffix)], Anchor is { } a ? Offset(a) : caret, caret);
        }
    }
}
