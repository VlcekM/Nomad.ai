using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// A line icon from the 16×16 geometries in Theme/Icons.axaml, stroked (or filled) with the
/// inherited text foreground unless <see cref="Stroke"/> is set — so icons inside buttons follow
/// the button's hover colours like its label does.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Icon, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.35);

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);

    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<Icon, bool>(nameof(IsFilled));

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeProperty, StrokeThicknessProperty, IsFilledProperty, TextElement.ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
        VerticalAlignmentProperty.OverrideDefaultValue<Icon>(Avalonia.Layout.VerticalAlignment.Center);
        HorizontalAlignmentProperty.OverrideDefaultValue<Icon>(Avalonia.Layout.HorizontalAlignment.Center);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Rendered size in pixels (the geometry is drawn on a 16 px grid and scaled).</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data)
        {
            return;
        }

        var brush = Stroke ?? TextElement.GetForeground(this);
        if (brush is null)
        {
            return;
        }

        var scale = Size / 16.0;
        var offset = new Point((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (IsFilled)
            {
                context.DrawGeometry(brush, null, data);
            }
            else
            {
                var pen = new Pen(brush, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                context.DrawGeometry(null, pen, data);
            }
        }
    }
}
