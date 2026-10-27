using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// Placeholder bars for something still loading: rounded rows of varying length that pulse gently while
/// <see cref="IsActive"/>. They only appear if the load takes longer than a moment (a fast one never flashes them),
/// fade in, and fade out when it ends. Takes up its rows' height while shown, none once done. Runs on render frames
/// only while active or fading.
/// </summary>
public sealed class Skeleton : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<Skeleton, bool>(nameof(IsActive));

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<Skeleton, int>(nameof(Rows), 1);

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<Skeleton, double>(nameof(RowHeight), 10);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<Skeleton, double>(nameof(RowSpacing), 10);

    /// <summary>"0.9,0.6,0.75": each row's share of the width, repeated for further rows.</summary>
    public static readonly StyledProperty<string> WidthsProperty =
        AvaloniaProperty.Register<Skeleton, string>(nameof(Widths), "1");

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Skeleton, IBrush?>(nameof(Fill));

    /// <summary>A load shorter than this shows nothing.</summary>
    private const double DelayMs = 250;
    private const double FadeMs = 200;
    private const double PulseMs = 1600;

    private double[] _widths = [1];
    private bool _animating;
    private bool _shown;
    private TimeSpan? _activeSince;
    private TimeSpan? _fadeStart;
    private double _fadeFrom;
    private double _opacity;
    private double _pulse = 1;
    private TimeSpan _now;

    static Skeleton()
    {
        AffectsMeasure<Skeleton>(RowsProperty, RowHeightProperty, RowSpacingProperty);
        AffectsRender<Skeleton>(FillProperty, WidthsProperty);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public double RowHeight
    {
        get => GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public string Widths
    {
        get => GetValue(WidthsProperty);
        set => SetValue(WidthsProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            if (IsActive)
            {
                _activeSince = null; // Restarts the delay at the next frame.
                _fadeStart = _shown ? _now : null;
                _fadeFrom = _opacity;
            }
            else if (_shown)
            {
                _fadeStart = null;
                _fadeFrom = _opacity;
            }

            Animate();
        }
        else if (change.Property == WidthsProperty)
        {
            _widths = Widths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 1) : 1)
                .DefaultIfEmpty(1)
                .ToArray();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Animate();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Math.Max(Rows, 0);
        return _shown && rows > 0 ? new Size(0, rows * RowHeight + (rows - 1) * RowSpacing) : default;
    }

    private void Animate()
    {
        if (_animating || (!IsActive && !_shown) || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _animating = true;
        top.RequestAnimationFrame(Step);
    }

    private void Step(TimeSpan time)
    {
        _now = time;
        if (VisualRoot is null)
        {
            _animating = false;
            return;
        }

        if (IsActive)
        {
            _activeSince ??= time;
            if (!_shown && (time - _activeSince.Value).TotalMilliseconds >= DelayMs)
            {
                _shown = true;
                _fadeStart = time;
                _fadeFrom = 0;
                InvalidateMeasure();
            }

            if (_shown)
            {
                _fadeStart ??= time;
                var p = Math.Clamp((time - _fadeStart.Value).TotalMilliseconds / FadeMs, 0, 1);
                _opacity = _fadeFrom + (1 - _fadeFrom) * Ease(p);

                // A slow breath between 55% and 100%, starting at full.
                var phase = (time - _activeSince.Value).TotalMilliseconds / PulseMs * 2 * Math.PI;
                _pulse = 0.775 + 0.225 * Math.Cos(phase);
                InvalidateVisual();
            }
        }
        else if (_shown)
        {
            _fadeStart ??= time;
            var p = Math.Clamp((time - _fadeStart.Value).TotalMilliseconds / FadeMs, 0, 1);
            _opacity = _fadeFrom * (1 - Ease(p));
            InvalidateVisual();
            if (p >= 1)
            {
                _shown = false;
                _opacity = 0;
                _fadeStart = null;
                InvalidateMeasure();
            }
        }

        if (IsActive || _shown)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Step);
        }
        else
        {
            _animating = false;
        }
    }

    private static double Ease(double p) => 1 - Math.Pow(1 - p, 3);

    public override void Render(DrawingContext context)
    {
        if (!_shown || _opacity <= 0 || Fill is not { } fill)
        {
            return;
        }

        using (context.PushOpacity(_opacity * _pulse))
        {
            var rows = Math.Max(Rows, 0);
            var radius = Math.Min(4, RowHeight / 2);
            for (var i = 0; i < rows; i++)
            {
                var width = Bounds.Width * _widths[i % _widths.Length];
                var rect = new Rect(0, i * (RowHeight + RowSpacing), width, RowHeight);
                context.DrawRectangle(fill, null, new RoundedRect(rect, radius));
            }
        }
    }
}
