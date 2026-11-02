using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Nomad.App.Controls;

/// <summary>
/// The mark of a session waiting for work of its own to finish (background commands, agents, workflows): three dots in
/// a row, a soft pulse running through them from left to right. Unlike the working spinner it doesn't turn: nothing is
/// being done here, only waited for.
/// </summary>
internal static class WaitDots
{
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1400);

    /// <summary>Draws the dots around <paramref name="center"/>, <paramref name="gap"/> apart, at a point of the pulse (0–1).</summary>
    public static void Draw(DrawingContext context, Point center, Color color, double phase, double opacity, double radius = 1.6, double gap = 4.4)
    {
        for (var k = 0; k < 3; k++)
        {
            // Each dot's pulse a third of the way behind the one on its left: a bump that eases up and down.
            var t = (phase - k / 5.0 + 1) % 1;
            var pulse = t < 0.5 ? Math.Sin(t * 2 * Math.PI - Math.PI / 2) * 0.5 + 0.5 : 0;
            var alpha = (0.3 + 0.7 * pulse) * opacity;
            var lift = pulse * radius * 0.9;
            context.DrawEllipse(new ImmutableSolidColorBrush(color, alpha), null, new Point(center.X + (k - 1) * gap, center.Y - lift), radius, radius);
        }
    }
}

/// <summary>
/// The waiting mark on its own (the chat's waiting row), 20 px as the working mark. Animates while <see cref="IsActive"/>,
/// and a moment after (its row folds away meanwhile); still otherwise.
/// </summary>
public sealed class WaitMark : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<WaitMark, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<WaitMark, bool>(nameof(IsActive));

    // Long enough for the row's fold (200 ms) to finish before the dots stop.
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(300);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly FrameLoop _frames;
    private TimeSpan _stopAt;

    public WaitMark()
    {
        _frames = new FrameLoop(this, _ =>
        {
            InvalidateVisual();
            _frames!.Update(IsActive || _clock.Elapsed < _stopAt);
        });
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(20, 20);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _frames.Update(IsActive);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frames.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            _stopAt = _clock.Elapsed + Linger;
            _frames.Update(true);
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Foreground is ISolidColorBrush brush)
        {
            var phase = _clock.Elapsed / WaitDots.Period % 1;
            WaitDots.Draw(context, new Point(Bounds.Width / 2, Bounds.Height / 2 + 1), brush.Color, phase, 1, radius: 2, gap: 5.5);
        }
    }
}
