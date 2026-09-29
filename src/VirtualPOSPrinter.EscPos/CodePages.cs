using System.Text;

namespace VirtualPOSPrinter.EscPos;

/// <summary>
/// Maps ESC t numbers to code pages and decodes bytes. ESC t numbering differs between printer vendors,
/// so the table is a best guess and callers can override the code page for a whole job.
/// </summary>
public static class CodePages
{
    /// <summary>Selectable code pages for a manual override: display name and Windows code page number.</summary>
    public static readonly IReadOnlyList<(string Name, int CodePage)> Available = new[]
    {
        ("CP437 (US)", 437), ("CP850 (Latin 1)", 850), ("CP852 (Latin 2)", 852), ("CP858 (Latin 1 + euro)", 858),
        ("CP737 (Greek)", 737), ("CP869 (Greek)", 869), ("CP1253 (Windows Greek)", 1253), ("ISO-8859-7 (Greek)", 28597),
        ("CP1252 (Windows Latin 1)", 1252), ("CP860 (Portuguese)", 860), ("CP863 (Canadian French)", 863),
        ("CP865 (Nordic)", 865), ("CP866 (Cyrillic)", 866),
    };

    private static readonly Dictionary<int, int> EscTMap = new()
    {
        [0] = 437, [1] = 437, [2] = 850, [3] = 860, [4] = 863, [5] = 865,
        [7] = 1253,   // Greek printers commonly use page 7 for Windows-1253
        [14] = 737, [15] = 28597, [16] = 1252, [17] = 866, [18] = 852, [19] = 858,
        [28] = 869,
    };

    private static readonly Dictionary<int, Encoding> Cache = new();

    static CodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Windows code page for an ESC t argument, or null when the number is not mapped.</summary>
    public static int? FromEscT(int n) => EscTMap.TryGetValue(n, out var cp) ? cp : null;

    public static Encoding Get(int codePage)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(codePage, out var enc))
            {
                try { enc = Encoding.GetEncoding(codePage); }
                catch (Exception) { enc = Encoding.GetEncoding(437); }
                Cache[codePage] = enc;
            }
            return enc;
        }
    }
}
