using System.Text;

namespace Nomad.App.Ide;

/// <summary>A place in a document: a line and an index into its text (tabs count as one).</summary>
public readonly record struct TextPos(int Line, int Index) : IComparable<TextPos>
{
    public int CompareTo(TextPos other) => Line != other.Line ? Line.CompareTo(other.Line) : Index.CompareTo(other.Index);

    public static bool operator <(TextPos a, TextPos b) => a.CompareTo(b) < 0;

    public static bool operator >(TextPos a, TextPos b) => a.CompareTo(b) > 0;

    public static bool operator <=(TextPos a, TextPos b) => a.CompareTo(b) <= 0;

    public static bool operator >=(TextPos a, TextPos b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// An open file's text as lines, with its caret and selection, its undo history and how it is written (encoding, byte
/// order mark, line breaks), so each tab keeps its own while one <see cref="CodeView"/> shows whichever is active.
/// </summary>
public sealed class CodeDocument
{
    /// <summary>Typing within this long of the last key joins the same undo step.</summary>
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1.5);

    private readonly List<string> _lines;
    private readonly List<Edit> _undo = [];
    private readonly List<Edit> _redo = [];
    private int _version;
    private int _savedVersion;
    private int _nextVersion;
    private DateTime _lastEdit;

    public CodeDocument(string text, bool readOnly = false, Encoding? encoding = null, bool bom = false)
    {
        _lines = [.. text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None)];
        var lf = text.IndexOf('\n');
        NewLine = lf > 0 && text[lf - 1] == '\r' ? "\r\n" : lf >= 0 ? "\n" : text.Contains('\r') ? "\r" : "\r\n";
        IndentWithTabs = _lines.Count(l => l.StartsWith('\t')) > _lines.Count(l => l.StartsWith("  ", StringComparison.Ordinal));
        IsReadOnly = readOnly;
        Encoding = encoding ?? new UTF8Encoding(false);
        HasBom = bom;
    }

    public static CodeDocument Empty { get; } = new("", readOnly: true);

    public IReadOnlyList<string> Lines => _lines;

    public bool IsReadOnly { get; }

    public Encoding Encoding { get; }

    public bool HasBom { get; }

    /// <summary>The file's line break (its first one), used for the whole file when saved.</summary>
    public string NewLine { get; }

    /// <summary>Tab inserts a tab when most indented lines start with one, else spaces.</summary>
    public bool IndentWithTabs { get; }

    /// <summary>Its syntax highlighting, when its language has a grammar.</summary>
    public CodeHighlighting? Highlighting { get; set; }

    public TextPos Anchor { get; set; }

    public TextPos Caret { get; set; }

    public bool IsDirty => _version != _savedVersion;

    /// <summary>The text changed (an edit, undo or redo).</summary>
    public event Action? Changed;

    /// <summary>Whether it now differs from the file as last saved or loaded.</summary>
    public event Action? DirtyChanged;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string Text => string.Join(NewLine, _lines);

    public TextPos End => new(_lines.Count - 1, _lines[^1].Length);

    public TextPos Clamp(TextPos p)
    {
        var line = Math.Clamp(p.Line, 0, _lines.Count - 1);
        return new TextPos(line, Math.Clamp(p.Index, 0, _lines[line].Length));
    }

    public string GetText(TextPos from, TextPos to)
    {
        if (from > to)
        {
            (from, to) = (to, from);
        }

        if (from.Line == to.Line)
        {
            return _lines[from.Line][from.Index..to.Index];
        }

        var sb = new StringBuilder(_lines[from.Line][from.Index..]);
        for (var line = from.Line + 1; line < to.Line; line++)
        {
            sb.Append('\n').Append(_lines[line]);
        }

        return sb.Append('\n').Append(_lines[to.Line], 0, to.Index).ToString();
    }

    /// <summary>
    /// Replaces the text between two places (in either order) with another (lines split by '\n'), leaving the caret
    /// after it. Typing a character, or deleting one, next to the last such edit joins its undo step.
    /// </summary>
    public void Replace(TextPos a, TextPos b, string text)
    {
        if (IsReadOnly)
        {
            return;
        }

        var (from, to) = a <= b ? (a, b) : (b, a);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var removed = GetText(from, to);
        if (removed.Length == 0 && text.Length == 0)
        {
            return;
        }

        var before = (Anchor, Caret);
        var end = Splice(from, to, text);
        Anchor = Caret = end;

        var now = DateTime.UtcNow;
        var merged = false;
        if (_undo.Count > 0 && _redo.Count == 0 && now - _lastEdit < MergeWindow && _undo[^1] is var last && !last.Sealed)
        {
            // Typing on: one more character right after the last ones (a space after a word starts a new step).
            if (removed.Length == 0 && last.Removed.Length == 0 && text.Length == 1 && text != "\n" && last.InsertedEnd == from &&
                !(text == " " && last.Inserted.Length > 0 && last.Inserted[^1] != ' '))
            {
                _undo[^1] = last with { Inserted = last.Inserted + text, InsertedEnd = end, After = (Anchor, Caret) };
                merged = true;
            }

            // Backspacing on: one more character just before the last ones removed.
            else if (text.Length == 0 && last.Inserted.Length == 0 && removed.Length == 1 && removed != "\n" && to == last.Start)
            {
                _undo[^1] = last with { Start = from, InsertedEnd = from, Removed = removed + last.Removed, After = (Anchor, Caret) };
                merged = true;
            }
        }

        if (!merged)
        {
            _undo.Add(new Edit(from, removed, text, end, before, (Anchor, Caret), _version, 0));
        }

        _undo[^1] = _undo[^1] with { VersionAfter = Bump() };
        _redo.Clear();
        _lastEdit = now;
        Notify();
    }

    /// <summary>The next edit starts its own undo step (after the caret moved, say).</summary>
    public void Seal()
    {
        if (_undo.Count > 0 && !_undo[^1].Sealed)
        {
            _undo[^1] = _undo[^1] with { Sealed = true };
        }
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var edit = _undo[^1] with { Sealed = true };
        _undo.RemoveAt(_undo.Count - 1);
        Splice(edit.Start, edit.InsertedEnd, edit.Removed);
        (Anchor, Caret) = edit.Before;
        _version = edit.VersionBefore;
        _redo.Add(edit);
        Notify();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Splice(edit.Start, EndOf(edit.Start, edit.Removed), edit.Inserted);
        (Anchor, Caret) = edit.After;
        _version = edit.VersionAfter;
        _undo.Add(edit);
        Notify();
    }

    /// <summary>The file was written with this text: it is clean again.</summary>
    public void MarkSaved()
    {
        var was = IsDirty;
        _savedVersion = _version;
        Seal();
        if (was)
        {
            DirtyChanged?.Invoke();
        }
    }

    /// <summary>The bytes to write: the text with the file's line breaks, in its encoding, with its byte order mark if it had one.</summary>
    public byte[] Encode()
    {
        var body = Encoding.GetBytes(Text);
        var preamble = HasBom ? Encoding.GetPreamble() : [];
        return [.. preamble, .. body];
    }

    private int Bump() => _version = ++_nextVersion;

    private void Notify()
    {
        Changed?.Invoke();
        DirtyChanged?.Invoke();
    }

    private TextPos Splice(TextPos from, TextPos to, string text)
    {
        Highlighting?.Invalidate(from.Line);
        var head = _lines[from.Line][..from.Index];
        var tail = _lines[to.Line][to.Index..];
        _lines.RemoveRange(from.Line, to.Line - from.Line + 1);
        var parts = text.Split('\n');
        parts[0] = head + parts[0];
        var endIndex = parts[^1].Length;
        parts[^1] += tail;
        _lines.InsertRange(from.Line, parts);
        return new TextPos(from.Line + parts.Length - 1, endIndex);
    }

    private static TextPos EndOf(TextPos start, string text)
    {
        var lastBreak = text.LastIndexOf('\n');
        return lastBreak < 0
            ? start with { Index = start.Index + text.Length }
            : new TextPos(start.Line + text.Count(c => c == '\n'), text.Length - lastBreak - 1);
    }

    private sealed record Edit(
        TextPos Start,
        string Removed,
        string Inserted,
        TextPos InsertedEnd,
        (TextPos Anchor, TextPos Caret) Before,
        (TextPos Anchor, TextPos Caret) After,
        int VersionBefore,
        int VersionAfter,
        bool Sealed = false);
}
