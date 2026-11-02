using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Nomad.App.Ide;

// Reference counts over declarations, as Visual Studio's code lens: a short row of small grey text ("7 references")
// over each declaration the language server counts, which lists them when clicked. The rows sit between the lines,
// so a row's top is its line's place plus the lens rows above it.
public sealed partial class CodeView
{
    private const double LensGrowMs = 200;
    private const double LensFadeMs = 180;

    // Each row's lens height (0 for none, less while it grows in) and the total of them up to and including it,
    // for the rows and lenses they were worked out from.
    private double[]? _lensHeights;
    private double[]? _lensAbove;
    private IReadOnlyList<int>? _lensRows;
    private IReadOnlyList<CodeLensMark>? _lensMarks;
    private bool _lensGrowing;
    private readonly Dictionary<int, CodeLensMark> _lensByRow = [];

    /// <summary>A lens row: a little shorter than a line, as Visual Studio's.</summary>
    private double LensHeight => Math.Round(LineHeight * 0.82);

    /// <summary>Visual Studio's 8 pt beside its 10 pt code.</summary>
    private double LensFontSize => Math.Round(FontSize * 0.85 * 2) / 2;

    private static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// <summary>The lens rows' height over a row, its own lens included.</summary>
    private double LensAbove(int row)
    {
        LayOutLenses(force: false);
        if (_lensAbove is not { Length: > 0 } above)
        {
            return 0;
        }

        return row < 0 ? 0 : above[Math.Min(row, above.Length - 1)];
    }

    private double LensHeightOf(int row) => _lensHeights is { } heights && row >= 0 && row < heights.Length ? heights[row] : 0;

    private double LensTotal => LensAbove(int.MaxValue);

    /// <summary>The row at a height (its lens above it counts as its own); beyond the ends, as if the rows went on.</summary>
    private int RowAt(double y)
    {
        var count = Rows.Count;
        LayOutLenses(force: false);
        if (_lensAbove is null || count == 0)
        {
            return (int)Math.Floor((y - Padding.Top) / LineHeight);
        }

        if (y < Padding.Top)
        {
            return (int)Math.Floor((y - Padding.Top) / LineHeight);
        }

        var bottom = RowTop(count - 1) + LineHeight;
        if (y >= bottom)
        {
            return count + (int)Math.Floor((y - bottom) / LineHeight);
        }

        // The last row whose lens top is at or above the height.
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (RowTop(mid) - LensHeightOf(mid) <= y)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>The row whose text is at a height; -1 over a lens (no name or squiggle is there).</summary>
    private int TextRowAt(double y)
    {
        var row = RowAt(y);
        return row >= 0 && row < Rows.Count && y < RowTop(row) ? -1 : row;
    }

    // Worked out again when the rows (an edit, a fold) or the lenses change; forced while a row grows in.
    private void LayOutLenses(bool force)
    {
        var rows = Rows;
        var marks = _doc.Lenses;
        if (!force && ReferenceEquals(rows, _lensRows) && ReferenceEquals(marks, _lensMarks))
        {
            return;
        }

        _lensRows = rows;
        _lensMarks = marks;
        _lensByRow.Clear();
        _lensGrowing = false;
        if (marks.Count == 0 || rows.Count == 0)
        {
            (_lensHeights, _lensAbove) = (null, null);
            return;
        }

        var heights = new double[rows.Count];
        var now = DateTime.UtcNow;
        foreach (var mark in marks)
        {
            // A lens over a line folded away doesn't show.
            var row = _doc.Folding.RowOf(Math.Min(mark.Line, _lines.Count - 1));
            if (row >= rows.Count || rows[row] != mark.Line)
            {
                continue;
            }

            var grown = (now - mark.Since).TotalMilliseconds / LensGrowMs;
            _lensGrowing |= grown < 1;
            heights[row] = Math.Round(LensHeight * EaseOut(grown), 2);
            _lensByRow[row] = mark;
        }

        var above = new double[rows.Count];
        var total = 0.0;
        for (var r = 0; r < rows.Count; r++)
        {
            total += heights[r];
            above[r] = total;
        }

        (_lensHeights, _lensAbove) = (heights, above);
    }

    private void OnLensesChanged() => RelayLenses();

    // The rows laid out again with the lenses as they are now, the code in view kept where it is on screen.
    private void RelayLenses()
    {
        var scroller = this.FindAncestorOfType<ScrollViewer>();
        var anchor = _viewport.Height > 0 && _viewport.Top > Padding.Top ? TextRowAt(_viewport.Top + LineHeight) : -1;
        var before = anchor >= 0 ? RowTop(anchor) : 0;

        LayOutLenses(force: true);
        InvalidateMeasure();
        InvalidateVisual();
        if (anchor >= 0 && scroller is not null && RowTop(anchor) - before is var delta && Math.Abs(delta) > 0.01)
        {
            UpdateLayout();
            scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + delta);
        }

        if (_lensGrowing)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => RelayLenses());
        }
    }

    // ------------------------------------------------------------------ drawing

    private readonly Dictionary<(string, bool), TextLayout> _lensLayouts = [];

    // A title laid out in the UI's font, grey (or, lit, in the text's color).
    private TextLayout LensLayout(string title, bool lit = false)
    {
        if (!_lensLayouts.TryGetValue((title, lit), out var layout))
        {
            var font = this.TryFindResource("UiFont", ActualThemeVariant, out var value) && value is FontFamily family ? family : FontFamily.Default;
            var brush = lit ? Foreground : ThemeBrush("TextMutedBrush") ?? Foreground;
            layout = new TextLayout(title, new Typeface(font), LensFontSize, brush);
            _lensLayouts[(title, lit)] = layout;
        }

        return layout;
    }

    private void ClearLensLayouts()
    {
        foreach (var layout in _lensLayouts.Values)
        {
            layout.Dispose();
        }

        _lensLayouts.Clear();
    }

    /// <summary>Where a row's lens text is: at its line's indentation, in the middle of the lens row.</summary>
    private Rect LensRect(int row, CodeLensMark mark)
    {
        var line = _lines[mark.Line];
        var indent = line.Length - line.TrimStart(' ', '\t').Length;
        var x = TextLeft + Column(new TextPos(mark.Line, indent)) * CharWidth;
        var width = mark.Title is { } title ? LensLayout(title).WidthIncludingTrailingWhitespace : 0;
        var height = LensHeightOf(row);
        return new Rect(x - 3, RowTop(row) - height, width + 6, height);
    }

    // The lenses of the rows in view (and the one just under it), over the scene, clipped to the text's side.
    private void DrawLenses(DrawingContext context, Rect view, int firstRow, int lastRow)
    {
        if (_lensByRow.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var animating = _lensGrowing;
        using (context.PushClip(new Rect(view.Left + TextLeft - 4, view.Top, Math.Max(0, view.Width - TextLeft + 4), view.Height)))
        {
            for (var r = firstRow; r <= Math.Min(lastRow + 1, Rows.Count - 1); r++)
            {
                if (!_lensByRow.TryGetValue(r, out var mark) || mark.Title is not { } title)
                {
                    continue;
                }

                var shown = EaseOut((now - mark.Counted).TotalMilliseconds / LensFadeMs) * (LensHeightOf(r) / LensHeight);
                animating |= shown < 1;
                DrawLens(context, r, mark, title, shown);
            }
        }

        if (animating)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => InvalidateVisual());
        }
    }

    private void DrawLens(DrawingContext context, int row, CodeLensMark mark, string title, double shown)
    {
        var rect = LensRect(row, mark);
        var hovered = mark.Line == _lensHoverLine ? _lensHoverLevel : 0;
        using (context.PushOpacity(shown))
        {
            if (hovered > 0 && ThemeBrush("BgHoverBrush") is { } hover)
            {
                using (context.PushOpacity(hovered))
                {
                    context.DrawRectangle(hover, null, rect.Deflate(new Thickness(0, 1)), 3, 3);
                }
            }

            // Grey, lighting up to the text's color under the pointer.
            var layout = LensLayout(title);
            var at = new Point(rect.X + 3, rect.Y + (rect.Height - layout.Height) / 2);
            using (context.PushOpacity(1 - hovered))
            {
                layout.Draw(context, at);
            }

            if (hovered > 0)
            {
                using (context.PushOpacity(hovered))
                {
                    LensLayout(title, lit: true).Draw(context, at);
                }
            }
        }
    }
}

// The lens under the pointer: lit, a tooltip after a moment, and a click lists the references.
public sealed partial class CodeView
{
    private const double LensHoverMs = 120;

    private int _lensHoverLine = -1;
    private double _lensHoverLevel;
    private double _lensHoverFrom;
    private double _lensHoverTarget;
    private long _lensHoverStart;
    private bool _lensHoverRunning;
    private DispatcherTimer? _lensTipTimer;
    private CodeLensList? _lensList;

    private CodeLensList LensList
    {
        get
        {
            if (_lensList is null)
            {
                _lensList = new CodeLensList(this);
                LogicalChildren.Add(_lensList.Popup);
            }

            return _lensList;
        }
    }

    /// <summary>The counted lens whose text is under a point (and its row), if one is.</summary>
    private (int Row, CodeLensMark Mark)? LensAt(Point point)
    {
        var row = RowAt(point.Y);
        return _lensByRow.TryGetValue(row, out var mark) && mark.Title is not null && LensRect(row, mark).Contains(point) ? (row, mark) : null;
    }

    /// <summary>The pointer moved: the lens under it lit (another one's light goes at once); true if it is on one.</summary>
    private bool HoverLens(Point point)
    {
        var mark = LensAt(point)?.Mark;
        var line = mark?.Line ?? -1;
        if (mark is not null && line != _lensHoverLine)
        {
            _lensHoverLevel = 0;
            _lensHoverLine = line;
            FadeLensHover(1);
            StartLensTip(LensTip(mark));
        }
        else if (line < 0 && _lensHoverLine >= 0 && _lensHoverTarget > 0)
        {
            FadeLensHover(0);
            StopLensTip();
        }

        return line >= 0;
    }

    private void FadeLensHover(double target)
    {
        (_lensHoverFrom, _lensHoverTarget, _lensHoverStart) = (_lensHoverLevel, target, System.Diagnostics.Stopwatch.GetTimestamp());
        if (!_lensHoverRunning)
        {
            _lensHoverRunning = true;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(LensHoverFrame);
        }
    }

    private void LensHoverFrame(TimeSpan _)
    {
        var t = System.Diagnostics.Stopwatch.GetElapsedTime(_lensHoverStart).TotalMilliseconds / LensHoverMs;
        _lensHoverLevel = _lensHoverFrom + (_lensHoverTarget - _lensHoverFrom) * EaseOut(t);
        InvalidateVisual();
        if (t < 1)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(LensHoverFrame);
            return;
        }

        _lensHoverRunning = false;
        if (_lensHoverTarget == 0)
        {
            _lensHoverLine = -1;
        }
    }

    // "This method has 7 reference(s).", as Visual Studio's tooltip says.
    private string LensTip(CodeLensMark mark)
    {
        var count = new string([.. (mark.Title ?? "").TakeWhile(char.IsDigit)]);
        return $"This {LensKind(_lines[mark.Line], mark.Column)} has {(count.Length > 0 ? count : "no")} reference(s).";
    }

    // What a declaration is, from its line: the keyword before its name, or what follows it.
    private static string LensKind(string line, int column)
    {
        var before = " " + line[..Math.Min(column, line.Length)];
        foreach (var kind in (string[])["class", "record", "struct", "interface", "enum", "delegate", "event"])
        {
            if (before.Contains($" {kind} ", StringComparison.Ordinal))
            {
                return kind;
            }
        }

        var after = line[Math.Min(column, line.Length)..].TrimStart(' ', '\t');
        var name = new string([.. after.TakeWhile(c => char.IsLetterOrDigit(c) || c == '_')]);
        var rest = after[name.Length..].TrimStart();
        return rest.StartsWith('(') || rest.StartsWith('<') ? "method"
            : rest.StartsWith('{') || rest.StartsWith("=>", StringComparison.Ordinal) || rest.Length == 0 ? "property"
            : "member";
    }

    private void StartLensTip(string text)
    {
        StopLensTip();
        _lensTipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _lensTipTimer.Tick += (_, _) =>
        {
            _lensTipTimer?.Stop();
            ToolTip.SetTip(this, text);
            ToolTip.SetIsOpen(this, true);
        };
        _lensTipTimer.Start();
    }

    private void StopLensTip()
    {
        _lensTipTimer?.Stop();
        _lensTipTimer = null;
        if (ToolTip.GetTip(this) is not null)
        {
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, null);
        }
    }

    /// <summary>A click on a lens lists where its declaration is used, under it; false if it wasn't on one.</summary>
    private bool LensClick(Point point)
    {
        if (LensAt(point) is not { } at || Navigator is not { } navigator)
        {
            return false;
        }

        StopLensTip();
        var (row, mark) = at;
        var line = _lines[mark.Line];
        var name = new string([.. line[Math.Min(mark.Column, line.Length)..].TakeWhile(c => char.IsLetterOrDigit(c) || c == '_')]);
        var anchor = WindowAnchor(LensRect(row, mark));
        var isStruct = _doc.StructNamesOn(mark.Line).Any(t => t.Column == mark.Column);
        LensList.Show(anchor, name, isStruct, navigator, navigator.UsesAsync(mark.Line, mark.Column, CancellationToken.None));
        return true;
    }

    /// <summary>Esc closes an open reference list; true if one was.</summary>
    private bool CloseLensList()
    {
        if (_lensList is not { IsOpen: true } list)
        {
            return false;
        }

        list.Close();
        return true;
    }
}
