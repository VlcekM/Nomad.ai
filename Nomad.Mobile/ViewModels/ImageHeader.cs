using System.Buffers.Binary;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// An image's size in pixels read from its header (PNG, JPEG, GIF, BMP or WebP), without decoding it: whether a picture is small
/// enough to decode is known before any memory goes to it.
/// </summary>
public static class ImageHeader
{
    /// <summary>The width and height, or null when the header isn't one of those kinds or can't be read.</summary>
    public static (int Width, int Height)? SizeOf(ReadOnlySpan<byte> data)
    {
        try
        {
            return Read(data) is { Width: > 0, Height: > 0 } size ? size : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // Cut short.
        }
    }

    private static (int Width, int Height)? Read(ReadOnlySpan<byte> d)
    {
        // PNG: the signature, then IHDR's width and height, big-endian.
        if (d.Length >= 24 && d[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(d[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(d[20..]));
        }

        // GIF: the logical screen's width and height, little-endian.
        if (d.Length >= 10 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F')
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(d[6..]), BinaryPrimitives.ReadUInt16LittleEndian(d[8..]));
        }

        // BMP: the info header's width and height (negative for top-down rows).
        if (d.Length >= 26 && d[0] == 'B' && d[1] == 'M')
        {
            return (Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(d[18..])), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(d[22..])));
        }

        // WebP: RIFF....WEBP, then a lossy (VP8 ), lossless (VP8L) or extended (VP8X) chunk.
        if (d.Length >= 30 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8))
        {
            var chunk = d[12..16];
            if (chunk.SequenceEqual("VP8 "u8))
            {
                return (BinaryPrimitives.ReadUInt16LittleEndian(d[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(d[28..]) & 0x3FFF);
            }

            if (chunk.SequenceEqual("VP8L"u8))
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(d[21..]);
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }

            if (chunk.SequenceEqual("VP8X"u8))
            {
                return ((d[24] | d[25] << 8 | d[26] << 16) + 1, (d[27] | d[28] << 8 | d[29] << 16) + 1);
            }

            return null;
        }

        // JPEG: the first start-of-frame segment's height and width, big-endian.
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            var at = 2;
            while (at + 9 < d.Length)
            {
                if (d[at] != 0xFF)
                {
                    return null;
                }

                var marker = d[at + 1];
                if (marker == 0xFF)
                {
                    at++; // Fill byte.
                    continue;
                }

                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    return (BinaryPrimitives.ReadUInt16BigEndian(d[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(d[(at + 5)..]));
                }

                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                {
                    at += 2; // No length.
                    continue;
                }

                at += 2 + BinaryPrimitives.ReadUInt16BigEndian(d[(at + 2)..]);
            }
        }

        return null;
    }
}
