using System.Security.Cryptography;
using System.Text;
using Nomad.Core.Text;

namespace Nomad.Core.Tests;

/// <summary>
/// The QR encoder against known-good codes: whole module matrices made by Project Nayuki's qrcodegen (Python, byte
/// mode, level M, no error correction boost; the python-qrcode package makes the same ones) and the standard's own
/// tables of format bits, version bits, alignment positions and capacities.
/// </summary>
public sealed class QrCodeTests
{
    private static string Matrix(QrCode qr) =>
        string.Join("\n", Enumerable.Range(0, qr.Size).Select(r => string.Concat(Enumerable.Range(0, qr.Size).Select(c => qr[r, c] ? '1' : '0'))));

    private static string Sha256(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void Version1MatchesTheReferenceModuleForModule()
    {
        string[] expected =
        [
            "111111100111101111111",
            "100000101111001000001",
            "101110100100001011101",
            "101110100111001011101",
            "101110101100101011101",
            "100000100110101000001",
            "111111101010101111111",
            "000000000001100000000",
            "101010100111000010010",
            "101100001010001000111",
            "010011100010100011011",
            "101101010110001000011",
            "111001111100101010010",
            "000000001011010100110",
            "111111100011011101111",
            "100000100001110110000",
            "101110101001011100011",
            "101110100010001101010",
            "101110101010100011101",
            "100000100010001100010",
            "111111101010101110011",
        ];

        var qr = QrCode.Encode("Nomad");

        Assert.Equal(1, qr.Version);
        Assert.Equal(21, qr.Size);
        Assert.Equal(0, qr.Mask); // The reference's choice too.
        Assert.Equal(string.Join("\n", expected), Matrix(qr));
    }

    [Fact]
    public void Version2MatchesTheReferenceModuleForModule()
    {
        string[] expected =
        [
            "1111111000000010001111111",
            "1000001010010010101000001",
            "1011101011100010101011101",
            "1011101010000111101011101",
            "1011101000010100101011101",
            "1000001001111110101000001",
            "1111111010101010101111111",
            "0000000011010011000000000",
            "1000001011111001011001110",
            "0111100001110111000111110",
            "0011011001100111000111011",
            "1100010100100101111101001",
            "1000011100111100101100001",
            "1100000100000001010100010",
            "1001001101111001011111011",
            "1011000000110001101101101",
            "1011011011110001111110100",
            "0000000011101110100010000",
            "1111111001110000101010001",
            "1000001000001101100010010",
            "1011101001001011111110101",
            "1011101000000011111000011",
            "1011101000111000000001101",
            "1000001000010011100110001",
            "1111111010100110111001001",
        ];

        var qr = QrCode.Encode("https://github.com/"u8, version: 2, mask: 5);

        Assert.Equal(string.Join("\n", expected), Matrix(qr));
    }

    public static TheoryData<string, int, int, string> References() => new()
    {
        // Text (\u escapes kept as UTF-8), version, mask, SHA-256 of the reference's rows of 0 and 1 joined by \n.
        { "Nomad", 1, 3, "731a1f62499d3b54f2174f55756f04153f85ad3289a5ec92142a376548dc7618" },
        { "Nomad Hub Nomad Hub Nomad Hub Nomad Hub ", 3, 0, "09b98afd97f2dad0afb536d44bb2134028785dd0f6fa4c382da3d47a4cbe0712" },
        { new string('x', 60), 4, 6, "ed291108b53f9586dfb168ac7edaae0ac39144c9d63daaa0e2ab20d6d55d82f8" },
        { string.Concat(Enumerable.Repeat("0123456789abcdef", 5)), 5, 7, "b3e77634cd11cfd840d80c062fd56b275de028d2b784c15a2d35b891e42d3157" },
        {
            "nomad://enroll?hub=https%3A%2F%2Fhub.example.com%2F&token=Qm9va3MgYXJlIGEgdW5pcXVlbHkgcG9ydGFibGUgbWFnaWM", 6, 6,
            "c3662642d3508871006b9991fa121e0566ce3e77e7ac4b7dac6b4448e59fb049"
        },
        { string.Concat(Enumerable.Repeat("0123456789", 15)), 8, 4, "e181060974ebc0b1c074860856b84b35b36cb144c7b8371b379f6e164ac47f23" },
        { string.Concat(Enumerable.Repeat("été ", 28)), 9, 7, "69b186b8d324c1e3f2cc1283db0cc9f0a0cd5e2b11fe4f2fd9c4de99fd1cffa0" },
        { string.Concat(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyz", 8)), 10, 1, "2b272f514f76e22e49555ccb79fd0c8c214e3271eb053e176b5cf1f02c7fe37a" },
    };

    [Theory]
    [MemberData(nameof(References))]
    public void LargerVersionsMatchTheReference(string text, int version, int mask, string sha256)
    {
        var qr = QrCode.Encode(Encoding.UTF8.GetBytes(text), version, mask);

        Assert.Equal(sha256, Sha256(Matrix(qr)));
        Assert.Equal(version, QrCode.Encode(text).Version); // And the smallest version that holds it is the one asked for.
    }

    [Fact]
    public void Version7HasTheReferenceMatrixForBinaryData()
    {
        var qr = QrCode.Encode(Enumerable.Range(0, 120).Select(i => (byte)i).ToArray(), version: 7, mask: 2);

        Assert.Equal("159b22f724acb4a973e07ec45058f68048da8d1cb9aea335e721fd293d918599", Sha256(Matrix(qr)));
    }

    [Theory]
    // The standard's format strings for level M, masks 0 to 7.
    [InlineData(0, "101010000010010")]
    [InlineData(1, "101000100100101")]
    [InlineData(2, "101111001111100")]
    [InlineData(3, "101101101001011")]
    [InlineData(4, "100010111111001")]
    [InlineData(5, "100000011001110")]
    [InlineData(6, "100111110010111")]
    [InlineData(7, "100101010100000")]
    public void FormatBitsMatchTheStandard(int mask, string bits) => Assert.Equal(Convert.ToInt32(bits, 2), QrCode.FormatBits(mask));

    [Theory]
    [InlineData(7, "000111110010010100")]
    [InlineData(8, "001000010110111100")]
    [InlineData(9, "001001101010011001")]
    [InlineData(10, "001010010011010011")]
    public void VersionBitsMatchTheStandard(int version, string bits) => Assert.Equal(Convert.ToInt32(bits, 2), QrCode.VersionBits(version));

    [Theory]
    [InlineData(1, "")]
    [InlineData(2, "6,18")]
    [InlineData(6, "6,34")]
    [InlineData(7, "6,22,38")]
    [InlineData(8, "6,24,42")]
    [InlineData(10, "6,28,50")]
    public void AlignmentPatternsSitWhereTheStandardPutsThem(int version, string positions) =>
        Assert.Equal(positions, string.Join(",", QrCode.AlignmentPositions(version)));

    [Theory]
    // Level M data codewords by version, from the standard's capacity table.
    [InlineData(1, 16)]
    [InlineData(2, 28)]
    [InlineData(3, 44)]
    [InlineData(4, 64)]
    [InlineData(5, 86)]
    [InlineData(6, 108)]
    [InlineData(7, 124)]
    [InlineData(8, 154)]
    [InlineData(9, 182)]
    [InlineData(10, 216)]
    public void DataCapacityMatchesTheStandard(int version, int codewords) => Assert.Equal(codewords, QrCode.DataCodewords(version));

    [Fact]
    public void TheFinderPatternsAndQuietCornersAreInPlace()
    {
        var qr = QrCode.Encode("nomad://enroll?hub=https%3A%2F%2Fhub.example.com%2F&token=abc");

        foreach (var (top, left) in new[] { (0, 0), (0, qr.Size - 7), (qr.Size - 7, 0) })
        {
            for (var r = 0; r < 7; r++)
            {
                for (var c = 0; c < 7; c++)
                {
                    var ring = Math.Max(Math.Abs(r - 3), Math.Abs(c - 3));
                    Assert.Equal(ring != 2, qr[top + r, left + c]);
                }
            }
        }

        Assert.True(qr[qr.Size - 8, 8]); // The dark module.
    }

    [Fact]
    public void TooLongForVersion10IsRefused()
    {
        Assert.Equal(10, QrCode.Encode(new string('a', 213)).Version);
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('a', 214)));
    }
}
