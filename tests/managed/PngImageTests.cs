// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The PNG reader that lets Core pull elevation out of a terrain tile without
/// an imaging dependency. Every row filter is exercised: a filter reversed
/// wrongly does not fail, it produces plausible pixels, and a plausible wrong
/// pixel is a plausible wrong elevation.
/// </summary>
public class PngImageTests
{
    [Theory]
    [InlineData(0)] // None
    [InlineData(1)] // Sub
    [InlineData(2)] // Up
    [InlineData(3)] // Average
    [InlineData(4)] // Paeth
    public void EveryRowFilterRoundTrips(byte filter)
    {
        var pixels = Gradient(8, 5, channels: 3);
        var png = PngImage.Decode(Encode(8, 5, 3, pixels, filter));

        Assert.Equal(8, png.Width);
        Assert.Equal(5, png.Height);
        Assert.Equal(pixels, png.Rgb);
    }

    [Fact]
    public void MixedFiltersDownTheImageRoundTrip()
    {
        var pixels = Gradient(16, 16, channels: 3);
        var png = PngImage.Decode(Encode(16, 16, 3, pixels, rowFilter: y => (byte)(y % 5)));

        Assert.Equal(pixels, png.Rgb);
    }

    [Fact]
    public void AnAlphaChannelIsDroppedRatherThanShiftingTheColours()
    {
        var rgba = Gradient(4, 4, channels: 4);
        var png = PngImage.Decode(Encode(4, 4, 4, rgba, filter: 0));

        Assert.Equal((rgba[0], rgba[1], rgba[2]), png.Pixel(0, 0));
        Assert.Equal((rgba[4], rgba[5], rgba[6]), png.Pixel(1, 0));
    }

    [Fact]
    public void SomethingThatIsNotAPngIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => PngImage.Decode("<html>404</html>"u8));
    }

    [Fact]
    public void AFormatTheElevationServiceNeverSendsIsRefusedRatherThanGuessed()
    {
        // Colour type 3 is paletted. Reading its index bytes as RGB would
        // produce elevations that look real.
        var body = new byte[] { 0, 0, 0, 4, 0, 0, 0, 4, 8, 3, 0, 0, 0 };
        Assert.Throws<NotSupportedException>(() => PngImage.Decode(OnlyHeader(body)));
    }

    [Fact]
    public void ATruncatedTileIsRefused()
    {
        var whole = Encode(4, 4, 3, Gradient(4, 4, 3), filter: 0);
        Assert.Throws<InvalidDataException>(() => PngImage.Decode(whole.AsSpan(0, whole.Length / 2)));
    }

    // -- encoding helpers ---------------------------------------------------

    private static byte[] Gradient(int width, int height, int channels)
    {
        var pixels = new byte[width * height * channels];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7 + 13);
        return pixels;
    }

    private static byte[] Encode(int width, int height, int channels, byte[] pixels, byte filter) =>
        Encode(width, height, channels, pixels, _ => filter);

    /// <summary>Writes a PNG the decoder has to unfilter, applying the chosen
    /// filter to each row exactly as the spec defines it.</summary>
    private static byte[] Encode(
        int width, int height, int channels, byte[] pixels, Func<int, byte> rowFilter)
    {
        int stride = width * channels;
        var filtered = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            byte filter = rowFilter(y);
            filtered.WriteByte(filter);
            for (int i = 0; i < stride; i++)
            {
                int here = y * stride + i;
                byte a = i >= channels ? pixels[here - channels] : (byte)0;
                byte b = y > 0 ? pixels[here - stride] : (byte)0;
                byte c = y > 0 && i >= channels ? pixels[here - stride - channels] : (byte)0;
                filtered.WriteByte(filter switch
                {
                    0 => pixels[here],
                    1 => (byte)(pixels[here] - a),
                    2 => (byte)(pixels[here] - b),
                    3 => (byte)(pixels[here] - (a + b) / 2),
                    4 => (byte)(pixels[here] - Paeth(a, b, c)),
                    _ => throw new ArgumentOutOfRangeException(nameof(rowFilter)),
                });
            }
        }

        var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(filtered.ToArray());

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = channels == 4 ? (byte)6 : (byte)2;

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, deflated.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static byte[] OnlyHeader(byte[] ihdr)
    {
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    /// <summary>The CRC is written as zero: the decoder does not verify it, and
    /// a real checksum here would test the test rather than the reader.</summary>
    private static void WriteChunk(Stream target, ReadOnlySpan<byte> type, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        target.Write(length);
        target.Write(type);
        target.Write(body);
        target.Write([0, 0, 0, 0]);
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
