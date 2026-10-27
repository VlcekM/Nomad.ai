using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Nomad.App.Controls;

/// <summary>
/// Browser-like text selection over a panel of <see cref="SelectableTextBlock"/>s: a press beside a block's text (in
/// the margin to its right, between lines, in a code block's padding) starts a selection in the nearest block on that
/// line, and dragging back over the text extends it, as it would in a browser. Presses on the text itself, and on
/// buttons and boxes, are left alone.
/// </summary>
public static class SelectionAssist
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(SelectionAssist));

    private static SelectableTextBlock? _block;
    private static int _anchor;

    static SelectionAssist()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((panel, e) =>
        {
            panel.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            panel.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            panel.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            if (e.NewValue is true)
            {
                panel.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
                panel.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
                panel.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
            }
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    /// <summary>A selection this helper is dragging or made (the page's click-to-focus-the-composer leaves it alone).</summary>
    public static bool IsSelecting => _block is not null;

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _block = null;
        if (sender is not Control panel || !e.GetCurrentPoint(panel).Properties.IsLeftButtonPressed
            || e.ClickCount != 1 || e.KeyModifiers != KeyModifiers.None || IsOwnTarget(e.Source as Visual, panel))
        {
            return;
        }

        var point = e.GetPosition(panel);
        if (Nearest(panel, point) is not { } block || Index(block, e.GetPosition(block)) is not { } index)
        {
            return;
        }

        block.Focus(NavigationMethod.Pointer);
        block.SelectionStart = block.SelectionEnd = index;
        _block = block;
        _anchor = index;
        e.Pointer.Capture(panel);
        e.Handled = true;
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_block is not { } block || sender is not Control panel || !ReferenceEquals(e.Pointer.Captured, panel))
        {
            return;
        }

        if (Index(block, e.GetPosition(block)) is { } index)
        {
            block.SelectionStart = _anchor;
            block.SelectionEnd = index;
        }

        e.Handled = true;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_block is null || sender is not Control panel || !ReferenceEquals(e.Pointer.Captured, panel))
        {
            return;
        }

        e.Pointer.Capture(null);
        if (_block.SelectionStart == _block.SelectionEnd)
        {
            _block = null; // Just a click: nothing selected, so the click may focus the composer.
        }
        else
        {
            var block = _block;
            block.LostFocus += Forget;

            void Forget(object? s, RoutedEventArgs a)
            {
                block.LostFocus -= Forget;
                if (ReferenceEquals(_block, block))
                {
                    _block = null;
                }
            }
        }
    }

    /// <summary>A press that some control handles itself: text, buttons, boxes, scrollbars.</summary>
    private static bool IsOwnTarget(Visual? source, Visual panel)
    {
        for (var visual = source; visual is not null && !ReferenceEquals(visual, panel); visual = visual.GetVisualParent())
        {
            if (visual is SelectableTextBlock or Button or TextBox or ToggleButton or ScrollBar or Thumb)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The visible block whose lines span the point's height, nearest to it horizontally.</summary>
    private static SelectableTextBlock? Nearest(Control panel, Point point)
    {
        SelectableTextBlock? best = null;
        var bestDistance = double.MaxValue;
        foreach (var block in panel.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            if (!block.IsEffectivelyVisible || string.IsNullOrEmpty(block.Text ?? InlineText(block))
                || block.TransformToVisual(panel) is not { } transform)
            {
                continue;
            }

            var bounds = new Rect(block.Bounds.Size).TransformToAABB(transform);
            if (point.Y < bounds.Top || point.Y > bounds.Bottom)
            {
                continue;
            }

            var distance = point.X < bounds.Left ? bounds.Left - point.X : point.X > bounds.Right ? point.X - bounds.Right : 0;
            if (distance < bestDistance)
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
