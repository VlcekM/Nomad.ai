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

    private static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(140);

    private bool _dragging;
    private double _shown;
    private double _glideFrom;
    private DateTime _glideStart;
    private DispatcherTimer? _timer;

    static FillSlider()
    {
        AffectsRender<FillSlider>(TrackBrushProperty, FillBrushProperty, ThicknessProperty);
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

    private void SetFromPointer(PointerEventArgs e)
    {
        var width = Bounds.Width;
        if (width > 0)
        {
            Value = Math.Clamp(e.GetPosition(this).X / width, 0, 1) * Maximum;
        }
    }

    public override void Render(DrawingContext context)
    {
        // A thin pill centred in a taller hit area.
        var height = Math.Min(Bounds.Height, Thickness);
        var bounds = new Rect(0, (Bounds.Height - height) / 2, Bounds.Width, height);
        var radius = height / 2;
        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null, bounds, radius, radius);
        }

        // At least a dot at the left end, so the lowest level still shows as filled.
        var fraction = Maximum > 0 ? Math.Clamp(_shown / Maximum, 0, 1) : 0;
        var fill = Math.Max(bounds.Height, bounds.Width * fraction);
        if (FillBrush is { } brush)
        {
            context.DrawRectangle(brush, null, bounds.WithWidth(fill), radius, radius);
        }
    }
}
