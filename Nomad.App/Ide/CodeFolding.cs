namespace Nomad.App.Ide;

/// <summary>A block that folds: its first line stays shown, the lines after it up to <see cref="End"/> hide.</summary>
public readonly record struct FoldRegion(int Start, int End);

/// <summary>
/// A document's foldable blocks and which of them are folded, as Visual Studio's outlining. The blocks come from the
/// indentation, the way VS Code finds them without a language service: a line followed by more deeply indented ones
/// starts a block, which runs to the last of them and takes in a closing line at its own depth (<c>}</c>, <c>]</c>,
/// <c>&lt;/Tag&gt;</c>). A block whose first line is only an opening brace starts on the line before it, so a method
/// folds onto its signature. Worked out again (lazily) after every edit; folded blocks follow their lines.
/// </summary>
public sealed class CodeFolding
{
    private const int TabSize = 4;

    private readonly SortedSet<int> _folded = [];
    private List<FoldRegion> _regions = [];
    private Dictionary<int, FoldRegion> _byStart = [];
    private List<int>? _rows;
    private bool _stale = true;
    private IReadOnlyList<string> _lines = [];

    /// <summary>Folding changed (a block folded or unfolded): the view lays out its rows again.</summary>
    public event Action? Changed;

    public void Attach(IReadOnlyList<string> lines)
    {
        _lines = lines;
        _stale = true;
        _rows = null;
    }

    public IReadOnlyList<FoldRegion> Regions
    {
        get
        {
            Update();
            return _regions;
        }
    }

    /// <summary>The block that starts on this line, if one does.</summary>
    public FoldRegion? RegionAt(int line)
    {
        Update();
        return _byStart.TryGetValue(line, out var region) ? region : null;
    }

    public bool IsFolded(int line) => _folded.Contains(line);

    public bool HasFolded => _folded.Count > 0;

    /// <summary>The document lines shown, in order (all of them when nothing is folded).</summary>
    public IReadOnlyList<int> Rows
    {
        get
        {
            Update();
            if (_rows is null)
            {
                _rows = new List<int>(_lines.Count);
                var hiddenTo = -1;
                for (var line = 0; line < _lines.Count; line++)
                {
                    if (line <= hiddenTo)
                    {
                        continue;
                    }

                    _rows.Add(line);
                    if (_folded.Contains(line) && _byStart.TryGetValue(line, out var region))
                    {
                        hiddenTo = region.End;
                    }
                }
            }

            return _rows;
        }
    }

    /// <summary>The row a line is shown on (a hidden line: its folded block's first line's).</summary>
    public int RowOf(int line)
    {
        var rows = Rows;
        var index = BinarySearch(rows, line);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    private static int BinarySearch(IReadOnlyList<int> rows, int line)
    {
        int lo = 0, hi = rows.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (rows[mid] == line)
            {
                return mid;
            }

            if (rows[mid] < line)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ~lo;
    }

    /// <summary>Folds or unfolds the block starting on this line; false if none starts there.</summary>
    public bool Toggle(int line)
    {
        Update();
        if (!_byStart.ContainsKey(line))
        {
            return false;
        }

        if (!_folded.Remove(line))
        {
            _folded.Add(line);
        }

        Invalidated();
        return true;
    }

    /// <summary>The innermost block holding this line (or starting on it).</summary>
    public FoldRegion? Innermost(int line)
    {
        FoldRegion? best = null;
        foreach (var region in Regions)
        {
            if (region.Start > line)
            {
                break;
            }

            if (line <= region.End && (best is null || region.Start >= best.Value.Start))
            {
                best = region;
            }
        }

        return best;
    }

    /// <summary>Ctrl+M, Ctrl+L: unfolds everything if anything is folded, else folds every block.</summary>
    public void ToggleAll()
    {
        if (_folded.Count > 0)
        {
            _folded.Clear();
        }
        else
        {
            foreach (var region in Regions)
            {
                _folded.Add(region.Start);
            }
        }

        Invalidated();
    }

    /// <summary>Unfolds whatever hides this line (the caret went there); true if anything did.</summary>
    public bool Reveal(int line)
    {
        if (_folded.Count == 0)
        {
            return false;
        }

        Update();
        var any = false;
        foreach (var start in _folded.Where(s => s < line).ToList())
        {
            if (_byStart.TryGetValue(start, out var region) && line <= region.End)
            {
                _folded.Remove(start);
                any = true;
            }
        }

        if (any)
        {
            Invalidated();
        }

        return any;
    }

    /// <summary>
    /// Lines from <paramref name="line"/> were rewritten (<paramref name="removed"/> of them by <paramref name="added"/>):
    /// folded blocks below move with their lines, and those that started inside the rewritten ones unfold.
    /// </summary>
    public void OnSplice(int line, int removed, int added)
    {
        _stale = true;
        _rows = null;
        if (_folded.Count == 0)
        {
            return;
        }

        var delta = added - removed;
        var moved = new List<int>();
        foreach (var start in _folded)
        {
            if (start <= line)
            {
                moved.Add(start);
            }
            else if (start >= line + removed)
            {
                moved.Add(start + delta);
            }
        }

        _folded.Clear();
        _folded.UnionWith(moved);
    }

    private void Invalidated()
    {
        _rows = null;
        Changed?.Invoke();
    }

    private void Update()
    {
        if (!_stale)
        {
            return;
        }

        _stale = false;
        _rows = null;
        _regions = Find(_lines);
        _byStart = _regions.ToDictionary(r => r.Start);

        // Folded blocks that no longer are (their indentation changed) unfold.
        _folded.RemoveWhere(start => !_byStart.ContainsKey(start));
    }

    private static List<FoldRegion> Find(IReadOnlyList<string> lines)
    {
        var regions = new List<FoldRegion>();
        var open = new Stack<(int Indent, int Line)>();
        var lastText = -1;
        for (var i = 0; i <= lines.Count; i++)
        {
            var atEnd = i == lines.Count;
            var indent = atEnd ? -1 : Indent(lines[i]);
            if (indent < 0 && !atEnd)
            {
                continue; // A blank line belongs to whatever surrounds it.
            }

            // Lines at this depth or shallower end the blocks of the deeper (or same) lines before them.
            var closer = !atEnd && IsCloser(lines[i]);
            while (open.Count > 0 && open.Peek().Indent >= indent)
            {
                var (depth, start) = open.Pop();
                if (lastText > start) // It has lines under it.
                {
                    Add(regions, lines, start, closer && depth == indent ? i : lastText);
                }
            }

            if (!atEnd)
            {
                open.Push((indent, i));
                lastText = i;
            }
        }

        regions.Sort((a, b) => a.Start.CompareTo(b.Start));

        // One block per first line (a brace line moved onto its signature can meet a block already there).
        var unique = new List<FoldRegion>(regions.Count);
        foreach (var region in regions)
        {
            if (unique.Count > 0 && unique[^1].Start == region.Start)
            {
                unique[^1] = unique[^1].End >= region.End ? unique[^1] : region;
            }
            else
            {
                unique.Add(region);
            }
        }

        return unique;
    }

    private static void Add(List<FoldRegion> regions, IReadOnlyList<string> lines, int start, int end)
    {
        // A block opened by a brace alone folds onto the line before it: "void M()" then "{".
        var trimmed = lines[start].Trim();
        if (trimmed is "{" or "[" or "(" && start > 0 && lines[start - 1].Trim().Length > 0 &&
            Indent(lines[start - 1]) == Indent(lines[start]))
        {
            start--;
        }

        if (end > start)
        {
            regions.Add(new FoldRegion(start, end));
        }
    }

    // A line that only closes what is above it: it folds away with its block.
    private static bool IsCloser(string line)
    {
        var t = line.TrimStart();
        return t.Length > 0 && (t[0] is '}' or ']' or ')' || t.StartsWith("</", StringComparison.Ordinal) ||
                                t.StartsWith("#endregion", StringComparison.Ordinal) || t.StartsWith("end", StringComparison.Ordinal) && (t.Length == 3 || !char.IsLetterOrDigit(t[3])));
    }

    /// <summary>A line's indentation in columns (tabs to four), or -1 for a blank line.</summary>
    private static int Indent(string line)
    {
        var col = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                col++;
            }
            else if (c == '\t')
            {
                col += TabSize - col % TabSize;
            }
            else
            {
                return col;
            }
        }

        return -1;
    }
}
