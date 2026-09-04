// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;

namespace MeshRF.Map;

/// <summary>
/// Decoded pixels of a PNG, as tightly packed 8-bit RGB.
///
/// Deliberately minimal: this exists so <see cref="TerrainTiles"/> can read
/// elevation out of a Terrarium tile inside MeshRF.Core, which has no imaging
/// dependency and cannot take one — the library is the part that stays
/// UI-framework agnostic and unit-testable. The app already decodes basemap
/// tiles through Avalonia, but a terrain tile is data rather than something
/// drawn, so routing it through the toolkit would put the elevation lookup on
/// the wrong side of the app boundary.
///
/// Only what the elevation service actually receives is supported: 8 bits per
/// channel, colour type 2 (RGB) or 6 (RGBA), non-interlaced. Anything else
/// throws rather than guessing, because a silently misread tile becomes a
/// plausible-looking wrong elevation.
/// </summary>
public sealed class PngImage
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major, three bytes per pixel, no row padding.</summary>
    public byte[] Rgb { get; }

    private PngImage(int width, int height, byte[] rgb)
    {
        Width = width;
        Height = height;
        Rgb = rgb;
    }

    /// <summary>Wraps pixels that are already decoded. For callers holding
    /// image data from somewhere other than a PNG stream.</summary>
    public static PngImage FromRgb(int width, int height, byte[] rgb)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "an image needs both dimensions");
        if (rgb.Length != width * height * 3)
            throw new ArgumentException(
                $"expected {width * height * 3} bytes for {width}x{height}, got {rgb.Length}", nameof(rgb));
        return new PngImage(width, height, rgb);
    }

    public (byte R, byte G, byte B) Pixel(int x, int y)
    {
        int i = (y * Width + x) * 3;
        return (Rgb[i], Rgb[i + 1], Rgb[i + 2]);
    }

    public static PngImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG");

        int width = 0, height = 0, bytesPerPixel = 0;
        bool sawHeader = false;
        var idat = new MemoryStream();

        int pos = 8;
        while (pos + 8 <= data.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[pos..]));
            var type = data.Slice(pos + 4, 4);
            int bodyStart = pos + 8;
            if (length < 0 || bodyStart + length + 4 > data.Length)
                throw new InvalidDataException("truncated PNG chunk");
            var body = data.Slice(bodyStart, length);

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length < 13) throw new InvalidDataException("short IHDR");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
                byte bitDepth = body[8];
                byte colourType = body[9];
                byte interlace = body[12];

                if (width <= 0 || height <= 0)
                    throw new InvalidDataException("empty PNG");
                if (bitDepth != 8)
                    throw new NotSupportedException($"PNG bit depth {bitDepth} is not supported, only 8");
                if (interlace != 0)
                    throw new NotSupportedException("interlaced PNG is not supported");
                bytesPerPixel = colourType switch
                {
                    2 => 3,
                    6 => 4,
                    _ => throw new NotSupportedException($"PNG colour type {colourType} is not supported, only 2 and 6"),
                };
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            pos = bodyStart + length + 4; // skip the body and its CRC
        }

        if (!sawHeader) throw new InvalidDataException("PNG has no IHDR");
        if (idat.Length == 0) throw new InvalidDataException("PNG has no IDAT");

        idat.Position = 0;
        int stride = width * bytesPerPixel;
        var raw = new byte[checked(stride * height)];
        using (var inflate = new ZLibStream(idat, CompressionMode.Decompress))
        {
            Unfilter(inflate, raw, stride, height, bytesPerPixel);
        }

        return new PngImage(width, height, bytesPerPixel == 3 ? raw : DropAlpha(raw, width, height));
    }

    /// <summary>Reads the filtered scanlines and reverses each row's filter in
    /// place. Every filter but None refers to the row above, so this has to run
    /// top to bottom over the whole image rather than per row on demand.</summary>
    private static void Unfilter(Stream source, byte[] raw, int stride, int height, int bpp)
    {
        var line = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = source.ReadByte();
            if (filter < 0) throw new InvalidDataException("truncated PNG image data");
            ReadExactly(source, line);

            int row = y * stride;
            int prior = row - stride;
            for (int i = 0; i < stride; i++)
            {
                byte a = i >= bpp ? raw[row + i - bpp] : (byte)0;      // left
                byte b = y > 0 ? raw[prior + i] : (byte)0;             // above
                byte c = y > 0 && i >= bpp ? raw[prior + i - bpp] : (byte)0; // above-left
                raw[row + i] = filter switch
                {
                    0 => line[i],
                    1 => (byte)(line[i] + a),
                    2 => (byte)(line[i] + b),
                    3 => (byte)(line[i] + (a + b) / 2),
                    4 => (byte)(line[i] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"unknown PNG row filter {filter}"),
                };
            }
        }
    }

    private static void ReadExactly(Stream source, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = source.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new InvalidDataException("truncated PNG image data");
            read += n;
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] DropAlpha(byte[] rgba, int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
        {
            rgb[j] = rgba[i];
            rgb[j + 1] = rgba[i + 1];
            rgb[j + 2] = rgba[i + 2];
        }
        return rgb;
    }
}
