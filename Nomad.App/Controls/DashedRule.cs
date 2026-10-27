using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>A 1 px horizontal dashed line (the composer's rule between the attachments and the text).</summary>
public sealed class DashedRule : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<DashedRule, IBrush?>(nameof(Stroke));

    /// <summary>Dash and gap length, px.</summary>
    public static readonly StyledProperty<double> DashProperty =
        AvaloniaProperty.Register<DashedRule, double>(nameof(Dash), 3);

    static DashedRule()
    {
        AffectsRender<DashedRule>(StrokeProperty, DashProperty);
        HeightProperty.OverrideDefaultValue<DashedRule>(1);
        IsHitTestVisibleProperty.OverrideDefaultValue<DashedRule>(false);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Dash
    {
        get => GetValue(DashProperty);
        set => SetValue(DashProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Stroke is not { } stroke)
        {
            return;
        }

        var pen = new Pen(stroke, 1, new DashStyle([Dash, Dash], 0));
        context.DrawLine(pen, new Point(0, 0.5), new Point(Bounds.Width, 0.5));
    }
}
