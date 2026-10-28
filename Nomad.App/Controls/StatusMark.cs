using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Nomad.App.Controls;

public enum SessionMark
{
    None,

    /// <summary>A turn runs: a spinning circle.</summary>
    Working,

    /// <summary>Subagents are at work: three dots circling.</summary>
    Subagents,

    /// <summary>Claude asks something (a permission or a question): a yellow dot.</summary>
    Waiting,

    /// <summary>Done, not looked at yet: a blue dot.</summary>
    Unread,
}

/// <summary>
/// A sidebar row's state on its right: a spinning circle while working, circling dots for subagents, or a dot. A change
/// crossfades (200 ms, cubic ease-out, retargeting mid-flight); the timer runs only while fading or animating.
/// </summary>
public sealed class StatusMark : Control
{
    public static readonly StyledProperty<SessionMark> MarkProperty =
        AvaloniaProperty.Register<StatusMark, SessionMark>(nameof(Mark));

    public static readonly StyledProperty<IBrush?> WorkingBrushProperty =
        AvaloniaProperty.Register<StatusMark, IBrush?>(nameof(WorkingBrush));

    public static readonly StyledProperty<IBrush?> WaitingBrushProperty =
        AvaloniaProperty.Register<StatusMark, IBrush?>(nameof(WaitingBrush));

    public static readonly StyledProperty<IBrush?> UnreadBrushProperty =
        AvaloniaProperty.Register<StatusMark, IBrush?>(nameof(UnreadBrush));

    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SpinPeriod = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan OrbitPeriod = TimeSpan.FromMilliseconds(1600);
    private const double DotSize = 6;
    private const double SpinnerSize = 13;
    private const double SpinnerStroke = 1.8;

    private readonly DateTime _start = DateTime.UtcNow;
    private DispatcherTimer? _timer;

    // The mark fading out, and how far the one fading in had come when the change came.
    private SessionMark _from;
    private double _fromOpacity;
    private DateTime _changed = DateTime.MinValue;

    public SessionMark Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    public IBrush? WorkingBrush
    {
        get => GetValue(WorkingBrushProperty);
        set => SetValue(WorkingBrushProperty, value);
    }

    public IBrush? WaitingBrush
    {
        get => GetValue(WaitingBrushProperty);
        set => SetValue(WaitingBrushProperty, value);
    }

    public IBrush? UnreadBrush
    {
        get => GetValue(UnreadBrushProperty);
        set => SetValue(UnreadBrushProperty, value);
    }


    protected override Size MeasureOverride(Size availableSize) => new(14, 14);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // A row coming into view shows its state at once; only changes fade.
        _changed = DateTime.MinValue;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    // A recycled row taking another session's state: no fade.
    private bool _rebinding;

    protected override void OnDataContextBeginUpdate()
    {
        base.OnDataContextBeginUpdate();
        _rebinding = true;
    }

    protected override void OnDataContextEndUpdate()
    {
        base.OnDataContextEndUpdate();
        _rebinding = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkProperty)
        {
            // Mid-fade, whichever of the two marks shows more fades out from where it is.
            var t = Eased(Progress());
            var outgoing = _fromOpacity * (1 - t);
            if (t >= outgoing)
            {
                _from = change.GetOldValue<SessionMark>();
                _fromOpacity = t;
            }
            else
            {
                _fromOpacity = outgoing;
            }

            _changed = VisualRoot is null || _rebinding ? DateTime.MinValue : DateTime.UtcNow;
            Update();
            InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty)
        {
            Update();
        }
    }

    private double Progress() => Math.Clamp((DateTime.UtcNow - _changed) / Fade, 0, 1);

    private static double Eased(double t) => 1 - Math.Pow(1 - t, 3);

    private void Update()
    {
        var moving = IsVisible && VisualRoot is not null && (Mark is SessionMark.Working or SessionMark.Subagents || Progress() < 1);
        if (moving)
        {
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
            {
                InvalidateVisual();
                Update();
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
        var t = Eased(Progress());
        if (t < 1)
        {
            DrawMark(context, _from, _fromOpacity * (1 - t));
        }

        DrawMark(context, Mark, t);
    }

    private void DrawMark(DrawingContext context, SessionMark mark, double opacity)
    {
        if (opacity <= 0)
        {
            return;
        }

        switch (mark)
        {
            case SessionMark.Working when WorkingBrush is ISolidColorBrush brush:
                DrawSpinner(context, brush.Color, (DateTime.UtcNow - _start) / SpinPeriod % 1, opacity);
                break;
            case SessionMark.Subagents when WorkingBrush is ISolidColorBrush brush:
                DrawOrbit(context, brush.Color, (DateTime.UtcNow - _start) / OrbitPeriod % 1, opacity);
                break;
            case SessionMark.Waiting when WaitingBrush is ISolidColorBrush brush:
                DrawDot(context, brush.Color, opacity);
                break;
            case SessionMark.Unread when UnreadBrush is ISolidColorBrush brush:
                DrawDot(context, brush.Color, opacity);
                break;
        }
    }

    /// <summary>A faint ring with a quarter-and-a-bit arc running round it.</summary>
    private void DrawSpinner(DrawingContext context, Color color, double phase, double opacity)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (SpinnerSize - SpinnerStroke) / 2;
        context.DrawEllipse(null, new Pen(new ImmutableSolidColorBrush(color, 0.2 * opacity), SpinnerStroke), center, radius, radius);

        const double sweep = 100;
        var start = 360 * phase - 90;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(PointOnCircle(center, radius, start), false);
            g.ArcTo(PointOnCircle(center, radius, start + sweep), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(color, opacity), SpinnerStroke, lineCap: PenLineCap.Round), geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    /// <summary>Three dots circling the centre, each brightening in turn as it passes the top.</summary>
    private void DrawOrbit(DrawingContext context, Color color, double phase, double opacity)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        const double radius = 3.6;
        const double dot = 1.6;
        for (var k = 0; k < 3; k++)
        {
            var turn = phase + k / 3.0;
            var angle = 2 * Math.PI * turn - Math.PI / 2;
            var glow = 0.5 + 0.5 * Math.Cos(2 * Math.PI * turn);
            var alpha = (0.35 + 0.65 * glow) * opacity;
            context.DrawEllipse(
                new ImmutableSolidColorBrush(color, alpha),
                null,
                new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)),
                dot,
                dot);
        }
    }

    private void DrawDot(DrawingContext context, Color color, double opacity)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(
            new ImmutableSolidColorBrush(color, opacity), null, center, DotSize / 2, DotSize / 2);
    }
}
