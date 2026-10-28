using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Nomad.App.Controls;

/// <summary>
/// Find in the chat (Ctrl+F): marks every match in the text the transcript shows, drawn over the
/// <see cref="Scroller"/> it sits on (hit-test invisible). The current match has a stronger mark that glides to
/// the next one (180 ms, cubic ease-out), on render frames only while it moves.
/// </summary>
public sealed class FindHighlights : Control
{
    /// <summary>A match: a stretch of one text block's text (the indices of its text layout).</summary>
    public readonly record struct Match(TextBlock Block, int Start, int Length);

    private const double GlideMs = 180;

    private readonly List<Match> _matches = [];
    private Rect? _glideFrom;
    private TimeSpan? _glideStart;
    private double _glide = 1;
    private bool _framePending;

    /// <summary>The control whose text blocks are searched (the transcript).</summary>
    public Control? Source { get; set; }

    /// <summary>The scroller the source scrolls in: matches are cut off at its edges, and its moves redraw them.</summary>
    public ScrollViewer? Scroller { get; set; }

    public IReadOnlyList<Match> Matches => _matches;

    public int Current { get; private set; } = -1;

    public FindHighlights()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Counts <paramref name="query"/> in <paramref name="text"/> the way the marks find it (case ignored).</summary>
    public static int Count(string text, string query)
    {
        var count = 0;
        for (var at = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); at >= 0; at = text.IndexOf(query, at + query.Length, StringComparison.CurrentCultureIgnoreCase))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Marks <paramref name="query"/> in the text blocks the source has now (a virtualized list has only those near
    /// the view). No match is current until <see cref="Pick"/> chooses one.
    /// </summary>
    public void Search(string query)
    {
        _matches.Clear();
        Current = -1;
        if (query.Length > 0 && Source is not null)
        {
            foreach (var block in Source.GetVisualDescendants().OfType<TextBlock>())
            {
                if (!block.IsEffectivelyVisible || InButton(block) || Text(block) is not { Length: > 0 } text)
                {
                    continue;
                }

                for (var at = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); at >= 0; at = text.IndexOf(query, at + query.Length, StringComparison.CurrentCultureIgnoreCase))
                {
                    _matches.Add(new Match(block, at, query.Length));
                }
            }
        }

        InvalidateVisual();
    }

    public void Clear() => Search("");

    /// <summary>
    /// Makes the <paramref name="occurrence"/>-th match inside <paramref name="container"/> current (its last, if it
    /// shows fewer); with <paramref name="glide"/> the mark glides there from the one before. False if it has none.
    /// </summary>
    public bool Pick(Visual container, int occurrence, bool glide)
    {
        var inside = Enumerable.Range(0, _matches.Count).Where(i => _matches[i].Block.GetVisualAncestors().Contains(container)).ToList();
        var from = glide ? CurrentRect() : null;
        var next = inside.Count == 0 ? -1 : inside[Math.Clamp(occurrence, 0, inside.Count - 1)];
        if (from is not null && next >= 0 && next != Current)
        {
            _glideFrom = from;
            _glideStart = null;
            _glide = 0;
            RequestFrame();
        }

        Current = next;
        InvalidateVisual();
        return next >= 0;
    }
    /// <summary>The current match's rectangle (its first line's) in this control's coordinates, or another visual's.</summary>
    public Rect? CurrentRect(Visual? relativeTo = null) =>
        Current >= 0 && Current < _matches.Count ? Rects(_matches[Current], relativeTo ?? this).Select(r => (Rect?)r).FirstOrDefault() : null;

    private static string? Text(TextBlock block) => block.Inlines is { Count: > 0 } inlines ? inlines.Text : block.Text;

    private static bool InButton(Visual visual)
    {
        for (var parent = visual.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
        {
            if (parent is Button)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Rect> Rects(Match match, Visual relativeTo)
    {
        var block = match.Block;
        if (!block.IsEffectivelyVisible || block.TranslatePoint(default, relativeTo) is not { } origin)
        {
            yield break;
        }

        var text = Text(block);
        if (text is null || match.Start + match.Length > text.Length)
        {
            yield break;
        }

        var padding = block.Padding;
        foreach (var rect in block.TextLayout.HitTestTextRange(match.Start, match.Length))
        {
            yield return rect.Translate(new Vector(origin.X + padding.Left, origin.Y + padding.Top));
        }
    }

    /// <summary>What of this control the block can be seen in: the scrollers and clipped panels around it.</summary>
    private Rect Visible(TextBlock block)
    {
        var visible = new Rect(Bounds.Size);
        for (var visual = block.GetVisualParent(); visual is not null; visual = visual.GetVisualParent())
        {
            if (visual.ClipToBounds && visual.TranslatePoint(default, this) is { } at)
            {
                visible = visible.Intersect(new Rect(at, visual.Bounds.Size));
            }

            if (ReferenceEquals(visual, Scroller))
            {
                break;
            }
        }

        return visible;
    }

    public override void Render(DrawingContext context)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        var match = Brush("FindMatchBrush", "#40D2784B");
        var current = Brush("FindCurrentBrush", "#80D2784B");
        var edge = new Pen(Brush("AccentBrush", "#D2784B"), 1);
        var clips = new Dictionary<TextBlock, Rect>();
        for (var i = 0; i < _matches.Count; i++)
        {
            if (i == Current)
            {
                continue;
            }

            var block = _matches[i].Block;
            if (!clips.TryGetValue(block, out var visible))
            {
                visible = clips[block] = Visible(block);
            }

            foreach (var rect in Rects(_matches[i], this))
            {
                if (rect.Intersects(visible))
                {
                    using (context.PushClip(visible))
                    {
                        context.DrawRectangle(match, null, new RoundedRect(Box(rect), 3));
                    }
                }
            }
        }

        if (Current < 0 || Current >= _matches.Count)
        {
            return;
        }

        var currentBlock = _matches[Current].Block;
        var clip = clips.TryGetValue(currentBlock, out var known) ? known : Visible(currentBlock);
        var rects = Rects(_matches[Current], this).ToList();
        if (rects.Count == 0)
        {
            return;
        }

        // Mid-glide the mark is on its way from the last match: one box, eased between the two.
        if (_glide < 1 && _glideFrom is { } from)
        {
            var to = rects[0];
            var eased = 1 - Math.Pow(1 - _glide, 3);
            rects = [new Rect(Lerp(from.X, to.X, eased), Lerp(from.Y, to.Y, eased), Lerp(from.Width, to.Width, eased), Lerp(from.Height, to.Height, eased))];
            clip = new Rect(Bounds.Size);
        }

        foreach (var rect in rects)
        {
            using (context.PushClip(clip))
            {
                context.DrawRectangle(current, edge, new RoundedRect(Box(rect).Deflate(0.5), 3));
            }
        }
    }

    private static Rect Box(Rect rect) => rect.Inflate(new Thickness(2, 0));

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private void RequestFrame()
    {
        if (_framePending || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _framePending = true;
        top.RequestAnimationFrame(time =>
        {
            _framePending = false;
            _glideStart ??= time;
            _glide = Math.Clamp((time - _glideStart.Value).TotalMilliseconds / GlideMs, 0, 1);
            InvalidateVisual();
            if (_glide < 1)
            {
                RequestFrame();
            }
        });
    }

    private static IBrush Brush(string key, string fallback) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));
}
