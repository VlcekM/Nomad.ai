using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// Yellow construction stripes, drawn behind the title bar of a debug run (one started with <c>--dev-*</c> options) so
/// it is never mistaken for the user's own Nomad.ai.
/// </summary>
public sealed class HazardStripes : Control
{
    private const double Stripe = 9;
    private const double Gap = 13;

    private static readonly IBrush Yellow = new SolidColorBrush(Color.FromArgb(0x16, 0xF2, 0xC1, 0x2E));

    public HazardStripes()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        var height = Bounds.Height;
        var period = Stripe + Gap;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            // Parallelograms leaning right, from just left of the edge (so the first one is whole) to past the end.
            for (var x = -height; x < Bounds.Width; x += period)
            {
                g.BeginFigure(new Point(x, height), isFilled: true);
                g.LineTo(new Point(x + height, 0));
                g.LineTo(new Point(x + height + Stripe, 0));
                g.LineTo(new Point(x + Stripe, height));
                g.EndFigure(isClosed: true);
            }
        }

        context.DrawGeometry(Yellow, null, geometry);
    }
}
