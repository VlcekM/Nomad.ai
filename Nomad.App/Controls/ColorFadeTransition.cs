using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Nomad.App.Controls;

/// <summary>
/// A <see cref="BrushTransition"/> that blends solid colours premultiplied. Avalonia's own lerps the channels as they
/// are, so a fade between a translucent white (hover, pressed, <c>Transparent</c> = #00FFFFFF) and an opaque dark grey
/// passes through a half-opaque light grey: the row flashes white. Premultiplied, it only ever crossfades.
/// Anything but two solid colours switches at the end, as <see cref="BrushTransition"/> does.
/// </summary>
public sealed class ColorFadeTransition : InterpolatingTransitionBase<IBrush?>
{
    protected override IBrush? Interpolate(double progress, IBrush? from, IBrush? to)
    {
        if (progress >= 1)
            return to;
        if (!TryColor(from, out var a) || !TryColor(to, out var b))
            return from;

        double aa = a.A / 255.0, ba = b.A / 255.0;
        var alpha = aa + (ba - aa) * progress;
        if (alpha <= 0)
            return new ImmutableSolidColorBrush(Colors.Transparent);

        byte Channel(byte x, byte y) =>
            (byte)Math.Clamp(Math.Round((x * aa + (y * ba - x * aa) * progress) / alpha), 0, 255);

        return new ImmutableSolidColorBrush(Color.FromArgb(
            (byte)Math.Round(alpha * 255), Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B)));
    }

    // A missing brush is no fill at all.
    private static bool TryColor(IBrush? brush, out Color color)
    {
        switch (brush)
        {
            case null:
                color = Colors.Transparent;
                return true;
            case ISolidColorBrush solid:
                color = solid.Color;
                if (solid.Opacity < 1)
                    color = Color.FromArgb((byte)Math.Round(color.A * solid.Opacity), color.R, color.G, color.B);
                return true;
            default:
                color = default;
                return false;
        }
    }
}
