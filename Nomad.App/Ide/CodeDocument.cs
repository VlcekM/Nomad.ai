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
        _changed.AddRange(Enumerable.Repeat(false, _lines.Count));
        Folding.Attach(_lines);
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

    /// <summary>Its foldable blocks and which are folded.</summary>
    public CodeFolding Folding { get; } = new();

    /// <summary>Its syntax highlighting, when its language has a grammar.</summary>
    public CodeHighlighting? Highlighting { get; set; }

    public TextPos Anchor { get; set; }

    public TextPos Caret
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                CaretChanged?.Invoke();
            }
        }
    }

    /// <summary>The caret moved (the editor's navigation bar follows it).</summary>
    public event Action? CaretChanged;

    public bool IsDirty => _version != _savedVersion;

    /// <summary>The text changed (an edit, undo or redo).</summary>
    public event Action? Changed;

    /// <summary>Whether it now differs from the file as last saved or loaded.</summary>
    public event Action? DirtyChanged;

    /// <summary>
    /// The caret was put somewhere to be shown (<see cref="GoTo"/>, <see cref="Select"/>): the view scrolls it to the
    /// middle, and takes the focus when the argument says so.
    /// </summary>
    public event Action<bool>? RevealRequested;

    /// <summary>Puts the caret at a place (no selection) and has the view show it and take the focus, as a Go To does.</summary>
    public void GoTo(TextPos pos)
    {
        Anchor = Caret = Clamp(pos);
        RevealRequested?.Invoke(true);
    }

    /// <summary>Selects a range and has the view show it (a find's match: the focus stays in the find box).</summary>
    public void Select(TextPos from, TextPos to, bool focus = false)
    {
        Anchor = Clamp(from);
        Caret = Clamp(to);
        RevealRequested?.Invoke(focus);
    }

    /// <summary>The find's matches, picked out in the text and beside the scroll bar; <see cref="CurrentMark"/> is the one at hand (-1 for none).</summary>
    public IReadOnlyList<(TextPos From, TextPos To)> Marks { get; private set; } = [];

    public int CurrentMark { get; private set; } = -1;

    /// <summary><see cref="Marks"/> changed.</summary>
    public event Action? MarksChanged;

    public void SetMarks(IReadOnlyList<(TextPos From, TextPos To)> marks, int current)
    {
        Marks = marks;
        CurrentMark = current;
        MarksChanged?.Invoke();
    }

    /// <summary>How the text differs from the file as last committed (the gutter's git margin); null outside git.</summary>
    public IReadOnlyList<Nomad.Core.Code.DiffHunk>? GitHunks { get; private set; }

    public void SetGitHunks(IReadOnlyList<Nomad.Core.Code.DiffHunk>? hunks)
    {
        GitHunks = hunks;
        MarksChanged?.Invoke();
    }

    /// <summary>Lines another program just changed (the file reloaded under the editor): they flash, and count as changed.</summary>
    public IReadOnlyList<int> FlashLines { get; private set; } = [];

    /// <summary>When <see cref="FlashLines"/> were set.</summary>
    public DateTime FlashedUtc { get; private set; }

    public void Flash(IReadOnlyList<int> lines)
    {
        foreach (var line in lines)
        {
            if (line >= 0 && line < _changed.Count)
            {
                _changed[line] = true;
            }
        }

        FlashLines = lines;
        FlashedUtc = DateTime.UtcNow;
        MarksChanged?.Invoke();
    }

    // The edits between BeginGroup and EndGroup undo and redo as one (several carets typing, a Replace All).
    private int _group;
    private int _nextGroup;

    public void BeginGroup()
    {
        Seal();
        _group = ++_nextGroup;
    }

    public void EndGroup()
    {
        _group = 0;
        Seal();
    }

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
        var (end, flags) = Splice(from, to, text);
        Anchor = Caret = end;

        var now = DateTime.UtcNow;
        var merged = false;
        if (_group == 0 && _undo.Count > 0 && _redo.Count == 0 && now - _lastEdit < MergeWindow && _undo[^1] is var last && !last.Sealed)
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
            _undo.Add(new Edit(from, removed, text, end, before, (Anchor, Caret), _version, 0, flags, Group: _group));
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

        // A group comes off whole, last edit first.
        var group = _undo[^1].Group;
        do
        {
            var edit = _undo[^1] with { Sealed = true };
            _undo.RemoveAt(_undo.Count - 1);
            Splice(edit.Start, edit.InsertedEnd, edit.Removed, edit.RemovedChanged);
            (Anchor, Caret) = edit.Before;
            _version = edit.VersionBefore;
            _redo.Add(edit);
        }
        while (group != 0 && _undo.Count > 0 && _undo[^1].Group == group);

        Notify();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        // A group goes back on whole, first edit first (as undo left them).
        var group = _redo[^1].Group;
        do
        {
            var edit = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            Splice(edit.Start, EndOf(edit.Start, edit.Removed), edit.Inserted);
            (Anchor, Caret) = edit.After;
            _version = edit.VersionAfter;
            _undo.Add(edit);
        }
        while (group != 0 && _redo.Count > 0 && _redo[^1].Group == group);

        Notify();
    }

    /// <summary>The file was written with this text: it is clean again.</summary>
    public void MarkSaved()
    {
        var was = IsDirty;
        _savedVersion = _version;
        Seal();

        // Undoing or redoing past this point now leads away from the saved text: those lines will count as changed.
        for (var i = 0; i < _undo.Count; i++)
        {
            _undo[i] = _undo[i] with { RemovedChanged = null };
        }

        for (var i = 0; i < _redo.Count; i++)
        {
            _redo[i] = _redo[i] with { RemovedChanged = null };
        }

        for (var i = 0; i < _changed.Count; i++)
        {
            _changed[i] = false;
        }

        Changed?.Invoke(); // The change bar goes.
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

    /// <summary>
    /// Writes <paramref name="text"/> over the range. Its lines count as changed, unless <paramref name="changed"/> says
    /// otherwise (an undo putting back lines as they were); returns where it ends, and the removed lines' changed marks.
    /// </summary>
    private (TextPos End, bool[] Removed) Splice(TextPos from, TextPos to, string text, bool[]? changed = null)
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

        // The change bar: the lines it wrote are changed ones now (or as they were, for an undo).
        var removedChanged = _changed.GetRange(from.Line, to.Line - from.Line + 1).ToArray();
        _changed.RemoveRange(from.Line, to.Line - from.Line + 1);
        _changed.InsertRange(from.Line, changed is { } marks && marks.Length == parts.Length ? marks : Enumerable.Repeat(true, parts.Length));
        Folding.OnSplice(from.Line, to.Line - from.Line + 1, parts.Length);
        return (new TextPos(from.Line + parts.Length - 1, endIndex), removedChanged);
    }

    private readonly List<bool> _changed = [];

    /// <summary>Edited since the file was opened or last saved (the editor's change bar).</summary>
    public bool IsLineChanged(int line) => line < _changed.Count && _changed[line];

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
        bool[]? RemovedChanged,
        bool Sealed = false,
        int Group = 0);
}
