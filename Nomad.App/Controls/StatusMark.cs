using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Nomad.App.Infrastructure;

namespace Nomad.App.Controls;

public enum SessionMark
{
    None,

    /// <summary>A turn runs: the pixel spinner.</summary>
    Working,

    /// <summary>Claude asks something (a permission or a question): a yellow dot.</summary>
    Waiting,

    /// <summary>Done, not looked at yet: a blue dot.</summary>
    Unread,
}

/// <summary>
/// A sidebar row's state on its right: the working spinner, or a dot. A change crossfades (200 ms, cubic
/// ease-out, retargeting mid-flight); the timer runs only while fading or while the spinner shows.
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
    private static readonly TimeSpan SpinPeriod = TimeSpan.FromMilliseconds(1100);
    private const double DotSize = 6;

    private readonly DateTime _start = DateTime.UtcNow;
    private ClayPlayer? _clay;
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

    public StatusMark()
    {
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    protected override Size MeasureOverride(Size availableSize) => new(12, 12);

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
        if (change.Property == MarkProperty && Mark == SessionMark.Working)
        {
            _clay = null; // Working again: its mark comes in from the start.
        }

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
        var moving = IsVisible && VisualRoot is not null && (Mark == SessionMark.Working || Progress() < 1);
        if (moving)
        {
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
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
            case SessionMark.Working when WorkingMarkSheets.Current is { } sheets:
                // The desktop app's working mark, its plain loop; 2 px larger than the dots so it reads as alive.
                var now = (DateTime.UtcNow - _start).TotalMilliseconds;
                _clay ??= new ClayPlayer(sheets, "default", now);
                _clay.Advance(now);
                _clay.Draw(context, new Rect(-2, -2, Bounds.Width + 4, Bounds.Height + 4), now, opacity);
                break;
            case SessionMark.Working when WorkingBrush is ISolidColorBrush brush:
                PixelSpinner.Draw(context, Bounds.Size, brush.Color, (DateTime.UtcNow - _start) / SpinPeriod % 1, opacity);
                break;
            case SessionMark.Waiting when WaitingBrush is ISolidColorBrush brush:
                DrawDot(context, brush.Color, opacity);
                break;
            case SessionMark.Unread when UnreadBrush is ISolidColorBrush brush:
                DrawDot(context, brush.Color, opacity);
                break;
        }
    }

    private void DrawDot(DrawingContext context, Color color, double opacity)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(
            new ImmutableSolidColorBrush(color, opacity), null, center, DotSize / 2, DotSize / 2);
    }
}
