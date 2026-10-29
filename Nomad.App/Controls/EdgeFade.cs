using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// Clips its child and fades its last <see cref="FadeWidth"/> pixels on the right to transparent: a title too long
/// for its row fades out instead of ending in an ellipsis. One that fits isn't faded at all.
/// </summary>
public sealed class EdgeFade : Decorator
{
    public static readonly StyledProperty<double> FadeWidthProperty =
        AvaloniaProperty.Register<EdgeFade, double>(nameof(FadeWidth), 28);

    static EdgeFade()
    {
        ClipToBoundsProperty.OverrideDefaultValue<EdgeFade>(true);
    }

    public double FadeWidth
    {
        get => GetValue(FadeWidthProperty);
        set => SetValue(FadeWidthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FadeWidthProperty)
        {
            InvalidateArrange();
        }
    }

    private double _maskWidth = -1;

    /// <summary>
    /// Fades only a child that doesn't fit: one sized to its text (beside a logo, say) would otherwise fade its own
    /// last letters too.
    /// </summary>
    private void UpdateMask(double width, bool overflows)
    {
        if (!overflows || width <= FadeWidth)
        {
            _maskWidth = -1;
            OpacityMask = null;
            return;
        }

        if (width == _maskWidth)
        {
            return;
        }

        _maskWidth = width;
        OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Colors.Black, 1 - (FadeWidth / width)),
                new GradientStop(Colors.Transparent, 1),
            },
        };
    }

    /// <summary>The child measures as wide as it likes (no trimming): what does not fit is clipped and faded.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var desired = Child?.DesiredSize ?? default;
        return new Size(Math.Min(desired.Width, availableSize.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, Math.Max(finalSize.Width, Child.DesiredSize.Width), finalSize.Height));
        UpdateMask(finalSize.Width, Child is { } child && child.DesiredSize.Width > finalSize.Width + 0.5);
        return finalSize;
    }
}
