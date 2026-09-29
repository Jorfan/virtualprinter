using System.Text;

namespace VirtualPOSPrinter.UI;

public static class HexDump
{
    public const int MaxBytes = 64 * 1024;

    /// <summary>Header plus classic offset / hex / ASCII dump of the first <see cref="MaxBytes"/> bytes of a file.</summary>
    public static string FromFile(string path, string header)
    {
        using var fs = File.OpenRead(path);
        var len = (int)Math.Min(fs.Length, MaxBytes);
        var data = new byte[len];
        fs.ReadExactly(data, 0, len);

        var sb = new StringBuilder(header).AppendLine();
        if (fs.Length > len) sb.AppendLine($"(showing first {len} bytes)");
        sb.AppendLine();
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++)
            {
                sb.Append(i + j < data.Length ? data[i + j].ToString("X2") : "  ").Append(' ');
                if (j == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                var b = data[i + j];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.AppendLine("|");
        }
        return sb.ToString();
    }
}
