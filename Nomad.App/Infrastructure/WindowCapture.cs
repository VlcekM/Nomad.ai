using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Saves what the window's own surface shows (through DWM's PrintWindow), as opposed to a re-render of the visual tree:
/// the renderer's debug overlays (<c>--dev-overlays</c>) are drawn by the compositor onto the surface only, so a
/// <see cref="RenderTargetBitmap"/> never has them. Works while the window is cloaked.
/// </summary>
internal static class WindowCapture
{
    private const uint PwRenderFullContent = 2;

    public static bool TrySave(Window window, string path)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetClientRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var dib = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return false;
            }

            var old = SelectObject(dc, dib);
            var printed = PrintWindow(hwnd, dc, PwRenderFullContent);
            SelectObject(dc, old);
            if (!printed)
            {
                return false;
            }

            var scaling = window.RenderScaling;
            using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var frame = bitmap.Lock())
            {
                for (var y = 0; y < height; y++)
                {
                    unsafe
                    {
                        Buffer.MemoryCopy((byte*)bits + y * width * 4, (byte*)frame.Address + y * frame.RowBytes, frame.RowBytes, width * 4);
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            bitmap.Save(path);
            return true;
        }
        finally
        {
            if (dib != IntPtr.Zero)
            {
                DeleteObject(dib);
            }

            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint Color0; // One palette entry, so the header is a whole BITMAPINFO.
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}
