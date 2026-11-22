using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Pure managed WOFF2 to TrueType/OpenType (SFNT) decoder per W3C WOFF File Format 2.0.
/// Used on Linux/macOS where DirectWrite is unavailable; zero native dependencies and NativeAOT-safe.
/// </summary>
internal static class Woff2Decoder
{
    private const uint Woff2Signature = 0x774F4632; // 'wOF2'
    private const uint HeadTag = 0x68656164; // 'head'
    private const uint HheaTag = 0x68686561; // 'hhea'
    private const uint HmtxTag = 0x686D7478; // 'hmtx'
    private const uint MaxpTag = 0x6D617870; // 'maxp'
    private const uint GlyfTag = 0x676C7966; // 'glyf'
    private const uint LocaTag = 0x6C6F6361; // 'loca'

    private static readonly uint[] KnownTags =
    [
        Tag("cmap"), Tag("head"), Tag("hhea"), Tag("hmtx"), Tag("maxp"), Tag("name"), Tag("OS/2"), Tag("post"),
        Tag("cvt "), Tag("fpgm"), Tag("glyf"), Tag("loca"), Tag("prep"), Tag("CFF "), Tag("VORG"), Tag("EBDT"),
        Tag("EBLC"), Tag("gasp"), Tag("hdmx"), Tag("kern"), Tag("LTSH"), Tag("PCLT"), Tag("VDMX"), Tag("vhea"),
        Tag("vmtx"), Tag("BASE"), Tag("GDEF"), Tag("GPOS"), Tag("GSUB"), Tag("EBSC"), Tag("JSTF"), Tag("MATH"),
        Tag("CBDT"), Tag("CBLC"), Tag("COLR"), Tag("CPAL"), Tag("SVG "), Tag("sbix"), Tag("acnt"), Tag("avar"),
        Tag("bdat"), Tag("bloc"), Tag("bsln"), Tag("cvar"), Tag("fdsc"), Tag("feat"), Tag("fmtx"), Tag("fvar"),
        Tag("gvar"), Tag("hsty"), Tag("just"), Tag("lcar"), Tag("mort"), Tag("morx"), Tag("opbd"), Tag("prop"),
        Tag("trak"), Tag("Zapf"), Tag("Silf"), Tag("Glat"), Tag("Gloc"), Tag("Feat"), Tag("Sill"),
    ];

    private sealed class TableEntry
    {
        public uint Tag;
        public uint OrigLength;
        public uint TransformLength;
        public bool IsTransformed;
        public byte[]? Data;
    }

    /// <summary>Decodes a WOFF2 font container into a standard TrueType/OpenType (SFNT) byte array.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> woff2)
    {
        if (woff2.Length < 48 || BinaryPrimitives.ReadUInt32BigEndian(woff2) != Woff2Signature)
        {
            throw new InvalidDataException("Not a WOFF2 font.");
        }

        var flavor = BinaryPrimitives.ReadUInt32BigEndian(woff2[4..]);
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(woff2[12..]);
        var totalCompressedSize = BinaryPrimitives.ReadUInt32BigEndian(woff2[20..]);

        var pos = 48;
        var entries = new TableEntry[numTables];
        for (var i = 0; i < numTables; i++)
        {
            var flags = woff2[pos++];
            var tagIndex = flags & 0x3F;
            uint tag;
            if (tagIndex == 63)
            {
                tag = BinaryPrimitives.ReadUInt32BigEndian(woff2[pos..]);
                pos += 4;
            }
            else
            {
                tag = KnownTags[tagIndex];
            }

            var transformVersion = (flags >> 6) & 0x03;
            var isTransformed = (tag is GlyfTag or LocaTag && transformVersion == 0)
                || (tag == HmtxTag && transformVersion == 1);

            var origLength = ReadUIntBase128(woff2, ref pos);
            var transformLength = isTransformed ? ReadUIntBase128(woff2, ref pos) : origLength;

            entries[i] = new TableEntry
            {
                Tag = tag,
                OrigLength = origLength,
                TransformLength = transformLength,
                IsTransformed = isTransformed,
            };
        }

        if ((uint)pos + totalCompressedSize > (uint)woff2.Length)
        {
            throw new InvalidDataException("Truncated WOFF2 compressed data.");
        }

        var uncompressed = DecompressBrotli(woff2.Slice(pos, (int)totalCompressedSize));

        var streamPos = 0;
        TableEntry? glyfEntry = null;
        TableEntry? locaEntry = null;
        TableEntry? hmtxEntry = null;
        byte[]? glyfRaw = null;
        byte[]? hmtxRaw = null;

        foreach (var entry in entries)
        {
            if (entry.Tag == LocaTag && entry.IsTransformed)
            {
                locaEntry = entry;
                continue;
            }

            var sliceLen = checked((int)entry.TransformLength);
            if (streamPos + sliceLen > uncompressed.Length)
            {
                throw new InvalidDataException("Truncated WOFF2 table stream.");
            }

            var slice = uncompressed.AsSpan(streamPos, sliceLen).ToArray();
            streamPos += sliceLen;

            if (entry.Tag == GlyfTag && entry.IsTransformed)
            {
                glyfEntry = entry;
                glyfRaw = slice;
            }
            else if (entry.Tag == HmtxTag && entry.IsTransformed)
            {
                hmtxEntry = entry;
                hmtxRaw = slice;
            }
            else
            {
                entry.Data = slice;
            }
        }

        short[]? xMins = null;
        if (glyfEntry is not null && glyfRaw is not null)
        {
            if (locaEntry is null)
            {
                throw new InvalidDataException("Transformed glyf table without loca table.");
            }

            (glyfEntry.Data, locaEntry.Data, xMins) = ReconstructGlyfAndLoca(glyfRaw);
        }

        if (hmtxEntry is not null && hmtxRaw is not null)
        {
            var hhea = entries.FirstOrDefault(e => e.Tag == HheaTag)?.Data
                ?? throw new InvalidDataException("Transformed hmtx without hhea.");
            var maxp = entries.FirstOrDefault(e => e.Tag == MaxpTag)?.Data
                ?? throw new InvalidDataException("Transformed hmtx without maxp.");
            var numHMetrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34, 2));
            var numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4, 2));
            hmtxEntry.Data = ReconstructHmtx(hmtxRaw, numGlyphs, numHMetrics, xMins);
        }

        return AssembleSfnt(flavor, entries);
    }

    private static (byte[] Glyf, byte[] Loca, short[] XMins) ReconstructGlyfAndLoca(byte[] raw)
    {
        var span = raw.AsSpan();
        var optionFlags = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        var numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var indexFormat = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);

        var nContourSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[8..]));
        var nPointsSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[12..]));
        var flagSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[16..]));
        var glyphSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[20..]));
        var compositeSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[24..]));
        var bboxSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[28..]));
        var instructionSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[32..]));

        var off = 36;
        var nContourStream = span.Slice(off, nContourSize); off += nContourSize;
        var nPointsStream = span.Slice(off, nPointsSize); off += nPointsSize;
        var flagStream = span.Slice(off, flagSize); off += flagSize;
        var glyphStream = span.Slice(off, glyphSize); off += glyphSize;
        var compositeStream = span.Slice(off, compositeSize); off += compositeSize;
        var bboxStream = span.Slice(off, bboxSize); off += bboxSize;
        var instructionStream = span.Slice(off, instructionSize); off += instructionSize;

        var hasOverlapBitmap = (optionFlags & 1) != 0;
        var overlapBitmap = hasOverlapBitmap ? span.Slice(off, (numGlyphs + 7) >> 3) : ReadOnlySpan<byte>.Empty;

        var bboxBitmapLen = ((numGlyphs + 31) >> 5) << 2;
        var bboxBitmap = bboxStream[..bboxBitmapLen];
        var bboxPos = bboxBitmapLen;

        var nPointsPos = 0;
        var flagPos = 0;
        var glyphPos = 0;
        var compositePos = 0;
        var instPos = 0;

        using var glyfOut = new MemoryStream(raw.Length * 2);
        var offsets = new uint[numGlyphs + 1];
        var xMins = new short[numGlyphs];

        for (var i = 0; i < numGlyphs; i++)
        {
            offsets[i] = (uint)glyfOut.Position;
            var nContours = BinaryPrimitives.ReadInt16BigEndian(nContourStream.Slice(i * 2, 2));
            var hasBbox = (bboxBitmap[i >> 3] & (0x80 >> (i & 7))) != 0;

            if (nContours == 0)
            {
                if (hasBbox)
                {
                    bboxPos += 8;
                }

                continue;
            }

            if (nContours == -1)
            {
                if (!hasBbox)
                {
                    throw new InvalidDataException("Composite glyph missing bounding box.");
                }

                var xMin = BinaryPrimitives.ReadInt16BigEndian(bboxStream.Slice(bboxPos, 2));
                var yMin = BinaryPrimitives.ReadInt16BigEndian(bboxStream.Slice(bboxPos + 2, 2));
                var xMax = BinaryPrimitives.ReadInt16BigEndian(bboxStream.Slice(bboxPos + 4, 2));
                var yMax = BinaryPrimitives.ReadInt16BigEndian(bboxStream.Slice(bboxPos + 6, 2));
                bboxPos += 8;
                xMins[i] = xMin;

                WriteInt16BE(glyfOut, -1);
                WriteInt16BE(glyfOut, xMin);
                WriteInt16BE(glyfOut, yMin);
                WriteInt16BE(glyfOut, xMax);
                WriteInt16BE(glyfOut, yMax);

                var compStart = compositePos;
                var haveInstructions = false;
                while (true)
                {
                    var flags = BinaryPrimitives.ReadUInt16BigEndian(compositeStream.Slice(compositePos, 2));
                    compositePos += 4; // flags + glyphIndex
                    compositePos += (flags & 0x0001) != 0 ? 4 : 2; // ARG_1_AND_2_ARE_WORDS
                    if ((flags & 0x0008) != 0) compositePos += 2; // WE_HAVE_A_SCALE
                    else if ((flags & 0x0040) != 0) compositePos += 4; // WE_HAVE_AN_X_AND_Y_SCALE
                    else if ((flags & 0x0080) != 0) compositePos += 8; // WE_HAVE_A_TWO_BY_TWO

                    if ((flags & 0x0100) != 0) haveInstructions = true; // WE_HAVE_INSTRUCTIONS
                    if ((flags & 0x0020) == 0) break; // MORE_COMPONENTS
                }

                glyfOut.Write(compositeStream[compStart..compositePos]);
                if (haveInstructions)
                {
                    var instLen = Read255UInt16(glyphStream, ref glyphPos);
                    WriteUInt16BE(glyfOut, instLen);
                    glyfOut.Write(instructionStream.Slice(instPos, instLen));
                    instPos += instLen;
                }

                PadTo4(glyfOut);
                continue;
            }

            // Simple glyph (nContours > 0).
            var endPts = new ushort[nContours];
            var totalPoints = 0;
            for (var c = 0; c < nContours; c++)
            {
                totalPoints += Read255UInt16(nPointsStream, ref nPointsPos);
                endPts[c] = (ushort)(totalPoints - 1);
            }

            var xs = new int[totalPoints];
            var ys = new int[totalPoints];
            var onCurves = new bool[totalPoints];
            int curX = 0, curY = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            for (var p = 0; p < totalPoints; p++)
            {
                var flag = flagStream[flagPos++];
                onCurves[p] = (flag & 0x80) == 0;
                var (dx, dy) = DecodeTriplet(flag & 0x7F, glyphStream, ref glyphPos);
                curX += dx;
                curY += dy;
                xs[p] = curX;
                ys[p] = curY;
                if (curX < minX) minX = curX;
                if (curX > maxX) maxX = curX;
                if (curY < minY) minY = curY;
                if (curY > maxY) maxY = curY;
            }

            var instLength = Read255UInt16(glyphStream, ref glyphPos);
            var instructions = instructionStream.Slice(instPos, instLength);
            instPos += instLength;

            short gxMin, gyMin, gxMax, gyMax;
            if (hasBbox)
            {
                gxMin = BinaryPrimitives.ReadInt16BigEndian(bboxStream.Slice(bboxPos, 2));
                gyMin = BinaryPrimitives.ReadInt16BigEndian(bboxStream.Slice(bboxPos + 2, 2));
                gxMax = BinaryPrimitives.ReadInt16BigEndian(bboxStream.Slice(bboxPos + 4, 2));
                gyMax = BinaryPrimitives.ReadInt16BigEndian(bboxStream.Slice(bboxPos + 6, 2));
                bboxPos += 8;
            }
            else if (totalPoints > 0)
            {
                gxMin = (short)minX;
                gyMin = (short)minY;
                gxMax = (short)maxX;
                gyMax = (short)maxY;
            }
            else
            {
                gxMin = gyMin = gxMax = gyMax = 0;
            }

            xMins[i] = gxMin;

            WriteInt16BE(glyfOut, nContours);
            WriteInt16BE(glyfOut, gxMin);
            WriteInt16BE(glyfOut, gyMin);
            WriteInt16BE(glyfOut, gxMax);
            WriteInt16BE(glyfOut, gyMax);

            for (var c = 0; c < nContours; c++)
            {
                WriteUInt16BE(glyfOut, endPts[c]);
            }

            WriteUInt16BE(glyfOut, instLength);
            glyfOut.Write(instructions);

            var overlap = hasOverlapBitmap && (overlapBitmap[i >> 3] & (0x80 >> (i & 7))) != 0;
            WriteSimplePoints(glyfOut, xs, ys, onCurves, overlap);
            PadTo4(glyfOut);
        }

        offsets[numGlyphs] = (uint)glyfOut.Position;

        byte[] loca;
        if (indexFormat == 0)
        {
            loca = new byte[(numGlyphs + 1) * 2];
            for (var i = 0; i <= numGlyphs; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(i * 2, 2), (ushort)(offsets[i] >> 1));
            }
        }
        else
        {
            loca = new byte[(numGlyphs + 1) * 4];
            for (var i = 0; i <= numGlyphs; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4, 4), offsets[i]);
            }
        }

        return (glyfOut.ToArray(), loca, xMins);
    }

    private static (int Dx, int Dy) DecodeTriplet(int code, ReadOnlySpan<byte> data, ref int pos)
    {
        if (code < 10)
        {
            var dy = ((code & 0x0E) << 7) + data[pos++];
            return (0, (code & 1) != 0 ? dy : -dy);
        }

        if (code < 20)
        {
            var dx = (((code - 10) & 0x0E) << 7) + data[pos++];
            return ((code & 1) != 0 ? dx : -dx, 0);
        }

        if (code < 84)
        {
            var b0 = code - 20;
            var b1 = data[pos++];
            var dx = 1 + (b0 & 0x30) + (b1 >> 4);
            var dy = 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F);
            return ((b0 & 1) != 0 ? dx : -dx, (b0 & 2) != 0 ? dy : -dy);
        }

        if (code < 120)
        {
            var b0 = code - 84;
            var b1 = data[pos++];
            var b2 = data[pos++];
            var q = b0 >> 2;
            var dx = 1 + (q / 3) * 256 + b1;
            var dy = 1 + (q % 3) * 256 + b2;
            return ((b0 & 1) != 0 ? dx : -dx, (b0 & 2) != 0 ? dy : -dy);
        }

        if (code < 124)
        {
            var b0 = code - 120;
            var b1 = data[pos++];
            var b2 = data[pos++];
            var b3 = data[pos++];
            var dx = (b1 << 4) | (b2 >> 4);
            var dy = ((b2 & 0x0F) << 8) | b3;
            return ((b0 & 1) != 0 ? dx : -dx, (b0 & 2) != 0 ? dy : -dy);
        }

        {
            var b0 = code - 124;
            var dx = (data[pos] << 8) | data[pos + 1];
            var dy = (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            return ((b0 & 1) != 0 ? dx : -dx, (b0 & 2) != 0 ? dy : -dy);
        }
    }

    private static void WriteSimplePoints(MemoryStream output, int[] xs, int[] ys, bool[] onCurves, bool overlap)
    {
        var n = xs.Length;
        var flags = new byte[n];
        using var xBytes = new MemoryStream(n * 2);
        using var yBytes = new MemoryStream(n * 2);

        int prevX = 0, prevY = 0;
        for (var i = 0; i < n; i++)
        {
            byte f = onCurves[i] ? (byte)0x01 : (byte)0x00;
            if (i == 0 && overlap) f |= 0x40; // OVERLAP_SIMPLE

            var dx = xs[i] - prevX;
            var dy = ys[i] - prevY;
            prevX = xs[i];
            prevY = ys[i];

            if (dx == 0)
            {
                f |= 0x10; // X_IS_SAME_OR_POSITIVE_X_SHORT_VECTOR
            }
            else if (dx is >= -255 and <= 255)
            {
                f |= 0x02; // X_SHORT_VECTOR
                if (dx > 0) f |= 0x10;
                xBytes.WriteByte((byte)Math.Abs(dx));
            }
            else
            {
                WriteInt16BE(xBytes, (short)dx);
            }

            if (dy == 0)
            {
                f |= 0x20; // Y_IS_SAME_OR_POSITIVE_Y_SHORT_VECTOR
            }
            else if (dy is >= -255 and <= 255)
            {
                f |= 0x04; // Y_SHORT_VECTOR
                if (dy > 0) f |= 0x20;
                yBytes.WriteByte((byte)Math.Abs(dy));
            }
            else
            {
                WriteInt16BE(yBytes, (short)dy);
            }

            flags[i] = f;
        }

        // Write flags (with simple repeat compression).
        for (var i = 0; i < n;)
        {
            var f = flags[i];
            var run = 1;
            while (i + run < n && flags[i + run] == f && run < 256) run++;

            if (run > 2)
            {
                output.WriteByte((byte)(f | 0x08)); // REPEAT_FLAG
                output.WriteByte((byte)(run - 1));
                i += run;
            }
            else
            {
                output.WriteByte(f);
                i++;
            }
        }

        xBytes.Position = 0;
        xBytes.CopyTo(output);
        yBytes.Position = 0;
        yBytes.CopyTo(output);
    }

    private static byte[] ReconstructHmtx(byte[] raw, int numGlyphs, int numHMetrics, short[]? xMins)
    {
        var span = raw.AsSpan();
        var flags = span[0];
        var hasPropLsb = (flags & 1) == 0;
        var hasMonoLsb = (flags & 2) == 0;
        var pos = 1;

        var advances = new ushort[numHMetrics];
        for (var i = 0; i < numHMetrics; i++)
        {
            advances[i] = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pos, 2));
            pos += 2;
        }

        var lsbs = new short[numGlyphs];
        if (hasPropLsb)
        {
            for (var i = 0; i < numHMetrics; i++)
            {
                lsbs[i] = BinaryPrimitives.ReadInt16BigEndian(span.Slice(pos, 2));
                pos += 2;
            }
        }
        else if (xMins is not null)
        {
            for (var i = 0; i < numHMetrics; i++) lsbs[i] = xMins[i];
        }

        if (hasMonoLsb)
        {
            for (var i = numHMetrics; i < numGlyphs; i++)
            {
                lsbs[i] = BinaryPrimitives.ReadInt16BigEndian(span.Slice(pos, 2));
                pos += 2;
            }
        }
        else if (xMins is not null)
        {
            for (var i = numHMetrics; i < numGlyphs; i++) lsbs[i] = xMins[i];
        }

        var output = new byte[numHMetrics * 4 + (numGlyphs - numHMetrics) * 2];
        var outPos = 0;
        for (var i = 0; i < numHMetrics; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(outPos, 2), advances[i]);
            BinaryPrimitives.WriteInt16BigEndian(output.AsSpan(outPos + 2, 2), lsbs[i]);
            outPos += 4;
        }

        for (var i = numHMetrics; i < numGlyphs; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(output.AsSpan(outPos, 2), lsbs[i]);
            outPos += 2;
        }

        return output;
    }

    private static byte[] AssembleSfnt(uint flavor, TableEntry[] entries)
    {
        var sorted = entries.OrderBy(e => e.Tag).ToArray();
        var numTables = sorted.Length;

        var entrySelector = 0;
        var searchRange = 16;
        while (searchRange * 2 <= numTables * 16)
        {
            searchRange *= 2;
            entrySelector++;
        }

        var rangeShift = numTables * 16 - searchRange;

        var totalSize = 12 + numTables * 16;
        foreach (var e in sorted)
        {
            var data = e.Data ?? throw new InvalidDataException("Missing decoded table data.");
            if (e.Tag == HeadTag && data.Length >= 12)
            {
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 0);
            }

            totalSize += (data.Length + 3) & ~3;
        }

        var sfnt = new byte[totalSize];
        BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(0, 4), flavor);
        BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(4, 2), (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(6, 2), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(8, 2), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(10, 2), (ushort)rangeShift);

        var dataOffset = 12 + numTables * 16;
        var headOffset = -1;
        for (var i = 0; i < numTables; i++)
        {
            var e = sorted[i];
            var data = e.Data!;
            var recordOffset = 12 + i * 16;
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recordOffset, 4), e.Tag);
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recordOffset + 4, 4), TableChecksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recordOffset + 8, 4), (uint)dataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recordOffset + 12, 4), (uint)data.Length);

            Buffer.BlockCopy(data, 0, sfnt, dataOffset, data.Length);
            if (e.Tag == HeadTag) headOffset = dataOffset;
            dataOffset += (data.Length + 3) & ~3;
        }

        if (headOffset >= 0 && headOffset + 12 <= sfnt.Length)
        {
            var adjustment = unchecked(0xB1B0AFBAu - TableChecksum(sfnt));
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(headOffset + 8, 4), adjustment);
        }

        return sfnt;
    }

    private static uint TableChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var full = data.Length & ~3;
        for (var i = 0; i < full; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i, 4)));
        }

        if (full < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            data[full..].CopyTo(tail);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(tail));
        }

        return sum;
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static uint ReadUIntBase128(ReadOnlySpan<byte> data, ref int pos)
    {
        uint accum = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = data[pos++];
            if (i == 0 && b == 0x80) throw new InvalidDataException("Invalid UIntBase128 leading zero.");
            if ((accum & 0xFE000000) != 0) throw new InvalidDataException("UIntBase128 overflow.");

            accum = (accum << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return accum;
        }

        throw new InvalidDataException("UIntBase128 exceeds 5 bytes.");
    }

    private static ushort Read255UInt16(ReadOnlySpan<byte> data, ref int pos)
    {
        var code = data[pos++];
        return code switch
        {
            253 => ReadUInt16BE(data, ref pos),
            254 => (ushort)(506 + data[pos++]),
            255 => (ushort)(253 + data[pos++]),
            _ => code,
        };
    }

    private static ushort ReadUInt16BE(ReadOnlySpan<byte> data, ref int pos)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
        pos += 2;
        return value;
    }

    private static void WriteUInt16BE(Stream s, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteInt16BE(Stream s, short value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buf, value);
        s.Write(buf);
    }

    private static void PadTo4(MemoryStream s)
    {
        var rem = (int)(s.Position & 3);
        if (rem == 0) return;

        for (var i = 0; i < 4 - rem; i++) s.WriteByte(0);
    }

    private static uint Tag(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
