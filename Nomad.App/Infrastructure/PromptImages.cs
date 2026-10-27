using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Nomad.Core.Protocol;

namespace Nomad.App.Infrastructure;

/// <summary>An image attached to the composer: what is sent, and a thumbnail to show.</summary>
public sealed class ImageAttachment(PromptImage image, Bitmap preview, string name)
{
    public PromptImage Image { get; } = image;

    public Bitmap Preview { get; } = preview;

    public string Name { get; } = name;
}

/// <summary>Turns files and clipboard data into prompt images the API accepts.</summary>
public static class PromptImages
{
    /// <summary>Longer sides are scaled down to this: the API scales them anyway, and this keeps a prompt small.</summary>
    private const int MaxSide = 2000;

    /// <summary>Keep the original encoding below this (the API takes up to 5 MB of base64 per image).</summary>
    private const int MaxOriginalBytes = 3_500_000;

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
    };

    public static bool IsImageFile(string path) => MediaTypes.ContainsKey(Path.GetExtension(path));

    public static ImageAttachment? FromFile(string path)
    {
        try
        {
            return FromBytes(File.ReadAllBytes(path), MediaTypes.GetValueOrDefault(Path.GetExtension(path), "image/png"), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Decodes an image; re-encodes it as PNG when it is too large, or a type the API doesn't take (BMP).</summary>
    public static ImageAttachment? FromBytes(byte[] data, string mediaType, string name)
    {
        Bitmap bitmap;
        try
        {
            using var stream = new MemoryStream(data);
            bitmap = new Bitmap(stream);
        }
        catch (Exception)
        {
            return null; // Not an image Skia can read.
        }

        var size = bitmap.PixelSize;
        var longest = Math.Max(size.Width, size.Height);
        if (longest > MaxSide || data.Length > MaxOriginalBytes || mediaType == "image/bmp")
        {
            if (longest > MaxSide)
            {
                var scale = (double)MaxSide / longest;
                var scaled = bitmap.CreateScaledBitmap(
                    new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale))),
                    BitmapInterpolationMode.HighQuality);
                bitmap.Dispose();
                bitmap = scaled;
            }

            using var png = new MemoryStream();
            bitmap.Save(png);
            data = png.ToArray();
            mediaType = "image/png";
        }

        return new ImageAttachment(new PromptImage(data, mediaType), bitmap, name);
    }

    // ------------------------------------------------------------------ clipboard (Win32: Avalonia 11 reads text only)

    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint CfHdrop = 15;

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr memory);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    private static extern unsafe uint DragQueryFile(IntPtr drop, uint index, char* file, uint length);

    /// <summary>
    /// What a paste should attach: image files copied in Explorer, or a copied image (PNG, else a bitmap). Null when
    /// the clipboard holds text: then the text box pastes it as usual.
    /// </summary>
    public static List<ImageAttachment>? FromClipboard()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            if (IsClipboardFormatAvailable(CfHdrop))
            {
                var images = ReadFiles().Where(IsImageFile).Select(FromFile).OfType<ImageAttachment>().ToList();
                return images.Count > 0 ? images : null;
            }

            if (IsClipboardFormatAvailable(CfUnicodeText))
            {
                return null;
            }

            var png = RegisterClipboardFormat("PNG");
            if (IsClipboardFormatAvailable(png) && Read(png) is { } pngData && FromBytes(pngData, "image/png", "Pasted image") is { } fromPng)
            {
                return [fromPng];
            }

            if (IsClipboardFormatAvailable(CfDib) && Read(CfDib) is { } dib && FromBytes(DibToBmp(dib), "image/bmp", "Pasted image") is { } fromDib)
            {
                return [fromDib];
            }

            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[]? Read(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bytes = new byte[(int)GlobalSize(handle)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static unsafe List<string> ReadFiles()
    {
        var files = new List<string>();
        var drop = GetClipboardData(CfHdrop);
        if (drop == IntPtr.Zero)
        {
            return files;
        }

        var count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFile(drop, i, null, 0);
            var buffer = new char[length + 1];
            fixed (char* p = buffer)
            {
                DragQueryFile(drop, i, p, length + 1);
            }

            files.Add(new string(buffer, 0, (int)length));
        }

        return files;
    }

    /// <summary>A CF_DIB block (header, masks, palette, pixels) with the 14-byte file header a .bmp starts with.</summary>
    private static byte[] DibToBmp(byte[] dib)
    {
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        var colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
        var masks = compression == 3 && headerSize == 40 ? 12 : 0;
        var colors = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 14 + headerSize + masks + (colors * 4));
        dib.CopyTo(bmp, 14);
        return bmp;
    }
}
