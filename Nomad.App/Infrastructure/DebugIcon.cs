using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Nomad.App.Infrastructure;

/// <summary>
/// A debug run's window icon: the tent in gold instead of black, so its taskbar button is told apart from the user's
/// own Nomad.ai at a glance.
/// </summary>
public static class DebugIcon
{
    private const int Size = 256;

    public static WindowIcon? Create()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Nomad.ai/Assets/nomad.ico"));
            using var tent = new Bitmap(stream);
            var gold = new RenderTargetBitmap(new PixelSize(Size, Size));
            using (var context = gold.CreateDrawingContext())
            {
                var area = new Rect(0, 0, Size, Size);
                using (context.PushOpacityMask(new ImageBrush(tent) { Stretch = Stretch.Uniform }, area))
                {
                    context.FillRectangle(new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromRgb(0xF7, 0xD5, 0x75), 0),
                            new GradientStop(Color.FromRgb(0xE0, 0xA8, 0x2E), 0.55),
                            new GradientStop(Color.FromRgb(0xB8, 0x83, 0x14), 1),
                        },
                    }, area);
                }
            }

            return new WindowIcon(gold);
        }
        catch (Exception)
        {
            return null; // the black tent is still a working icon
        }
    }
}
