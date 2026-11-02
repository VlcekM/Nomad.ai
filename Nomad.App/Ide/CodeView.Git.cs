using Avalonia;
using Nomad.Core.Code;

namespace Nomad.App.Ide;

// The git margin's peek, as Visual Studio's: a click on a change's bar (or a removal's notch) shows what it changed
// since the last commit, under it.
public sealed partial class CodeView
{
    // The margin left of the change bar takes the click: the bar itself is a few pixels, too thin to aim at.
    private const double GitClickWidth = BarLeft;

    private CodeGitPeek? _gitPeek;

    private CodeGitPeek GitPeek
    {
        get
        {
            if (_gitPeek is null)
            {
                _gitPeek = new CodeGitPeek(this);
                LogicalChildren.Add(_gitPeek.Popup);
            }

            return _gitPeek;
        }
    }

    /// <summary>A click on the git margin peeks at the change there; false if it wasn't on one.</summary>
    private bool GitClick(Point point)
    {
        if (GitHunkAt(point) is not { } hunk || _doc.GitBase is not { } head)
        {
            return false;
        }

        var removed = head.Skip(hunk.OldStart).Take(hunk.OldCount).ToList();
        var added = _lines.Skip(hunk.NewStart).Take(hunk.NewCount).ToList();
        var view = _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        GitPeek.Show(WindowAnchor(GitPeekAnchor(hunk, view)), view.Width - TextLeft - 24, removed, added, _doc.Highlighting?.Grammar,
            Navigator is { } navigator ? navigator.ShowDiff : null);
        return true;
    }

    // The change whose bar (or notch) is on the clicked row.
    private DiffHunk? GitHunkAt(Point point)
    {
        var view = _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        var rows = Rows;
        var row = TextRowAt(point.Y);
        if (_doc.GitHunks is not { Count: > 0 } hunks || point.X < view.Left || point.X > view.Left + GitClickWidth || row < 0 || row >= rows.Count)
        {
            return null;
        }

        var line = rows[row];
        foreach (var hunk in hunks)
        {
            var on = hunk.NewCount > 0
                ? line >= hunk.NewStart && line < hunk.NewStart + hunk.NewCount
                : line == hunk.NewStart || line == hunk.NewStart - 1;
            if (on)
            {
                return hunk;
            }
        }

        return null;
    }

    // Under the change's last line, from the text's left edge.
    private Rect GitPeekAnchor(DiffHunk hunk, Rect view)
    {
        var last = Math.Clamp(hunk.NewStart + Math.Max(hunk.NewCount, 1) - 1, 0, _lines.Count - 1);
        var row = _doc.Folding.RowOf(last);
        return new Rect(view.Left + TextLeft, RowTop(row), 1, LineHeight);
    }

    /// <summary>Esc closes an open git peek; true if one was.</summary>
    private bool CloseGitPeek()
    {
        if (_gitPeek is not { IsOpen: true } peek)
        {
            return false;
        }

        peek.Close();
        return true;
    }
}
