using System.Text;

namespace VirtualPOSPrinter.EscPos;

/// <summary>Result of encoding a barcode: dark (true) / light (false) modules plus the human-readable text.</summary>
public sealed record BarcodeModules(bool[] Modules, string Text);

/// <summary>Barcode encoders for the GS k symbologies. Returns null for unsupported kinds or invalid data.</summary>
public static class Barcodes
{
    public static BarcodeModules? Encode(int kind, byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        try
        {
            return kind switch
            {
                0 => Upc(text),
                2 => Ean13(text),
                3 => Ean8(text),
                4 => Code39(text),
                5 => Itf(text),
                6 => Codabar(text),
                8 => Code128(data),
                _ => null,     // UPC-E, Code93: not rendered
            };
        }
        catch (Exception) { return null; }
    }

    public static string KindName(int kind) => kind switch
    {
        0 => "UPC-A", 1 => "UPC-E", 2 => "EAN-13", 3 => "EAN-8", 4 => "CODE39", 5 => "ITF",
        6 => "CODABAR", 7 => "CODE93", 8 => "CODE128", _ => "BARCODE"
    };

    // ---- helpers ---------------------------------------------------------------------------

    private static void Append(List<bool> m, string pattern)
    {
        foreach (var c in pattern) m.Add(c == '1');
    }

    /// <summary>Appends alternating bar/space elements; 'n' is one module, 'w' is <paramref name="wide"/> modules.</summary>
    private static void AppendWidths(List<bool> m, string nw, int wide, ref bool bar)
    {
        foreach (var c in nw)
        {
            int w = c == 'w' ? wide : 1;
            for (int i = 0; i < w; i++) m.Add(bar);
            bar = !bar;
        }
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    // ---- EAN / UPC -------------------------------------------------------------------------

    private static readonly string[] L =
        { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };

    private static string R(int d) => new(L[d].Select(c => c == '1' ? '0' : '1').ToArray());
    private static string G(int d) => new(R(d).Reverse().ToArray());

    private static readonly string[] Parity =
        { "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL" };

    public static int EanCheckDigit(string digitsWithoutCheck)
    {
        int sum = 0;
        for (int i = 0; i < digitsWithoutCheck.Length; i++)
        {
            int d = digitsWithoutCheck[digitsWithoutCheck.Length - 1 - i] - '0';
            sum += i % 2 == 0 ? d * 3 : d;
        }
        return (10 - sum % 10) % 10;
    }

    private static BarcodeModules? Upc(string text)
    {
        var d = Digits(text);
        if (d.Length == 11) d += EanCheckDigit(d);
        if (d.Length != 12) return null;
        var r = Ean13("0" + d);
        return r == null ? null : r with { Text = d };
    }

    private static BarcodeModules? Ean13(string text)
    {
        var d = Digits(text);
        if (d.Length == 12) d += EanCheckDigit(d);
        if (d.Length != 13) return null;

        var m = new List<bool>();
        Append(m, "101");
        var parity = Parity[d[0] - '0'];
        for (int i = 0; i < 6; i++)
        {
            int digit = d[i + 1] - '0';
            Append(m, parity[i] == 'L' ? L[digit] : G(digit));
        }
        Append(m, "01010");
        for (int i = 7; i < 13; i++) Append(m, R(d[i] - '0'));
        Append(m, "101");
        return new BarcodeModules(m.ToArray(), d);
    }

    private static BarcodeModules? Ean8(string text)
    {
        var d = Digits(text);
        if (d.Length == 7) d += EanCheckDigit(d);
        if (d.Length != 8) return null;

        var m = new List<bool>();
        Append(m, "101");
        for (int i = 0; i < 4; i++) Append(m, L[d[i] - '0']);
        Append(m, "01010");
        for (int i = 4; i < 8; i++) Append(m, R(d[i] - '0'));
        Append(m, "101");
        return new BarcodeModules(m.ToArray(), d);
    }

    // ---- Code 39 ---------------------------------------------------------------------------

    internal static readonly Dictionary<char, string> Code39Patterns = new()
    {
        ['0'] = "nnnwwnwnn", ['1'] = "wnnwnnnnw", ['2'] = "nnwwnnnnw", ['3'] = "wnwwnnnnn", ['4'] = "nnnwwnnnw",
        ['5'] = "wnnwwnnnn", ['6'] = "nnwwwnnnn", ['7'] = "nnnwnnwnw", ['8'] = "wnnwnnwnn", ['9'] = "nnwwnnwnn",
        ['A'] = "wnnnnwnnw", ['B'] = "nnwnnwnnw", ['C'] = "wnwnnwnnn", ['D'] = "nnnnwwnnw", ['E'] = "wnnnwwnnn",
        ['F'] = "nnwnwwnnn", ['G'] = "nnnnnwwnw", ['H'] = "wnnnnwwnn", ['I'] = "nnwnnwwnn", ['J'] = "nnnnwwwnn",
        ['K'] = "wnnnnnnww", ['L'] = "nnwnnnnww", ['M'] = "wnwnnnnwn", ['N'] = "nnnnwnnww", ['O'] = "wnnnwnnwn",
        ['P'] = "nnwnwnnwn", ['Q'] = "nnnnnnwww", ['R'] = "wnnnnnwwn", ['S'] = "nnwnnnwwn", ['T'] = "nnnnwnwwn",
        ['U'] = "wwnnnnnnw", ['V'] = "nwwnnnnnw", ['W'] = "wwwnnnnnn", ['X'] = "nwnnwnnnw", ['Y'] = "wwnnwnnnn",
        ['Z'] = "nwwnwnnnn", ['-'] = "nwnnnnwnw", ['.'] = "wwnnnnwnn", [' '] = "nwwnnnwnn", ['*'] = "nwnnwnwnn",
        ['$'] = "nwnwnwnnn", ['/'] = "nwnwnnnwn", ['+'] = "nwnnnwnwn", ['%'] = "nnnwnwnwn",
    };

    private static BarcodeModules? Code39(string text)
    {
        var s = text.ToUpperInvariant().Replace("*", "");
        if (s.Length == 0 || s.Any(c => c == '*' || !Code39Patterns.ContainsKey(c))) return null;

        var m = new List<bool>();
        foreach (var c in "*" + s + "*")
        {
            bool bar = true;
            AppendWidths(m, Code39Patterns[c], 3, ref bar);
            m.Add(false);   // inter-character gap
        }
        m.RemoveAt(m.Count - 1);
        return new BarcodeModules(m.ToArray(), s);
    }

    // ---- ITF (interleaved 2 of 5) ----------------------------------------------------------

    internal static readonly string[] ItfPatterns =
        { "nnwwn", "wnnnw", "nwnnw", "wwnnn", "nnwnw", "wnwnn", "nwwnn", "nnnww", "wnnwn", "nwnwn" };

    private static BarcodeModules? Itf(string text)
    {
        var d = Digits(text);
        if (d.Length == 0) return null;
        if (d.Length % 2 == 1) d = "0" + d;

        var m = new List<bool>();
        bool bar = true;
        AppendWidths(m, "nnnn", 3, ref bar);
        for (int i = 0; i < d.Length; i += 2)
        {
            var a = ItfPatterns[d[i] - '0'];
            var b = ItfPatterns[d[i + 1] - '0'];
            for (int k = 0; k < 5; k++)
            {
                bool barState = true;
                var single = new List<bool>();
                AppendWidths(single, a[k].ToString(), 3, ref barState);   // bar element
                m.AddRange(single);
                barState = false;
                single.Clear();
                AppendWidths(single, b[k].ToString(), 3, ref barState);   // space element
                m.AddRange(single);
            }
        }
        bar = true;
        AppendWidths(m, "wnn", 3, ref bar);
        return new BarcodeModules(m.ToArray(), d);
    }

    // ---- Codabar ---------------------------------------------------------------------------

    private static readonly Dictionary<char, string> CodabarPatterns = new()
    {
        ['0'] = "nnnnnww", ['1'] = "nnnnwwn", ['2'] = "nnnwnnw", ['3'] = "wwnnnnn", ['4'] = "nnwnnwn",
        ['5'] = "wnnnnwn", ['6'] = "nwnnnnw", ['7'] = "nwnnwnn", ['8'] = "nwwnnnn", ['9'] = "wnnwnnn",
        ['-'] = "nnnwwnn", ['$'] = "nnwwnnn", [':'] = "wnnnwnw", ['/'] = "wnwnnnw", ['.'] = "wnwnwnw", ['+'] = "nnwnwnw",
        ['A'] = "nnwwnwn", ['B'] = "nwnwnnw", ['C'] = "nnnwnww", ['D'] = "nnnwwwn",
    };

    private static BarcodeModules? Codabar(string text)
    {
        var s = text.ToUpperInvariant();
        if (s.Length == 0) return null;
        bool hasStart = "ABCD".Contains(s[0]);
        bool hasStop = s.Length > 1 && "ABCD".Contains(s[^1]);
        if (!hasStart) s = "A" + s;
        if (!hasStop) s += "A";
        if (s.Any(c => !CodabarPatterns.ContainsKey(c))) return null;

        var m = new List<bool>();
        foreach (var c in s)
        {
            bool bar = true;
            AppendWidths(m, CodabarPatterns[c], 3, ref bar);
            m.Add(false);
        }
        m.RemoveAt(m.Count - 1);
        return new BarcodeModules(m.ToArray(), s);
    }

    // ---- Code 128 --------------------------------------------------------------------------

    internal static readonly string[] Code128Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    };

    /// <summary>
    /// Code 128 as sent by ESC/POS: the data starts with a code set selector "{A", "{B" or "{C";
    /// "{{" is a literal brace and "{1".."{4" are FNC1-FNC4. Without a selector, set B is assumed.
    /// </summary>
    private static BarcodeModules? Code128(byte[] data)
    {
        var values = new List<int>();
        var human = new StringBuilder();
        int set = 0;   // 0 none yet, 1 A, 2 B, 3 C
        int i = 0;

        void Switch(int newSet)
        {
            if (set == 0) values.Add(newSet == 1 ? 103 : newSet == 2 ? 104 : 105);
            else values.Add(newSet == 1 ? 101 : newSet == 2 ? 100 : 99);
            set = newSet;
        }

        if (data.Length >= 2 && data[0] == '{' && data[1] is (byte)'A' or (byte)'B' or (byte)'C')
        {
            Switch(data[1] - 'A' + 1);
            i = 2;
        }
        else Switch(2);

        while (i < data.Length)
        {
            byte b = data[i];
            if (b == '{' && i + 1 < data.Length)
            {
                byte n = data[i + 1];
                if (n is (byte)'A' or (byte)'B' or (byte)'C') { Switch(n - 'A' + 1); i += 2; continue; }
                if (n == '{') { values.Add('{' - 32); human.Append('{'); i += 2; continue; }
                if (n is >= (byte)'1' and <= (byte)'4')
                {
                    values.Add(n switch { (byte)'1' => 102, (byte)'2' => 97, (byte)'3' => 96, _ => set == 1 ? 101 : 100 });
                    i += 2;
                    continue;
                }
            }
            if (set == 3)
            {
                if (i + 1 < data.Length && char.IsDigit((char)b) && char.IsDigit((char)data[i + 1]))
                {
                    values.Add((b - '0') * 10 + (data[i + 1] - '0'));
                    human.Append((char)b).Append((char)data[i + 1]);
                    i += 2;
                    continue;
                }
                return null;   // odd digit in set C
            }
            if (set == 1) values.Add(b < 32 ? b + 64 : b - 32);
            else values.Add(b - 32);
            if (b >= 32) human.Append((char)b);
            i++;
        }

        int sum = values[0];
        for (int k = 1; k < values.Count; k++) sum += values[k] * k;
        values.Add(sum % 103);
        values.Add(106);

        var m = new List<bool>();
        foreach (var v in values)
        {
            if (v < 0 || v > 106) return null;
            bool bar = true;
            foreach (var c in Code128Patterns[v])
            {
                for (int k = 0; k < c - '0'; k++) m.Add(bar);
                bar = !bar;
            }
        }
        return new BarcodeModules(m.ToArray(), human.ToString());
    }
}
