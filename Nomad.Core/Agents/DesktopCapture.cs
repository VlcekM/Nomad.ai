using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Nomad.Core.Agents;

/// <summary>
/// One monitor as the screenshot tool numbers them: 1 and up, left to right (then top to bottom), with its bounds in desktop
/// pixels (the primary monitor's top left is 0,0; one to its left has a negative X).
/// </summary>
public sealed record MonitorInfo(int Number, int Left, int Top, int Width, int Height, bool IsPrimary)
{
    /// <summary>
    /// "2: 1920x1080 at -1920,0 (primary)", as the tool lists it.
    /// </summary>
    public override string ToString() => $"{Number}: {Width}x{Height} at {Left},{Top}{(IsPrimary ? " (primary)" : "")}";
}

/// <summary>
/// Captures the screen with GDI and encodes it as a PNG. It sees what the desktop of the calling process shows, so it has to run
/// in the Nomad window's own process (on the user's desktop), never in a bridge process an agent started on a hidden one.
/// </summary>
public static partial class DesktopCapture
{
    /// <summary>
    /// The monitors, numbered left to right, then top to bottom.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        if (!OperatingSystem.IsWindows()) return [];

        var found = new List<(Rect Bounds, bool Primary)>();
        var handle = GCHandle.Alloc(found);
        try
        {
            unsafe
            {
                EnumDisplayMonitors(0, 0, &CollectMonitor, GCHandle.ToIntPtr(handle));
            }
        }
        finally
        {
            handle.Free();
        }

        return found
            .OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)
            .Select((m, i) => new MonitorInfo(i + 1, m.Bounds.Left, m.Bounds.Top, m.Bounds.Right - m.Bounds.Left, m.Bounds.Bottom - m.Bounds.Top, m.Primary))
            .ToList();
    }

    /// <summary>
    /// The whole desktop: the rectangle around every monitor.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) VirtualScreen() => OperatingSystem.IsWindows()
        ? (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79))
        : (0, 0, 0, 0);

    /// <summary>
    /// The pixels in the rectangle as a PNG; throws <see cref="InvalidOperationException"/> when the desktop cannot be read (as on a
    /// hidden desktop, or a locked screen).
    /// </summary>
    public static byte[] CapturePng(int left, int top, int width, int height)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Screenshots are only taken on Windows.");
        if (width <= 0 || height <= 0) throw new InvalidOperationException("There is no screen to capture.");

        var pixels = CaptureBgra(left, top, width, height);
        return EncodePng(pixels, width, height);
    }

    /// <summary>
    /// Copies the rectangle off the screen as top-down 32-bit BGRA rows.
    /// </summary>
    private static unsafe byte[] CaptureBgra(int left, int top, int width, int height)
    {
        var screen = GetDC(0);
        if (screen == 0) throw new InvalidOperationException("The screen could not be read (no desktop to capture).");

        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (memory == 0 || bitmap == 0) throw new InvalidOperationException("The screen could not be read (out of graphics memory).");

            previous = SelectObject(memory, bitmap);
            // SRCCOPY | CAPTUREBLT, so layered windows (tooltips, some popups) are in the picture too.
            if (!BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020 | 0x40000000))
            {
                throw new InvalidOperationException($"The screen could not be read (BitBlt failed, error {Marshal.GetLastPInvokeError()}). " +
                    "The desktop may be locked or not the one the user sees.");
            }

            SelectObject(memory, previous);
            previous = 0;

            var info = new BitmapInfoHeader
            {
                Size = (uint)sizeof(BitmapInfoHeader),
                Width = width,
                Height = -height, // Negative: top-down rows.
                Planes = 1,
                BitCount = 32,
            };
            var pixels = new byte[(long)width * height * 4];
            fixed (byte* p = pixels)
            {
                if (GetDIBits(memory, bitmap, 0, (uint)height, p, &info, 0) == 0)
                    throw new InvalidOperationException("The screen could not be read (GetDIBits failed).");
            }

            return pixels;
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    #region PNG

    /// <summary>
    /// Encodes top-down BGRA rows as an RGB PNG (the alpha a screen capture carries means nothing).
    /// </summary>
    public static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0; // Filter: none.
                var source = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    row[1 + x * 3] = bgra[source + x * 4 + 2];
                    row[2 + x * 3] = bgra[source + x * 4 + 1];
                    row[3 + x * 3] = bgra[source + x * 4];
                }

                zlib.Write(row);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // Bit depth.
        header[9] = 2; // Colour type: RGB.
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", raw.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// One PNG chunk: its length, type, data and the CRC of type and data.
    /// </summary>
    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    /// <summary>
    /// The CRC-32 table PNG uses.
    /// </summary>
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>
    /// Runs the CRC over <paramref name="data"/>, starting from <paramref name="crc"/>.
    /// </summary>
    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    #endregion

    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
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
    }

    /// <summary>
    /// EnumDisplayMonitors' callback: adds the monitor to the list the handle points at.
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe int CollectMonitor(nint monitor, nint dc, Rect* clip, nint data)
    {
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        if (GetMonitorInfoW(monitor, &info) && GCHandle.FromIntPtr(data).Target is List<(Rect, bool)> list)
        {
            list.Add((info.Monitor, (info.Flags & 1) != 0));
        }

        return 1;
    }

    [LibraryImport("user32.dll")]
    private static unsafe partial int EnumDisplayMonitors(nint dc, nint clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint gdiObject);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BitmapInfoHeader* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    #endregion
}
