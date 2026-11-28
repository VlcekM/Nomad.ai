using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// A thin horizontal sync progress bar: shows a determinate fill when <see cref="Fraction"/> is known, or a smooth
/// FrameLoop-driven indeterminate sweep while <see cref="IsIndeterminate"/> is true.
/// </summary>
public sealed class SyncBar : Control
{
    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<SyncBar, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<SyncBar, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SyncBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SyncBar, IBrush?>(nameof(FillBrush));

    private const double SweepPeriodMs = 1350;

    private readonly FrameLoop _frames;
    private double _phase;
    private TimeSpan? _lastFrame;

    static SyncBar()
    {
        AffectsRender<SyncBar>(FractionProperty, TrackBrushProperty, FillBrushProperty);
    }

    public SyncBar()
    {
        _frames = new FrameLoop(this, Advance);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
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

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsIndeterminateProperty || change.Property == IsVisibleProperty)
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
        _frames.Stop();
        _lastFrame = null;
    }

    private void UpdateTimer()
    {
        var moving = IsIndeterminate && IsVisible && VisualRoot is not null;
        if (!moving)
        {
            _lastFrame = null;
        }

        _frames.Update(moving);
    }

    private void Advance(TimeSpan time)
    {
        var elapsed = _lastFrame is { } last ? Math.Clamp((time - last).TotalMilliseconds, 0, 50) : 16;
        _lastFrame = time;
        _phase = (_phase + elapsed / SweepPeriodMs) % 1.0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 2 || h <= 1)
        {
            return;
        }

        var radius = h / 2;
        using (context.PushClip(new RoundedRect(new Rect(0, 0, w, h), radius)))
        {
            if (TrackBrush is { } track)
            {
                context.FillRectangle(track, new Rect(0, 0, w, h));
            }

            if (FillBrush is not { } fill)
            {
                return;
            }

            if (IsIndeterminate)
            {
                // Cubic ease-in-out sweep across the track.
                var segW = Math.Max(28, w * 0.28);
                var t = _phase < 0.5 ? 4 * _phase * _phase * _phase : 1 - Math.Pow(-2 * _phase + 2, 3) / 2;
                var x = -segW + (w + segW) * t;
                context.DrawRectangle(fill, null, new RoundedRect(new Rect(x, 0, segW, h), radius));
            }
            else if (Fraction > 0.001)
            {
                var fillW = Math.Clamp(Fraction, 0, 1) * w;
                context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, 0, fillW, h), radius));
            }
        }
    }
}
