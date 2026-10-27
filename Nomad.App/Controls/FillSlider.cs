using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nomad.App.Controls;

/// <summary>
/// A filled bar slider (the status-row effort control): a rounded track filled up to the value, no
/// thumb. Press or drag anywhere to set the value; it follows the pointer freely and raises
/// <see cref="Released"/> when let go. Changes made while not dragging (a snap, a new session)
/// glide to the new value.
/// </summary>
public sealed class FillSlider : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<FillSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<FillSlider, double>(nameof(Maximum), 1);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<FillSlider, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<FillSlider, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<FillSlider, double>(nameof(Thickness), 6);

    public static readonly StyledProperty<IBrush?> KnobBrushProperty =
        AvaloniaProperty.Register<FillSlider, IBrush?>(nameof(KnobBrush));

    /// <summary>Tick marks at each whole level, which also become magnetic (none when null).</summary>
    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<FillSlider, IBrush?>(nameof(TickBrush));

    /// <summary>How close (px) the pointer must be to a level for the value to stick to it.</summary>
    public static readonly StyledProperty<double> MagnetDistanceProperty =
        AvaloniaProperty.Register<FillSlider, double>(nameof(MagnetDistance), 7);

    /// <summary>
    /// A second, quieter fill behind the main one, up to this value (none when negative): the plan usage bars' even
    /// pace. Where the main fill covers it, a thin gap in the main fill still marks it.
    /// </summary>
    public static readonly StyledProperty<double> PaceValueProperty =
        AvaloniaProperty.Register<FillSlider, double>(nameof(PaceValue), -1);

    public static readonly StyledProperty<IBrush?> PaceBrushProperty =
        AvaloniaProperty.Register<FillSlider, IBrush?>(nameof(PaceBrush));

    private static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(140);

    private bool _dragging;
    private double _shown;
    private double _glideFrom;
    private DateTime _glideStart;
    private DispatcherTimer? _timer;

    static FillSlider()
    {
        AffectsRender<FillSlider>(PaceValueProperty, PaceBrushProperty, TrackBrushProperty, FillBrushProperty, KnobBrushProperty, TickBrushProperty, ThicknessProperty);
        FocusableProperty.OverrideDefaultValue<FillSlider>(false);
        CursorProperty.OverrideDefaultValue<FillSlider>(new Cursor(StandardCursorType.Hand));
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public double MagnetDistance
    {
        get => GetValue(MagnetDistanceProperty);
        set => SetValue(MagnetDistanceProperty, value);
    }

    public double PaceValue
    {
        get => GetValue(PaceValueProperty);
        set => SetValue(PaceValueProperty, value);
    }

    public IBrush? PaceBrush
    {
        get => GetValue(PaceBrushProperty);
        set => SetValue(PaceBrushProperty, value);
    }

    /// <summary>A circle at the current position (none when null).</summary>
    public IBrush? KnobBrush
    {
        get => GetValue(KnobBrushProperty);
        set => SetValue(KnobBrushProperty, value);
    }

    /// <summary>Raised when a press or drag ends.</summary>
    public event EventHandler? Released;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            if (_dragging || !IsLoaded)
            {
                _shown = Value;
                InvalidateVisual();
            }
            else
            {
                _glideFrom = _shown;
                _glideStart = DateTime.UtcNow;
                _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Glide());
                _timer.Start();
            }
        }
    }

    private void Glide()
    {
        var t = Math.Clamp((DateTime.UtcNow - _glideStart) / GlideTime, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        _shown = _glideFrom + (Value - _glideFrom) * eased;
        if (t >= 1)
        {
            _timer?.Stop();
        }

        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _timer?.Stop();
        _dragging = true;
        e.Pointer.Capture(this);
        SetFromPointer(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            SetFromPointer(e);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            Released?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            _dragging = false;
            Released?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The pill: <see cref="Thickness"/> tall, centred in a taller hit area.</summary>
    private Rect Track
    {
        get
        {
            var height = Math.Min(Bounds.Height, Thickness);
            return new Rect(0, (Bounds.Height - height) / 2, Bounds.Width, height);
        }
    }

    /// <summary>
    /// Where a value sits: the knob's centre, which runs from one radius in from the left end to one
    /// radius in from the right, so the knob never leaves the pill.
    /// </summary>
    private double XOf(double value)
    {
        var r = Track.Height / 2;
        var fraction = Maximum > 0 ? Math.Clamp(value / Maximum, 0, 1) : 0;
        return r + fraction * Math.Max(0, Track.Width - 2 * r);
    }

    private void SetFromPointer(PointerEventArgs e)
    {
        var r = Track.Height / 2;
        var span = Track.Width - 2 * r;
        if (span <= 0)
        {
            return;
        }

        var x = e.GetPosition(this).X;
        var value = Math.Clamp((x - r) / span, 0, 1) * Maximum;

        // Magnetic stops: near a whole level, the value sticks to it.
        if (TickBrush is not null)
        {
            var level = Math.Round(value);
            if (Math.Abs(XOf(level) - x) <= MagnetDistance)
            {
                value = level;
            }
        }

        Value = value;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Track;
        var radius = bounds.Height / 2;
        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null, bounds, radius, radius);
        }

        var hasPace = PaceValue >= 0 && PaceBrush is not null;
        var paceX = hasPace ? XOf(PaceValue) : 0;
        if (hasPace)
        {
            context.DrawRectangle(PaceBrush, null, bounds.WithWidth(paceX + radius), radius, radius);
        }

        // The fill always covers the knob, so the lowest level still shows as filled.
        var x = XOf(_shown);
        if (FillBrush is { } brush)
        {
            context.DrawRectangle(brush, null, bounds.WithWidth(x + radius), radius, radius);
        }

        // Pace under the fill: a thin gap in it, the track's colour.
        if (hasPace && TrackBrush is { } gap && paceX + radius < x - 1)
        {
            context.FillRectangle(gap, new Rect(Math.Round(paceX + radius) - 1, bounds.Y, 1.5, bounds.Height));
        }

        // Small columns at each whole level: where the value snaps to.
        if (TickBrush is { } tick && Maximum >= 1)
        {
            for (var level = 0; level <= (int)Maximum; level++)
            {
                var tx = Math.Round(XOf(level)) - 1;
                context.FillRectangle(tick, new Rect(tx, bounds.Center.Y - 1.5, 2, 3));
            }
        }

        // The knob: a circle at the current position.
        if (KnobBrush is { } knob)
        {
            var knobRadius = radius - 1.5;
            context.DrawEllipse(knob, null, new Point(x, bounds.Center.Y), knobRadius, knobRadius);
        }
    }
}
