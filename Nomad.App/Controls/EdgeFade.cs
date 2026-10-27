using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// Clips its child and fades its last <see cref="FadeWidth"/> pixels on the right to transparent: a title too long
/// for its row fades out instead of ending in an ellipsis. A shorter one never reaches the fade.
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

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateMask();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FadeWidthProperty)
        {
            UpdateMask();
        }
    }

    private void UpdateMask()
    {
        var width = Bounds.Width;
        if (width <= FadeWidth)
        {
            OpacityMask = null;
            return;
        }

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
        return finalSize;
    }
}
