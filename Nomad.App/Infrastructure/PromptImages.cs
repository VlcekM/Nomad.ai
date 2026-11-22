using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Nomad.Core.Protocol;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Something attached to the composer: an image (sent as an image block, shown as a thumbnail), or any other file
/// or folder (its path goes into the prompt for Claude to read, shown as a chip).
/// </summary>
public sealed class Attachment
{
    private Attachment(PromptImage? image, Bitmap? preview, string? path, string name)
    {
        Image = image;
        Preview = preview;
        Path = path;
        Name = name;
        IsFolder = path is not null && Directory.Exists(path);
    }

    public static Attachment ForImage(PromptImage image, Bitmap preview, string name) => new(image, preview, null, name);

    public static Attachment ForFile(string path) =>
        new(null, null, path, System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path);

    public PromptImage? Image { get; }

    public Bitmap? Preview { get; }

    /// <summary>A file's or folder's full path; null for an image.</summary>
    public string? Path { get; }

    public string Name { get; }

    public bool IsImage => Image is not null;

    public bool IsFile => Image is null;

    public bool IsFolder { get; }

    /// <summary>The chip's second line: "CS", "PDF", "Folder".</summary>
    public string Kind => IsFolder ? "Folder"
        : System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "File";

    public string ToolTip => Path ?? (Preview is null ? Name : $"{Name} · {SizeText} · click to view");

    /// <summary>"1920 × 1080" for an image.</summary>
    public string SizeText => Preview is { PixelSize: var size } ? $"{size.Width} × {size.Height}" : "";

    /// <summary>The thumbnail's width at its 96 px height: the image's own shape, 64–192 px.</summary>
    public double PreviewWidth =>
        Preview is { PixelSize: { Width: > 0, Height: > 0 } size } ? Math.Clamp(96.0 * size.Width / size.Height, 64, 192) : 96;
}

/// <summary>Turns files and clipboard data into attachments: images the API accepts, other files by path.</summary>
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

    /// <summary>An image file as an image (a chip when it can't be decoded); any other file or folder as a chip.</summary>
    public static Attachment FromPath(string path) =>
        (IsImageFile(path) ? FromFile(path) : null) ?? Attachment.ForFile(path);

    public static Attachment? FromFile(string path)
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
    public static Attachment? FromBytes(byte[] data, string mediaType, string name)
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

        return Attachment.ForImage(new PromptImage(data, mediaType), bitmap, name);
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

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>
    /// Puts an image on the clipboard: PNG + DIB on Windows (for modern apps and Paint), and PNG via Avalonia elsewhere.
    /// </summary>
    public static bool CopyImage(Bitmap bitmap, TopLevel? topLevel)
    {
        if (OperatingSystem.IsWindows())
        {
            return topLevel?.TryGetPlatformHandle()?.Handle is { } owner && CopyImageWin32(bitmap, owner);
        }

        if (topLevel?.Clipboard is not { } clipboard)
        {
            return false;
        }

        _ = clipboard.SetBitmapAsync(bitmap);
        return true;
    }

    private static unsafe bool CopyImageWin32(Bitmap bitmap, IntPtr owner)
    {
        using var png = new MemoryStream();
        bitmap.Save(png);

        // A bottom-up 32-bit DIB: BITMAPINFOHEADER, then the rows from the last up.
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        fixed (byte* p = pixels)
        {
            bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), (IntPtr)p, pixels.Length, stride);
        }

        var dib = new byte[40 + pixels.Length];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), width);
        BitConverter.TryWriteBytes(dib.AsSpan(8), height);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)32);
        BitConverter.TryWriteBytes(dib.AsSpan(20), pixels.Length);
        for (var row = 0; row < height; row++)
        {
            pixels.AsSpan(row * stride, stride).CopyTo(dib.AsSpan(40 + ((height - 1 - row) * stride)));
        }

        if (!OpenClipboard(owner))
        {
            return false;
        }

        try
        {
            EmptyClipboard();
            return Put(RegisterClipboardFormat("PNG"), png.ToArray()) | Put(CfDib, dib);
        }
        finally
        {
            CloseClipboard();
        }

        static bool Put(uint format, byte[] data)
        {
            const uint moveable = 0x0002;
            var memory = GlobalAlloc(moveable, (nuint)data.Length);
            if (memory == IntPtr.Zero)
            {
                return false;
            }

            var target = GlobalLock(memory);
            Marshal.Copy(data, 0, target, data.Length);
            GlobalUnlock(memory);
            if (SetClipboardData(format, memory) != IntPtr.Zero)
            {
                return true; // The clipboard owns it now.
            }

            GlobalFree(memory);
            return false;
        }
    }

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    private static extern unsafe uint DragQueryFile(IntPtr drop, uint index, char* file, uint length);

    private static readonly (string ClipboardFormat, string MediaType)[] ClipboardImageFormats =
    [
        ("image/png", "image/png"),
        ("image/x-png", "image/png"),
        ("image/jpeg", "image/jpeg"),
        ("image/jpg", "image/jpeg"),
        ("image/webp", "image/webp"),
        ("image/gif", "image/gif"),
        ("image/bmp", "image/bmp"),
        ("image/x-bmp", "image/bmp"),
        ("image/x-win-bitmap", "image/bmp"),
    ];

    private static readonly HashSet<string> WaylandTextFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/plain;charset=utf-8",
        "UTF8_STRING",
        "STRING",
        "TEXT",
    };

    /// <summary>
    /// What a paste should attach: copied files, or a copied image. Null when the clipboard holds text (then the text
    /// box pastes it as usual) or nothing usable.
    /// </summary>
    public static async Task<List<Attachment>?> FromClipboardAsync(IClipboard? clipboard)
    {
        if (OperatingSystem.IsWindows())
        {
            return FromWin32Clipboard();
        }

        if (clipboard is not null)
        {
            try
            {
                using var data = await clipboard.TryGetDataAsync();
                if (data is not null)
                {
                    if (data.Contains(DataFormat.File) && await data.TryGetFilesAsync() is { Length: > 0 } files)
                    {
                        var attachments = files
                            .Select(f => f.TryGetLocalPath())
                            .OfType<string>()
                            .Select(FromPath)
                            .ToList();
                        if (attachments.Count > 0)
                        {
                            return attachments;
                        }
                    }

                    if (data.Contains(DataFormat.Text))
                    {
                        return null;
                    }

                    if (await ImageFromDataTransferAsync(data) is { } image)
                    {
                        return [image];
                    }
                }
            }
            catch (Exception)
            {
                // X11/XWayland clipboard selection reads can fail or time out; fall through to Wayland if active.
            }
        }

        if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return await FromWaylandClipboardAsync();
        }

        return null;
    }

    private static async Task<Attachment?> ImageFromDataTransferAsync(IAsyncDataTransfer data)
    {
        var byteFormats = data.Formats.OfType<DataFormat<byte[]>>().ToList();
        foreach (var (clipboardFormat, mediaType) in ClipboardImageFormats)
        {
            var format = byteFormats.FirstOrDefault(f => string.Equals(f.Identifier, clipboardFormat, StringComparison.OrdinalIgnoreCase));
            if (format is null)
            {
                continue;
            }

            if (await data.TryGetValueAsync(format) is { Length: > 0 } bytes
                && FromBytes(bytes, mediaType, "Pasted image") is { } attachment)
            {
                return attachment;
            }
        }

        if (data.Contains(DataFormat.Bitmap) && await data.TryGetValueAsync(DataFormat.Bitmap) is { } bitmap)
        {
            using (bitmap)
            using (var png = new MemoryStream())
            {
                bitmap.Save(png);
                if (FromBytes(png.ToArray(), "image/png", "Pasted image") is { } attachment)
                {
                    return attachment;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Fallback for Linux Wayland sessions where XWayland did not bridge a screenshot or file URI from the compositor
    /// clipboard into the X11 selection. Uses <c>wl-paste</c> when installed; returns null otherwise.
    /// </summary>
    private static async Task<List<Attachment>?> FromWaylandClipboardAsync()
    {
        var typesRaw = await RunWlPasteAsync(TimeSpan.FromSeconds(1), "--list-types");
        if (typesRaw is not { Length: > 0 })
        {
            return null;
        }

        var types = Encoding.UTF8.GetString(typesRaw)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (types.Length == 0)
        {
            return null;
        }

        if (types.Any(t => string.Equals(t, "text/uri-list", StringComparison.OrdinalIgnoreCase))
            && await RunWlPasteAsync(TimeSpan.FromSeconds(2), "--no-newline", "--type", "text/uri-list") is { Length: > 0 } uriBytes)
        {
            var files = Encoding.UTF8.GetString(uriBytes)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith('#') && Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile)
                .Select(line => FromPath(new Uri(line).LocalPath))
                .ToList();
            if (files.Count > 0)
            {
                return files;
            }
        }

        if (types.Any(WaylandTextFormats.Contains))
        {
            return null;
        }

        foreach (var (clipboardFormat, mediaType) in ClipboardImageFormats)
        {
            var matched = types.FirstOrDefault(t => string.Equals(t, clipboardFormat, StringComparison.OrdinalIgnoreCase));
            if (matched is null)
            {
                continue;
            }

            if (await RunWlPasteAsync(TimeSpan.FromSeconds(3), "--no-newline", "--type", matched) is { Length: > 0 } bytes
                && FromBytes(bytes, mediaType, "Pasted image") is { } attachment)
            {
                return [attachment];
            }
        }

        return null;
    }

    private static async Task<byte[]?> RunWlPasteAsync(TimeSpan timeout, params string[] args)
    {
        const int maxBytes = 32 * 1024 * 1024;
        var psi = new ProcessStartInfo("wl-paste")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        using (var cts = new CancellationTokenSource(timeout))
        {
            try
            {
                _ = process.StandardError.ReadToEndAsync(cts.Token);
                using var ms = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cts.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    if (ms.Length + read > maxBytes)
                    {
                        return null;
                    }

                    ms.Write(buffer, 0, read);
                }

                await process.WaitForExitAsync(cts.Token);
                return process.ExitCode == 0 ? ms.ToArray() : null;
            }
            catch (Exception)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Ignore cleanup failures for already-exited wl-paste.
                }

                return null;
            }
        }
    }

    private static List<Attachment>? FromWin32Clipboard()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            if (IsClipboardFormatAvailable(CfHdrop))
            {
                var files = ReadFiles().Select(FromPath).ToList();
                return files.Count > 0 ? files : null;
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
