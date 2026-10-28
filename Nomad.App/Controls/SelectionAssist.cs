using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Nomad.App.Markdown;

namespace Nomad.App.Controls;

/// <summary>
/// Browser-like text selection over a panel of <see cref="SelectableTextBlock"/>s: a press beside a block's text (in
/// the margin to its right, between lines or paragraphs, in a code block's padding) starts a selection in the nearest
/// block, and a drag (from there or from the text itself) that leaves its block selects across the blocks it passes,
/// as it would in a browser; copying it copies all of them. Presses on buttons and boxes are left alone.
/// </summary>
public static class SelectionAssist
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(SelectionAssist));

    /// <summary>The block the selection started in (it keeps the focus), and where in it.</summary>
    private static SelectableTextBlock? _block;
    private static int _anchor;
    private static Point _press;
    private static Control? _panel;

    /// <summary>A left button drag is going on; <see cref="_ownsCapture"/> when it began beside the text (not on it).</summary>
    private static bool _dragging;
    private static bool _ownsCapture;

    /// <summary>The blocks of a selection over several blocks, in reading order (empty while it is in one block).</summary>
    private static readonly List<SelectableTextBlock> _span = [];

    static SelectionAssist()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((panel, e) =>
        {
            panel.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            panel.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            panel.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            panel.RemoveHandler(SelectableTextBlock.CopyingToClipboardEvent, OnCopying);
            if (e.NewValue is true)
            {
                panel.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
                panel.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
                panel.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
                panel.AddHandler(SelectableTextBlock.CopyingToClipboardEvent, OnCopying, RoutingStrategies.Bubble, handledEventsToo: true);
            }
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    /// <summary>A selection this helper is dragging or made (the page's click-to-focus-the-composer leaves it alone).</summary>
    public static bool IsSelecting => _block is not null;

    /// <summary>Whether this block is part of a selection over several blocks (whose copy this helper makes).</summary>
    public static bool IsSpanning(SelectableTextBlock block) => _span.Contains(block);

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control panel || !e.GetCurrentPoint(panel).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ClearSpan();
        _block = null;
        _dragging = false;
        if (e.ClickCount != 1 || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var point = e.GetPosition(panel);
        var text = TextTarget(e.Source as Visual, panel, out var own);
        if (own)
        {
            return;
        }

        // On the text, the block places the caret and drags inside itself; this only follows the drag out of it.
        if (text is not null)
        {
            if (Index(text, e.GetPosition(text)) is { } at)
            {
                Start(panel, text, at, point, ownsCapture: false);
            }

            return;
        }

        if (Nearest(panel, point) is not { } block || Index(block, e.GetPosition(block)) is not { } index)
        {
            return;
        }

        block.Focus(NavigationMethod.Pointer);
        block.SelectionStart = block.SelectionEnd = index;
        Start(panel, block, index, point, ownsCapture: true);
        e.Pointer.Capture(panel);
        e.Handled = true;
    }

    private static void Start(Control panel, SelectableTextBlock block, int index, Point point, bool ownsCapture)
    {
        _panel = panel;
        _block = block;
        _anchor = index;
        _press = point;
        _dragging = true;
        _ownsCapture = ownsCapture;
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _block is not { } block || !ReferenceEquals(sender, _panel) || sender is not Control panel)
        {
            return;
        }

        if (!e.GetCurrentPoint(panel).Properties.IsLeftButtonPressed)
        {
            _dragging = false; // The release went elsewhere.
            return;
        }

        var point = e.GetPosition(panel);
        var target = Nearest(panel, point) ?? block;

        // Until something is selected, a drag from beside the text may still pick the block it heads into (a press
        // between two paragraphs and a drag down selects the lower one).
        if (_ownsCapture && _span.Count == 0 && block.SelectionStart == block.SelectionEnd && !ReferenceEquals(target, block)
            && panel.TranslatePoint(_press, target) is { } press && Index(target, press) is { } anchor)
        {
            block.SelectionStart = block.SelectionEnd = 0;
            target.Focus(NavigationMethod.Pointer);
            _block = block = target;
            _anchor = anchor;
        }

        if (_span.Count == 0 && ReferenceEquals(target, block))
        {
            // Inside its own block a drag on the text is the block's; one from beside it is this helper's.
            if (_ownsCapture && Index(block, e.GetPosition(block)) is { } index)
            {
                block.SelectionStart = _anchor;
                block.SelectionEnd = index;
                e.Handled = true;
            }

            return;
        }

        // Out of its block (and from then on, until the release) the drag selects across blocks.
        if (Index(target, e.GetPosition(target)) is { } focus)
        {
            Span(panel, block, target, focus);
        }

        e.Handled = true;
    }

    /// <summary>Selects from the anchor to <paramref name="focus"/> in <paramref name="target"/>, all of every block between.</summary>
    private static void Span(Control panel, SelectableTextBlock anchor, SelectableTextBlock target, int focus)
    {
        var blocks = Blocks(panel).Select(b => b.Block).ToList();
        int a = blocks.IndexOf(anchor), f = blocks.IndexOf(target);
        if (a < 0 || f < 0)
        {
            return;
        }

        var (from, to) = a <= f ? (a, f) : (f, a);
        foreach (var old in _span.Where(b => blocks.IndexOf(b) is var i && (i < from || i > to)))
        {
            old.ClearSelection();
        }

        _span.Clear();
        for (var i = from; i <= to; i++)
        {
            var block = blocks[i];
            if (a == f)
            {
                block.SelectionStart = _anchor;
                block.SelectionEnd = focus;
                continue; // Back in its own block: a selection in one block again.
            }

            if (i == a || i == f)
            {
                // The first block selects from its point to its end, the last one from its start to its point.
                var at = i == a ? _anchor : focus;
                if (i == from)
                {
                    block.SelectAll();
                    block.SelectionStart = at;
                }
                else
                {
                    block.SelectionStart = 0;
                    block.SelectionEnd = at;
                }
            }
            else
            {
                block.SelectAll();
            }

            _span.Add(block);
        }
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || _block is not { } block || !ReferenceEquals(sender, _panel))
        {
            return;
        }

        _dragging = false;
        if (_ownsCapture && ReferenceEquals(e.Pointer.Captured, sender))
        {
            e.Pointer.Capture(null);
        }

        if (_span.Count == 0 && (!_ownsCapture || block.SelectionStart == block.SelectionEnd))
        {
            // Just a click, or a selection the block made itself: nothing for this helper to keep.
            _block = null;
            return;
        }

        block.LostFocus += Forget;

        void Forget(object? s, RoutedEventArgs a)
        {
            block.LostFocus -= Forget;
            if (ReferenceEquals(_block, block))
            {
                ClearSpan();
                _block = null;
            }
        }
    }

    private static void ClearSpan()
    {
        foreach (var block in _span)
        {
            block.ClearSelection();
        }

        _span.Clear();
    }

    /// <summary>Copying any block of a selection over several copies all of it, a blank line between paragraphs.</summary>
    private static void OnCopying(object? sender, RoutedEventArgs e)
    {
        if (_span.Count == 0 || e.Source is not SelectableTextBlock source || !_span.Contains(source)
            || sender is not Control panel || TopLevel.GetTopLevel(panel)?.Clipboard is not { } clipboard)
        {
            return;
        }

        e.Handled = true;
        _ = clipboard.SetTextAsync(SpanText(panel));
    }

    /// <summary>The text of a selection over several blocks.</summary>
    public static string SpanText(Control panel)
    {
        var text = new StringBuilder();
        double? bottom = null;
        foreach (var (block, bounds) in Blocks(panel).Where(b => _span.Contains(b.Block)))
        {
            if (block.SelectedText is not { Length: > 0 } selected)
            {
                continue;
            }

            if (bottom is { } previous)
            {
                // Paragraphs stand apart; list items and the lines of one item sit close.
                text.Append(bounds.Top - previous >= 8 ? "\n\n" : "\n");
            }

            text.Append(selected.Replace(MarkdownTextBlock.PillGap, "", StringComparison.Ordinal));
            bottom = bounds.Bottom;
        }

        return text.ToString();
    }

    /// <summary>
    /// The text block a press is on (null beside the text), or <paramref name="own"/> for a press that some control
    /// handles itself: buttons, boxes, scrollbars.
    /// </summary>
    private static SelectableTextBlock? TextTarget(Visual? source, Visual panel, out bool own)
    {
        own = false;
        SelectableTextBlock? text = null;
        for (var visual = source; visual is not null && !ReferenceEquals(visual, panel); visual = visual.GetVisualParent())
        {
            if (visual is Button or TextBox or ToggleButton or ScrollBar or Thumb)
            {
                own = true;
                return null;
            }

            text ??= visual as SelectableTextBlock;
        }

        return text;
    }

    /// <summary>The visible blocks with text, in reading order (top to bottom, then left to right), with their bounds in the panel.</summary>
    private static IEnumerable<(SelectableTextBlock Block, Rect Bounds)> Blocks(Control panel) =>
        panel.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Where(b => b.IsEffectivelyVisible && !string.IsNullOrEmpty(b.Text ?? InlineText(b)))
            .Select(b => (Block: b, Transform: b.TransformToVisual(panel)))
            .Where(b => b.Transform is not null)
            .Select(b => (b.Block, Bounds: new Rect(b.Block.Bounds.Size).TransformToAABB(b.Transform!.Value)))
            .OrderBy(b => Math.Round(b.Bounds.Top)).ThenBy(b => b.Bounds.Left);

    /// <summary>
    /// The visible block nearest to the point: vertically first (so a press in the gap above, below or between blocks
    /// still finds one), then horizontally.
    /// </summary>
    private static SelectableTextBlock? Nearest(Control panel, Point point)
    {
        SelectableTextBlock? best = null;
        var bestDistance = (Y: double.MaxValue, X: double.MaxValue);
        foreach (var (block, bounds) in Blocks(panel))
        {
            var distance = (
                Y: point.Y < bounds.Top ? bounds.Top - point.Y : point.Y > bounds.Bottom ? point.Y - bounds.Bottom : 0,
                X: point.X < bounds.Left ? bounds.Left - point.X : point.X > bounds.Right ? point.X - bounds.Right : 0);
            if (distance.Y < bestDistance.Y || (distance.Y == bestDistance.Y && distance.X < bestDistance.X))
            {
                best = block;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static string? InlineText(SelectableTextBlock block) => block.Inlines is { Count: > 0 } ? " " : null;

    /// <summary>The character index at a point (clamped into the text), as the block's own selection would pick it.</summary>
    private static int? Index(SelectableTextBlock block, Point point)
    {
        var layout = block.TextLayout;
        if (layout is null)
        {
            return null;
        }

        var padding = block.Padding;
        var x = Math.Clamp(point.X - padding.Left, 0, Math.Max(layout.WidthIncludingTrailingWhitespace, 0));
        var y = Math.Clamp(point.Y - padding.Top, 0, Math.Max(layout.Height - 1, 0));
        var hit = layout.HitTestPoint(new Point(x, y));
        return hit.TextPosition;
    }
}
