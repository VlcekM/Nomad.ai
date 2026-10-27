using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Nomad.App.Controls;

/// <summary>
/// The "working" mark, as in the desktop app: a 3×3 grid of small square pixels with a soft wave of
/// brightness running diagonally through it. Animates only while visible and attached.
/// </summary>
public sealed class PixelSpinner : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<PixelSpinner, IBrush?>(nameof(Foreground));

    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1100);

    private readonly DateTime _start = DateTime.UtcNow;
    private DispatcherTimer? _timer;

    static PixelSpinner()
    {
        AffectsRender<PixelSpinner>(ForegroundProperty);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(14, 14);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        if (IsVisible && VisualRoot is not null)
        {
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Foreground is ISolidColorBrush brush)
        {
            Draw(context, Bounds.Size, brush.Color, (DateTime.UtcNow - _start) / Period % 1, 1);
        }
    }

    /// <summary>The 3×3 pixels at <paramref name="phase"/> (0–1) of the wave, faded by <paramref name="opacity"/>.</summary>
    public static void Draw(DrawingContext context, Size size, Color color, double phase, double opacity)
    {
        var cell = Math.Min(size.Width, size.Height) / 3;
        var pixel = Math.Round(cell * 0.62);
        var inset = (cell - pixel) / 2;

        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                // The wave enters at the bottom-left corner and leaves at the top-right.
                var offset = (col + (2 - row)) / 6.0;
                var wave = 0.5 + 0.5 * Math.Cos(2 * Math.PI * (phase - offset));
                var alpha = (0.18 + 0.82 * Math.Pow(wave, 2.2)) * opacity;
                context.FillRectangle(
                    new ImmutableSolidColorBrush(Color.FromArgb((byte)(alpha * color.A), color.R, color.G, color.B)),
                    new Rect(Math.Round(col * cell + inset), Math.Round(row * cell + inset), pixel, pixel));
            }
        }
    }
}
