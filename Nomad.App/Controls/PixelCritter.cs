using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// The little pixel-art critter that sits on the composer's top edge, as in the original app:
/// a clay body with two ears, two eyes, four legs and a darker shading column on the right.
/// </summary>
public sealed class PixelCritter : Control
{
    // 9 x 10 cells of 3 px, traced from the reference: ears, a cheek bump on the left, two
    // eyes, four legs and a darker shading column on the right. B body, S shade, E eye.
    private static readonly string[] Map =
    [
        ".BB..BB..",
        "BBB..BB..",
        "BBBBBBBBS",
        ".BBBBBBBS",
        ".BEBBBEBS",
        ".BBBBBBBS",
        ".BBBBBBBS",
        ".BBBBBBBS",
        ".B.B..B.S",
        ".B.B..B.S",
    ];

    private const double Cell = 3;

    private static readonly IBrush BodyBrush = new SolidColorBrush(Color.Parse("#D97757"));
    private static readonly IBrush ShadeBrush = new SolidColorBrush(Color.Parse("#B45A40"));
    private static readonly IBrush EyeBrush = new SolidColorBrush(Color.Parse("#050505"));

    protected override Size MeasureOverride(Size availableSize) => new(Map[0].Length * Cell, Map.Length * Cell);

    public override void Render(DrawingContext context)
    {
        for (var y = 0; y < Map.Length; y++)
        {
            for (var x = 0; x < Map[y].Length; x++)
            {
                var brush = Map[y][x] switch
                {
                    'B' => BodyBrush,
                    'S' => ShadeBrush,
                    'E' => EyeBrush,
                    _ => null,
                };
                if (brush is not null)
                {
                    context.FillRectangle(brush, new Rect(x * Cell, y * Cell, Cell, Cell));
                }
            }
        }
    }
}
