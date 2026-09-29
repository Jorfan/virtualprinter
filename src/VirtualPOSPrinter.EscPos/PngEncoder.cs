using System.IO.Compression;

namespace VirtualPOSPrinter.EscPos;

/// <summary>Encodes 1-bit monochrome bitmaps as PNG, without external libraries.</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        WriteBE(s, (uint)data.Length);
        s.Write(t);
        s.Write(data);
        WriteBE(s, Crc(t, data));
    }

    private static void WriteBE(Stream s, uint v) =>
        s.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    /// <param name="bits">Packed rows, MSB first, 1 = black, (width+7)/8 bytes per row.</param>
    public static byte[] EncodeMono(int width, int height, byte[] bits)
    {
        int rowBytes = (width + 7) / 8;
        var raw = new byte[(rowBytes + 1) * height];
        for (int y = 0; y < height; y++)
        {
            raw[y * (rowBytes + 1)] = 0;   // filter: none
            for (int x = 0; x < rowBytes; x++)
            {
                int idx = y * rowBytes + x;
                byte v = idx < bits.Length ? bits[idx] : (byte)0;
                raw[y * (rowBytes + 1) + 1 + x] = (byte)~v;   // PNG grayscale: 0 = black
            }
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
        ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
        ihdr[8] = 1;    // bit depth
        ihdr[9] = 0;    // grayscale
        WriteChunk(ms, "IHDR", ihdr);

        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw);
            WriteChunk(ms, "IDAT", z.ToArray());
        }
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }
}
