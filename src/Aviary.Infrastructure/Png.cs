using System.Buffers.Binary;
using System.IO.Compression;

namespace Aviary.Infrastructure;

// Minimal PNG writer for guest screenshots: 8-bit RGB, no filtering.
public static class Png
{
    public static byte[] FromRgb565(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (pixels.Length < width * height * 2) throw new ArgumentException("Too little image data for the size.", nameof(pixels));
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            int v = BinaryPrimitives.ReadUInt16LittleEndian(pixels.Slice(i * 2, 2));
            rgb[i * 3] = (byte)((v >> 11 & 0x1F) * 255 / 31);
            rgb[i * 3 + 1] = (byte)((v >> 5 & 0x3F) * 255 / 63);
            rgb[i * 3 + 2] = (byte)((v & 0x1F) * 255 / 31);
        }
        return FromRgb(rgb, width, height);
    }

    public static byte[] FromRgb(byte[] rgb, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < height; y++) { z.WriteByte(0); z.Write(rgb, y * width * 3, width * 3); }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2; // 8-bit truecolor
        Chunk(png, "IHDR", header); Chunk(png, "IDAT", raw.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed); data.CopyTo(typed, 4);
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed)); output.Write(number);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
