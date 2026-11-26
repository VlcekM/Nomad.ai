using System.Text;

namespace Nomad.Core.Text;

/// <summary>
/// A QR code (ISO/IEC 18004) of some text: byte mode, error correction level M, versions 1 to 10 (up to 213 bytes of
/// UTF-8), with the mask chosen by the standard's penalty rules. Settings › Hub shows one for a phone to scan. Written
/// here rather than taken from a package so the NativeAOT build stays free of trimming warnings.
/// </summary>
public sealed class QrCode
{
    /// <summary>The largest version made; version 10 holds 213 bytes at level M, far more than an enrollment link needs.</summary>
    public const int MaxVersion = 10;

    // Level M, by version (index 0 unused): error correction codewords per block and number of blocks.
    private static readonly int[] EccPerBlock = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly int[] Blocks = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    private const int LevelMBits = 0; // The format's two error correction bits: L 01, M 00, Q 11, H 10.

    private readonly bool[,] _modules; // [row, column], true is dark.
    private readonly bool[,] _function;

    private QrCode(int version)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _function = new bool[Size, Size];
    }

    /// <summary>1 to <see cref="MaxVersion"/>.</summary>
    public int Version { get; }

    /// <summary>The mask used, 0 to 7.</summary>
    public int Mask { get; private set; }

    /// <summary>Modules on a side, without the quiet zone (which is 4 modules on every side, and light).</summary>
    public int Size { get; }

    /// <summary>Whether the module at this row and column is dark.</summary>
    public bool this[int row, int column] => _modules[row, column];

    /// <summary>The smallest code that holds the text as UTF-8.</summary>
    /// <exception cref="ArgumentException">The text is longer than version 10 holds.</exception>
    public static QrCode Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    /// <summary>The smallest code that holds these bytes.</summary>
    /// <exception cref="ArgumentException">More bytes than version 10 holds.</exception>
    public static QrCode Encode(ReadOnlySpan<byte> data) => Encode(data, version: null, mask: null);

    /// <summary>A code of these bytes, at a given version and mask when they are set (for tests against a reference).</summary>
    internal static QrCode Encode(ReadOnlySpan<byte> data, int? version, int? mask)
    {
        var length = data.Length;
        var v = version ?? Enumerable.Range(1, MaxVersion).FirstOrDefault(n => DataBits(n, length) <= DataCodewords(n) * 8);
        if (v is < 1 or > MaxVersion || DataBits(v, data.Length) > DataCodewords(v) * 8)
        {
            throw new ArgumentException($"{data.Length} bytes don't fit in a QR code of version {MaxVersion} or less.", nameof(data));
        }

        if (mask is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(mask));
        }

        var qr = new QrCode(v);
        qr.DrawFunctionPatterns();
        qr.DrawCodewords(AddErrorCorrection(v, DataCodewords(v, data)));
        if (mask is { } chosen)
        {
            qr.Mask = chosen;
        }
        else
        {
            var best = int.MaxValue;
            for (var m = 0; m < 8; m++)
            {
                qr.ApplyMask(m);
                qr.DrawFormatBits(m);
                var penalty = qr.Penalty();
                if (penalty < best)
                {
                    best = penalty;
                    qr.Mask = m;
                }

                qr.ApplyMask(m); // Masking is XOR: a second pass undoes it.
            }
        }

        qr.ApplyMask(qr.Mask);
        qr.DrawFormatBits(qr.Mask);
        return qr;
    }

    // ------------------------------------------------------------------ capacity

    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    private static int DataBits(int version, int length) => 4 + CountBits(version) + length * 8;

    /// <summary>Every module that holds data or error correction, in 8-bit codewords (remainder bits dropped).</summary>
    internal static int TotalCodewords(int version)
    {
        var modules = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            modules -= (25 * align - 10) * align - 55;
            if (version >= 7)
            {
                modules -= 36;
            }
        }

        return modules / 8;
    }

    internal static int DataCodewords(int version) => TotalCodewords(version) - EccPerBlock[version] * Blocks[version];

    /// <summary>Mode, count, data, terminator and padding, as the version's data codewords.</summary>
    private static byte[] DataCodewords(int version, ReadOnlySpan<byte> data)
    {
        var capacity = DataCodewords(version) * 8;
        var bits = new List<bool>(capacity);
        void Append(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        Append(0b0100, 4); // Byte mode.
        Append(data.Length, CountBits(version));
        foreach (var b in data)
        {
            Append(b, 8);
        }

        Append(0, Math.Min(4, capacity - bits.Count)); // Terminator.
        Append(0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
        {
            Append(pad, 8);
        }

        var result = new byte[capacity / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                result[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ error correction

    /// <summary>The data split into blocks, each followed by its Reed-Solomon codewords, then interleaved.</summary>
    internal static byte[] AddErrorCorrection(int version, byte[] data)
    {
        var blocks = Blocks[version];
        var ecc = EccPerBlock[version];
        var total = TotalCodewords(version);
        var shortBlocks = blocks - total % blocks;
        var shortLength = total / blocks; // Data and error correction of a short block; a long one has one data codeword more.
        var divisor = ReedSolomonDivisor(ecc);
        var all = new List<byte[]>(blocks);
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var length = shortLength - ecc + (i < shortBlocks ? 0 : 1);
            var block = data.AsSpan(k, length).ToArray();
            k += length;
            all.Add([.. block, .. ReedSolomonRemainder(block, divisor)]);
        }

        var result = new byte[total];
        var n = 0;
        for (var i = 0; i <= shortLength; i++) // A long block's length, as if short ones had a gap at their last data position.
        {
            for (var j = 0; j < blocks; j++)
            {
                // Short blocks have no codeword at the last data position; skip it there.
                if (i != shortLength - ecc || j >= shortBlocks)
                {
                    var block = all[j];
                    var index = j < shortBlocks && i > shortLength - ecc ? i - 1 : i;
                    result[n++] = block[index];
                }
            }
        }

        return result;
    }

    /// <summary>The generator polynomial of this degree, highest power first, its leading 1 left out.</summary>
    internal static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    internal static byte[] ReedSolomonRemainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    /// <summary>Multiplication in GF(2^8) modulo x^8 + x^4 + x^3 + x^2 + 1.</summary>
    private static byte Multiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    // ------------------------------------------------------------------ function patterns

    private void Set(int row, int column, bool dark)
    {
        _modules[row, column] = dark;
        _function[row, column] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            Set(6, i, i % 2 == 0); // Timing patterns.
            Set(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(3, Size - 4);
        DrawFinder(Size - 4, 3);

        var positions = AlignmentPositions(Version);
        foreach (var r in positions)
        {
            foreach (var c in positions)
            {
                // Not over the finders, whose corners the list's first and last entries mark.
                if (!(r == 6 && c == 6) && !(r == 6 && c == positions[^1]) && !(r == positions[^1] && c == 6))
                {
                    DrawAlignment(r, c);
                }
            }
        }

        DrawFormatBits(0); // Reserves the format areas (the real bits come after masking).
        DrawVersionBits();
    }

    private void DrawFinder(int row, int column)
    {
        for (var dr = -4; dr <= 4; dr++)
        {
            for (var dc = -4; dc <= 4; dc++)
            {
                var r = row + dr;
                var c = column + dc;
                if (r >= 0 && r < Size && c >= 0 && c < Size)
                {
                    var ring = Math.Max(Math.Abs(dr), Math.Abs(dc)); // 0 to 4: center, center, light, dark, separator.
                    Set(r, c, ring != 2 && ring != 4);
                }
            }
        }
    }

    private void DrawAlignment(int row, int column)
    {
        for (var dr = -2; dr <= 2; dr++)
        {
            for (var dc = -2; dc <= 2; dc++)
            {
                Set(row + dr, column + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
            }
        }
    }

    /// <summary>The rows (and columns) of the alignment patterns' centers; empty for version 1.</summary>
    internal static int[] AlignmentPositions(int version)
    {
        if (version == 1)
        {
            return [];
        }

        var count = version / 7 + 2;
        var size = version * 4 + 17;
        var step = (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = size - 7; i >= 1; i--, pos -= step)
        {
            result[i] = pos;
        }

        return result;
    }

    /// <summary>The 15 format bits for level M and this mask, BCH-protected and XOR-masked.</summary>
    internal static int FormatBits(int mask)
    {
        var data = (LevelMBits << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        return ((data << 10) | rem) ^ 0x5412;
    }

    /// <summary>The 18 version bits (versions 7 and up), BCH-protected.</summary>
    internal static int VersionBits(int version)
    {
        var rem = version;
        for (var i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }

        return (version << 12) | rem;
    }

    private static bool Bit(int value, int i) => ((value >> i) & 1) != 0;

    private void DrawFormatBits(int mask)
    {
        var bits = FormatBits(mask);

        // Around the top left finder.
        for (var i = 0; i <= 5; i++)
        {
            Set(i, 8, Bit(bits, i));
        }

        Set(7, 8, Bit(bits, 6));
        Set(8, 8, Bit(bits, 7));
        Set(8, 7, Bit(bits, 8));
        for (var i = 9; i < 15; i++)
        {
            Set(8, 14 - i, Bit(bits, i));
        }

        // The copy beside the other two finders.
        for (var i = 0; i < 8; i++)
        {
            Set(8, Size - 1 - i, Bit(bits, i));
        }

        for (var i = 8; i < 15; i++)
        {
            Set(Size - 15 + i, 8, Bit(bits, i));
        }

        Set(Size - 8, 8, true); // The dark module.
    }

    private void DrawVersionBits()
    {
        if (Version < 7)
        {
            return;
        }

        var bits = VersionBits(Version);
        for (var i = 0; i < 18; i++)
        {
            var a = Size - 11 + i % 3;
            var b = i / 3;
            Set(b, a, Bit(bits, i)); // Left of the top right finder,
            Set(a, b, Bit(bits, i)); // and the copy above the bottom left one.
        }
    }

    // ------------------------------------------------------------------ data and masks

    private void DrawCodewords(byte[] codewords)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5; // The vertical timing pattern takes a whole column.
            }

            for (var vert = 0; vert < Size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var column = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var row = upward ? Size - 1 - vert : vert;
                    if (!_function[row, column] && i < codewords.Length * 8)
                    {
                        _modules[row, column] = Bit(codewords[i >> 3], 7 - (i & 7));
                        i++;
                    }

                    // Modules left over (the remainder bits) stay light.
                }
            }
        }
    }

    private static bool MaskHits(int mask, int row, int column) => mask switch
    {
        0 => (row + column) % 2 == 0,
        1 => row % 2 == 0,
        2 => column % 3 == 0,
        3 => (row + column) % 3 == 0,
        4 => (row / 2 + column / 3) % 2 == 0,
        5 => row * column % 2 + row * column % 3 == 0,
        6 => (row * column % 2 + row * column % 3) % 2 == 0,
        _ => ((row + column) % 2 + row * column % 3) % 2 == 0,
    };

    private void ApplyMask(int mask)
    {
        for (var r = 0; r < Size; r++)
        {
            for (var c = 0; c < Size; c++)
            {
                if (!_function[r, c] && MaskHits(mask, r, c))
                {
                    _modules[r, c] = !_modules[r, c];
                }
            }
        }
    }

    /// <summary>The standard's four penalty rules; the mask with the lowest total is used.</summary>
    internal int Penalty()
    {
        var result = 0;

        // 1: five or more modules of one color in a row or column, 3 plus 1 per module past five.
        // 3: a finder-like 1:1:3:1:1 run with four light modules on one side, 40 each.
        for (var line = 0; line < Size; line++)
        {
            result += LinePenalty(i => _modules[line, i]) + LinePenalty(i => _modules[i, line]);
        }

        // 2: each 2×2 block of one color, 3.
        for (var r = 0; r < Size - 1; r++)
        {
            for (var c = 0; c < Size - 1; c++)
            {
                var color = _modules[r, c];
                if (color == _modules[r, c + 1] && color == _modules[r + 1, c] && color == _modules[r + 1, c + 1])
                {
                    result += 3;
                }
            }
        }

        // 4: 10 for every full 5% the share of dark modules is away from half.
        var dark = 0;
        foreach (var m in _modules)
        {
            if (m)
            {
                dark++;
            }
        }

        var total = Size * Size;
        result += Math.Abs(dark * 20 - total * 10) / total * 10;
        return result;
    }

    private static readonly bool[] FinderLeft = [false, false, false, false, true, false, true, true, true, false, true];
    private static readonly bool[] FinderRight = [true, false, true, true, true, false, true, false, false, false, false];

    private int LinePenalty(Func<int, bool> at)
    {
        var result = 0;
        var run = 1;
        for (var i = 1; i <= Size; i++)
        {
            if (i < Size && at(i) == at(i - 1))
            {
                run++;
                continue;
            }

            if (run >= 5)
            {
                result += 3 + run - 5;
            }

            run = 1;
        }

        for (var i = 0; i + 11 <= Size; i++)
        {
            bool left = true, right = true;
            for (var k = 0; k < 11 && (left || right); k++)
            {
                left &= at(i + k) == FinderLeft[k];
                right &= at(i + k) == FinderRight[k];
            }

            result += (left ? 40 : 0) + (right ? 40 : 0);
        }

        return result;
    }
}
