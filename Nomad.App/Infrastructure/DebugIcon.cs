using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Nomad.App.Infrastructure;

/// <summary>
/// A debug run's window icon: the tent in gold instead of black, so its taskbar button is told apart from the user's
/// own Nomad.ai at a glance. The black turns a gold that deepens towards the bottom right; the white stays.
/// </summary>
public static class DebugIcon
{
    private static readonly (byte R, byte G, byte B) Light = (0xF7, 0xD5, 0x75);
    private static readonly (byte R, byte G, byte B) Deep = (0xC2, 0x8A, 0x16);

    public static WindowIcon? Create()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Nomad.ai/Assets/nomad.ico"));
            var bitmap = WriteableBitmap.Decode(stream);
            using (var frame = bitmap.Lock())
            {
                var bgra = frame.Format == PixelFormat.Bgra8888;
                var premultiplied = bitmap.AlphaFormat != AlphaFormat.Unpremul;
                var (width, height) = (frame.Size.Width, frame.Size.Height);
                var row = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    var line = frame.Address + y * frame.RowBytes;
                    Marshal.Copy(line, row, 0, row.Length);
                    for (var x = 0; x < width; x++)
                    {
                        var i = x * 4;
                        var a = row[i + 3];
                        if (a == 0)
                        {
                            continue;
                        }

                        // How dark the pixel is (the tent black, the ground white): that much of it turns gold.
                        var lum = (row[i] + row[i + 1] + row[i + 2]) / 3.0;
                        var white = premultiplied ? a : 255;
                        var dark = 1 - Math.Clamp(lum / white, 0, 1);
                        var t = (x + y) / (double)(width + height);
                        var r = (byte)Math.Round(white - (white - Mix(Light.R, Deep.R, t) * white / 255.0) * dark);
                        var g = (byte)Math.Round(white - (white - Mix(Light.G, Deep.G, t) * white / 255.0) * dark);
                        var b = (byte)Math.Round(white - (white - Mix(Light.B, Deep.B, t) * white / 255.0) * dark);
                        (row[i], row[i + 1], row[i + 2]) = bgra ? (b, g, r) : (r, g, b);
                    }

                    Marshal.Copy(row, 0, line, row.Length);
                }
            }

            return new WindowIcon(bitmap);
        }
        catch (Exception)
        {
            return null; // the black tent is still a working icon
        }
    }

    private static byte Mix(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
}
