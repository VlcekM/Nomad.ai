using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Ide;

/// <summary>
/// The active tab's outline, joined to the file's card below it as in Visual Studio: rounded top corners, and at the
/// bottom the sides curve outwards into the card's top edge. It covers that stretch of the card's border with the
/// card's own colour, so tab and card read as one shape. Its bottom edge must sit on the card's border line.
/// </summary>
public sealed class TabShape : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<TabShape, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TabShape, IBrush?>(nameof(Stroke));

    /// <summary>A short accent line along the top: the active tab of the group that has the focus.</summary>
    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<TabShape, IBrush?>(nameof(Accent));

    /// <summary>The first tab: its left side runs straight down into the card's left border instead of curving out.</summary>
    public static readonly StyledProperty<bool> FlushLeftProperty =
        AvaloniaProperty.Register<TabShape, bool>(nameof(FlushLeft));

    private const double Radius = 7;
    private const double Flare = 6;

    static TabShape()
    {
        AffectsRender<TabShape>(FillProperty, StrokeProperty, AccentProperty, FlushLeftProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<TabShape>(false);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public bool FlushLeft
    {
        get => GetValue(FlushLeftProperty);
        set => SetValue(FlushLeftProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= Radius * 2 || h <= Radius + Flare)
        {
            return;
        }

        // Half-pixel lines; the bottom (b) is the middle of the card's 1 px border, the last pixel row of this control.
        var left = 0.5;
        var right = w - 0.5;
        var top = 0.5;
        var b = h - 0.5;

        Geometry Outline(bool closed)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            var start = FlushLeft ? 0 : left - Flare;
            if (FlushLeft)
            {
                // Down to the card's top edge and one pixel over it, to meet the card's own left border.
                g.BeginFigure(new Point(left, h + 0.5), isFilled: closed);
            }
            else
            {
                g.BeginFigure(new Point(left - Flare, b), isFilled: closed);
                g.ArcTo(new Point(left, b - Flare), new Size(Flare, Flare), 0, false, SweepDirection.CounterClockwise);
            }

            g.LineTo(new Point(left, top + Radius));
            g.ArcTo(new Point(left + Radius, top), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(right - Radius, top));
            g.ArcTo(new Point(right, top + Radius), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(right, b - Flare));
            g.ArcTo(new Point(right + Flare, b), new Size(Flare, Flare), 0, false, SweepDirection.CounterClockwise);
            if (closed)
            {
                // Down over the card's border line, so it doesn't show under the tab.
                g.LineTo(new Point(right + Flare, h + 0.5));
                g.LineTo(new Point(start, h + 0.5));
            }

            g.EndFigure(closed);
            return geometry;
        }

        if (Fill is { } fill)
        {
            context.DrawGeometry(fill, null, Outline(closed: true));
        }

        if (Stroke is { } stroke)
        {
            context.DrawGeometry(null, new Pen(stroke, 1), Outline(closed: false));
        }

        if (Accent is { } accent)
        {
            using (context.PushClip(new RoundedRect(new Rect(0, 0, w, h), Radius, Radius, 0, 0)))
            {
                context.FillRectangle(accent, new Rect(0, 0, w, 2));
            }
        }
    }
}
