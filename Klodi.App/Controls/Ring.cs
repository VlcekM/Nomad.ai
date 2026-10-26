using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Klodi.App.Controls;

/// <summary>
/// The status-row ring: idle it shows a faint track with an arc for the share of the context
/// window in use; while a turn runs it spins an accent arc. Also used as the 12 px tool spinner.
/// </summary>
public sealed class Ring : Control
{
    public static readonly StyledProperty<bool> IsSpinningProperty =
        AvaloniaProperty.Register<Ring, bool>(nameof(IsSpinning));

    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<Ring, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<Ring, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ArcBrushProperty =
        AvaloniaProperty.Register<Ring, IBrush?>(nameof(ArcBrush));

    public static readonly StyledProperty<IBrush?> SpinBrushProperty =
        AvaloniaProperty.Register<Ring, IBrush?>(nameof(SpinBrush));

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<Ring, double>(nameof(StrokeWidth), 1.6);

    private DispatcherTimer? _timer;
    private double _angle;

    static Ring()
    {
        AffectsRender<Ring>(FractionProperty, TrackBrushProperty, ArcBrushProperty, SpinBrushProperty, StrokeWidthProperty);
    }

    public bool IsSpinning
    {
        get => GetValue(IsSpinningProperty);
        set => SetValue(IsSpinningProperty, value);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? ArcBrush
    {
        get => GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public IBrush? SpinBrush
    {
        get => GetValue(SpinBrushProperty);
        set => SetValue(SpinBrushProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsSpinningProperty)
        {
            UpdateTimer();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    private void UpdateTimer()
    {
        if (IsSpinning && VisualRoot is not null)
        {
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
            {
                _angle = (_angle + 6) % 360;
                InvalidateVisual();
            });
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 2)
        {
            return;
        }

        var w = StrokeWidth;
        var radius = (size - w) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (TrackBrush is { } track)
        {
            context.DrawEllipse(null, new Pen(track, w), center, radius, radius);
        }

        if (IsSpinning)
        {
            DrawArc(context, SpinBrush ?? ArcBrush, center, radius, w, _angle - 90, 100);
        }
        else if (Fraction > 0.001)
        {
            DrawArc(context, ArcBrush, center, radius, w, -90, Math.Min(Fraction, 0.999) * 360);
        }
    }

    private static void DrawArc(DrawingContext context, IBrush? brush, Point center, double radius, double width, double startDegrees, double sweepDegrees)
    {
        if (brush is null)
        {
            return;
        }

        var start = PointOnCircle(center, radius, startDegrees);
        var end = PointOnCircle(center, radius, startDegrees + sweepDegrees);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(brush, width, lineCap: PenLineCap.Round), geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
