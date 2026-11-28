using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>One slice of <see cref="StorageChart"/>.</summary>
public sealed record StorageSegment(string Label, long Bytes, double Fraction, IBrush Brush);

/// <summary>
/// A horizontal stacked bar chart showing how Nomad Hub's disk space is divided across categories.
/// Rounded outer ends, 2 px gaps between segments, and a subtle track when empty.
/// </summary>
public sealed class StorageChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<StorageSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<StorageChart, IReadOnlyList<StorageSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<StorageChart, IBrush?>(nameof(TrackBrush));

    static StorageChart()
    {
        AffectsRender<StorageChart>(SegmentsProperty, TrackBrushProperty);
    }

    public IReadOnlyList<StorageSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 2 || h <= 2)
        {
            return;
        }

        var radius = h / 2;
        var clip = new RoundedRect(new Rect(0, 0, w, h), radius);

        using (context.PushClip(clip))
        {
            if (TrackBrush is { } track)
            {
                context.FillRectangle(track, new Rect(0, 0, w, h));
            }

            if (Segments is not { Count: > 0 } segments)
            {
                return;
            }

            const double gap = 2;
            const double minSeg = 4;
            var visible = segments.Where(s => s.Bytes > 0 && s.Fraction > 0).ToList();
            if (visible.Count == 0)
            {
                return;
            }

            var totalGap = gap * Math.Max(0, visible.Count - 1);
            var usable = Math.Max(0, w - totalGap);
            var widths = new double[visible.Count];
            double sum = 0;
            for (var i = 0; i < visible.Count; i++)
            {
                widths[i] = Math.Max(minSeg, visible[i].Fraction * usable);
                sum += widths[i];
            }

            if (sum > usable && sum > 0)
            {
                var scale = usable / sum;
                for (var i = 0; i < widths.Length; i++)
                {
                    widths[i] *= scale;
                }
            }

            double x = 0;
            for (var i = 0; i < visible.Count; i++)
            {
                var segW = i == visible.Count - 1 ? Math.Max(0, w - x) : widths[i];
                if (segW > 0.5)
                {
                    context.FillRectangle(visible[i].Brush, new Rect(x, 0, segW, h));
                }

                x += segW + gap;
            }
        }
    }
}
