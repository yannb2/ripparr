using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Ripparr.Subtitles;

/// <summary>A small PNG writer: enough for caption pictures (RGBA for the page, grey for the OCR).</summary>
public static class Png
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    public static byte[] Rgba(RgbaImage image)
    {
        var raw = new byte[image.Height * (image.Width * 4 + 1)];
        var at = 0;
        for (var y = 0; y < image.Height; y++)
        {
            raw[at++] = 0; // filter: none
            for (var x = 0; x < image.Width; x++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(at), image.Pixels[y * image.Width + x]);
                at += 4;
            }
        }

        return Encode(image.Width, image.Height, 6, raw);
    }

    public static byte[] Grey(byte[] pixels, int width, int height)
    {
        var raw = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * width, raw, y * (width + 1) + 1, width);
        }

        return Encode(width, height, 0, raw);
    }

    private static byte[] Encode(int width, int height, byte colorType, byte[] raw)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per sample
        header[9] = colorType;
        Chunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFFu);
        output.Write(number);
    }
}
