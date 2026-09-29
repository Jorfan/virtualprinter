using System.Text;
using VirtualPOSPrinter.EscPos;

// Tiny dependency-free test runner: `dotnet run --project tests/VirtualPOSPrinter.Tests`
var tests = new List<(string Name, Action Body)>();
void Test(string name, Action body) => tests.Add((name, body));

void True(bool cond, string msg = "condition was false") { if (!cond) throw new Exception(msg); }
void Eq<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected <{expected}> but was <{actual}>"); }

byte[] Bytes(params object[] parts)
{
    var l = new List<byte>();
    foreach (var p in parts)
        switch (p)
        {
            case string s: l.AddRange(Encoding.Latin1.GetBytes(s)); break;
            case int i: l.Add((byte)i); break;
            case char ch: l.Add((byte)ch); break;
            case byte b: l.Add(b); break;
            case byte[] a: l.AddRange(a); break;
            default: throw new ArgumentException("bad part");
        }
    return l.ToArray();
}

ParseResult Parse(byte[] data, int? cp = null, int mm = 80) => EscPosParser.Parse(data, new ParserOptions { CodePageOverride = cp, PaperWidthMm = mm });
List<TextLine> Lines(ParseResult r, int receipt = 0) => r.Receipts[receipt].Blocks.OfType<TextLine>().ToList();
string Text(TextLine l) => string.Concat(l.Runs.Select(r => r.Text));

const int ESC = 0x1B, GS = 0x1D;

// ---- text & style ----------------------------------------------------------------------------

Test("plain text produces one line per LF", () =>
{
    var r = Parse(Bytes("Hello\nWorld\n"));
    Eq(1, r.Receipts.Count);
    var lines = Lines(r);
    Eq(2, lines.Count);
    Eq("Hello", Text(lines[0]));
    Eq("World", Text(lines[1]));
});

Test("text without trailing LF is still rendered", () => Eq("Tail", Text(Lines(Parse(Bytes("Tail")))[0])));

Test("bold on/off splits runs", () =>
{
    var line = Lines(Parse(Bytes(ESC, 'E', 1, "A", ESC, 'E', 0, "B", "\n")))[0];
    Eq(2, line.Runs.Count);
    True(line.Runs[0].Bold && !line.Runs[1].Bold);
});

Test("underline ESC -", () => True(Lines(Parse(Bytes(ESC, '-', 2, "U\n")))[0].Runs[0].Underline == 2));

Test("GS ! sets width and height multipliers", () =>
{
    var run = Lines(Parse(Bytes(GS, '!', 0x11, "Big\n")))[0].Runs[0];
    Eq(2, run.WidthMul);
    Eq(2, run.HeightMul);
});

Test("ESC ! sets bold, double height/width and underline bits", () =>
{
    var run = Lines(Parse(Bytes(ESC, '!', 0x08 | 0x10 | 0x20 | 0x80, "X\n")))[0].Runs[0];
    True(run.Bold && run.Underline == 1 && run.WidthMul == 2 && run.HeightMul == 2);
});

Test("alignment latches per line", () =>
{
    var lines = Lines(Parse(Bytes(ESC, 'a', 1, "Mid\n", ESC, 'a', 2, "Right\n", ESC, 'a', 0, "Left\n")));
    Eq(Align.Center, lines[0].Align);
    Eq(Align.Right, lines[1].Align);
    Eq(Align.Left, lines[2].Align);
});

Test("ESC @ resets style", () =>
{
    var line = Lines(Parse(Bytes(ESC, 'E', 1, GS, '!', 0x11, ESC, '@', "Plain\n")))[0];
    True(!line.Runs[0].Bold && line.Runs[0].WidthMul == 1 && line.Runs[0].HeightMul == 1);
});

Test("line spacing ESC 3 changes empty line height, ESC 2 restores default", () =>
{
    var lines = Lines(Parse(Bytes(ESC, '3', 50, "a\n", ESC, '2', "b\n")));
    Eq(50, lines[0].HeightDots);
    Eq(30, lines[1].HeightDots);
});

Test("text wraps at paper width (48 columns on 80 mm, 32 on 58 mm)", () =>
{
    var text = new string('X', 100);
    var l80 = Lines(Parse(Bytes(text, "\n"), mm: 80));
    Eq(3, l80.Count);
    Eq(48, Text(l80[0]).Length);
    var l58 = Lines(Parse(Bytes(text, "\n"), mm: 58));
    Eq(32, Text(l58[0]).Length);
});

Test("ESC d feeds lines", () =>
{
    var r = Parse(Bytes("A\n", ESC, 'd', 3, "B\n"));
    True(r.Receipts[0].Blocks.OfType<FeedBlock>().Single().Dots == 90);
});

// ---- cut -------------------------------------------------------------------------------------

Test("GS V splits the job into one receipt per cut", () =>
{
    var r = Parse(Bytes("One\n", GS, 'V', 0, "Two\n", GS, 'V', 66, 0, "Three\n"));
    Eq(3, r.Receipts.Count);
    Eq("Two", Text(Lines(r, 1)[0]));
});

Test("trailing cut and feeds do not create an empty receipt", () =>
{
    var r = Parse(Bytes("One\n", GS, 'V', 1, ESC, 'd', 4));
    Eq(1, r.Receipts.Count);
});

// ---- code pages ------------------------------------------------------------------------------

Test("CP737 decodes Greek", () => Eq("ΑΒΓ", Text(Lines(Parse(Bytes(0x80, 0x81, 0x82, "\n"), cp: 737))[0])));
Test("CP1253 decodes Greek", () => Eq("αβγ", Text(Lines(Parse(Bytes(0xE1, 0xE2, 0xE3, "\n"), cp: 1253))[0])));
Test("ISO-8859-7 decodes Greek", () => Eq("αβγ", Text(Lines(Parse(Bytes(0xE1, 0xE2, 0xE3, "\n"), cp: 28597))[0])));
Test("CP869 decodes Greek", () => Eq("ΘΙ", Text(Lines(Parse(Bytes(0xAC, 0xAD, "\n"), cp: 869))[0])));
Test("CP852 decodes Central European", () => Eq("č", Text(Lines(Parse(Bytes(0x9F, "\n"), cp: 852))[0])));
Test("CP858 has the euro sign", () => Eq("€", Text(Lines(Parse(Bytes(0xD5, "\n"), cp: 858))[0])));

Test("ESC t 7 selects Windows-1253 (matches the sample Greek job)", () =>
    Eq("Παραγγελία", Text(Lines(Parse(Bytes(ESC, 't', 7, 0xD0, 0xE1, 0xF1, 0xE1, 0xE3, 0xE3, 0xE5, 0xEB, 0xDF, 0xE1, "\n")))[0])));

Test("ESC t 14 selects CP737", () => Eq("Α", Text(Lines(Parse(Bytes(ESC, 't', 14, 0x80, "\n")))[0])));

Test("code page override wins over ESC t", () =>
    Eq("Α", Text(Lines(Parse(Bytes(ESC, 't', 0, 0x80, "\n"), cp: 737))[0])));

Test("code page change mid-line applies to following bytes", () =>
{
    var line = Lines(Parse(Bytes(ESC, 't', 14, 0x80, ESC, 't', 7, 0xE1, "\n")))[0];
    Eq("Αα", Text(line));
});

// ---- graphics --------------------------------------------------------------------------------

Test("GS v 0 raster image", () =>
{
    var r = Parse(Bytes(GS, 'v', '0', 0, 2, 0, 3, 0, new byte[] { 0xFF, 0x00, 0xAA, 0x55, 0x0F, 0xF0 }));
    var img = r.Receipts[0].Blocks.OfType<ImageBlock>().Single();
    Eq(16, img.Width);
    Eq(3, img.Height);
    Eq(6, img.Bits.Length);
    Eq(1, img.ScaleX);
});

Test("truncated raster image keeps complete rows and does not throw", () =>
{
    var r = Parse(Bytes(GS, 'v', '0', 0, 2, 0, 10, 0, new byte[] { 1, 2, 3, 4, 5 }));
    Eq(2, r.Receipts[0].Blocks.OfType<ImageBlock>().Single().Height);
});

Test("ESC * bit image renders and swallows following LF", () =>
{
    var r = Parse(Bytes(ESC, '*', 0, 2, 0, 0xFF, 0x0F, "\n", "After\n"));
    var blocks = r.Receipts[0].Blocks;
    Eq(2, blocks.Count);
    Eq(8, ((ImageBlock)blocks[0]).Height);
});

Test("QR code via GS ( k", () =>
{
    var data = Encoding.ASCII.GetBytes("HELLO");
    var r = Parse(Bytes(
        GS, '(', 'k', 4, 0, 49, 65, 50, 0,
        GS, '(', 'k', 3, 0, 49, 67, 6,
        GS, '(', 'k', 3, 0, 49, 69, 49,
        GS, '(', 'k', data.Length + 3, 0, 49, 80, 48, data,
        GS, '(', 'k', 3, 0, 49, 81, 48));
    var qr = r.Receipts[0].Blocks.OfType<QrBlock>().Single();
    Eq("HELLO", Encoding.ASCII.GetString(qr.Data));
    Eq(6, qr.ModuleSize);
    Eq(QrEcc.M, qr.Ecc);
});

Test("barcode GS k in both forms", () =>
{
    var r = Parse(Bytes(GS, 'h', 80, GS, 'w', 2, GS, 'H', 2, GS, 'k', 4, "ABC123", 0, GS, 'k', 73, 5, "{B12A"));
    var codes = r.Receipts[0].Blocks.OfType<BarcodeBlock>().ToList();
    Eq(2, codes.Count);
    Eq(4, codes[0].Kind);
    Eq("ABC123", Encoding.ASCII.GetString(codes[0].Data));
    Eq(8, codes[1].Kind);
    Eq(80, codes[0].HeightDots);
    Eq(2, codes[0].ModuleWidth);
    Eq(2, codes[0].HriPosition);
});

// ---- robustness ------------------------------------------------------------------------------

Test("unknown commands are skipped without losing text", () =>
{
    var r = Parse(Bytes("A", ESC, 0xFE, "B", GS, 0xFE, "C", ESC, 'p', 0, 25, 250, "D\n"));
    Eq("ABCD", Text(Lines(r)[0]));
    True(r.Warnings.Count >= 2);
});

Test("status queries are ignored (DLE EOT, GS r, GS a)", () =>
{
    var r = Parse(Bytes(0x10, 0x04, 1, GS, 'r', 1, GS, 'a', 0xFF, "OK\n"));
    Eq("OK", Text(Lines(r)[0]));
});

Test("GS ( with unknown function is skipped by length", () =>
{
    var r = Parse(Bytes(GS, '(', 'A', 4, 0, 1, 2, 3, 4, "After\n"));
    Eq("After", Text(Lines(r)[0]));
});

Test("truncated commands at end of stream do not throw", () =>
{
    foreach (var seq in new[] { new byte[] { 0x1B }, new byte[] { 0x1D }, new byte[] { 0x1D, 0x76 }, new byte[] { 0x1D, 0x28, 0x6B, 0xFF },
                                new byte[] { 0x1B, 0x2A, 0x21, 0xFF, 0xFF }, new byte[] { 0x1D, 0x6B, 0x49, 0x80 } })
        Parse(seq);
});

Test("random garbage never throws and always renders", () =>
{
    var rnd = new Random(1234);
    for (int i = 0; i < 500; i++)
    {
        var data = new byte[rnd.Next(0, 400)];
        rnd.NextBytes(data);
        var r = Parse(data);
        HtmlRenderer.Render(r);
    }
});

// ---- encoders --------------------------------------------------------------------------------

Test("QR encoder: version 1 is 21x21 with finder patterns", () =>
{
    var qr = QrCode.Encode(Encoding.ASCII.GetBytes("HELLO"), QrEcc.L);
    Eq(1, qr.Version);
    Eq(21, qr.Size);
    True(qr[0, 0] && qr[0, 6] && qr[6, 0] && qr[6, 6] && !qr[1, 1] && qr[2, 2]);
    True(qr[0, 14] && qr[14, 0]);
    True(qr[13, 8]);   // dark module
});

Test("QR encoder picks larger versions for longer data", () =>
{
    Eq(1, QrCode.Encode(new byte[17], QrEcc.L).Version);
    Eq(2, QrCode.Encode(new byte[18], QrEcc.L).Version);
    Eq(40, QrCode.Encode(new byte[2953], QrEcc.L).Version);
    bool threw = false;
    try { QrCode.Encode(new byte[2954], QrEcc.L); } catch (ArgumentException) { threw = true; }
    True(threw);
});

Test("QR encoder handles every version/level without throwing", () =>
{
    foreach (QrEcc ecc in Enum.GetValues<QrEcc>())
        foreach (int len in new[] { 1, 20, 60, 150, 400, 900 })
        {
            var qr = QrCode.Encode(new byte[len], ecc);
            True(qr.Size == qr.Version * 4 + 17);
        }
});

Test("EAN-13 is 95 modules and check digit is computed", () =>
{
    var e = Barcodes.Encode(2, Encoding.ASCII.GetBytes("400638133393"))!;
    Eq(95, e.Modules.Length);
    Eq("4006381333931", e.Text);
});

Test("UPC-A and EAN-8 sizes", () =>
{
    Eq(95, Barcodes.Encode(0, Encoding.ASCII.GetBytes("01234567890"))!.Modules.Length);
    Eq(67, Barcodes.Encode(3, Encoding.ASCII.GetBytes("9638507"))!.Modules.Length);
});

Test("Code 39 patterns each have 3 wide elements", () =>
{
    foreach (var kv in Barcodes.Code39Patterns) Eq(3, kv.Value.Count(c => c == 'w'));
});

Test("Code 128 pattern table: every pattern spans 11 modules (13 for stop)", () =>
{
    Eq(107, Barcodes.Code128Patterns.Length);
    for (int i = 0; i < 106; i++) Eq(11, Barcodes.Code128Patterns[i].Sum(c => c - '0'));
    Eq(13, Barcodes.Code128Patterns[106].Sum(c => c - '0'));
});

Test("Code 128 known checksum: '{BPJJ123' region", () =>
{
    // Start B (104) + 'A'(33)*1 -> checksum = (104 + 33) % 103 = 34
    var e = Barcodes.Encode(8, Encoding.ASCII.GetBytes("{BA"))!;
    Eq(11 * 3 + 13, e.Modules.Length);   // start, 'A', checksum, stop
});

Test("ITF and Codabar encode", () =>
{
    True(Barcodes.Encode(5, Encoding.ASCII.GetBytes("12345678"))!.Modules.Length > 0);
    True(Barcodes.Encode(6, Encoding.ASCII.GetBytes("A1234B"))!.Modules.Length > 0);
});

Test("PNG encoder writes a valid signature and IHDR", () =>
{
    var png = PngEncoder.EncodeMono(8, 2, new byte[] { 0xFF, 0x00 });
    Eq(0x89, (int)png[0]);
    Eq("PNG", Encoding.ASCII.GetString(png, 1, 3));
    Eq("IHDR", Encoding.ASCII.GetString(png, 12, 4));
});

// ---- HTML ------------------------------------------------------------------------------------

Test("HTML output contains escaped text, one paper per receipt, and image/QR/barcode", () =>
{
    var data = Bytes("<b>&\n", GS, 'V', 0, "Second\n",
        GS, 'v', '0', 0, 1, 0, 1, 0, 0xFF,
        GS, '(', 'k', 8, 0, 49, 80, 48, "HELLO", GS, '(', 'k', 3, 0, 49, 81, 48,
        GS, 'k', 2, "400638133393", 0);
    var html = HtmlRenderer.Render(Parse(data));
    True(html.Contains("&lt;b&gt;&amp;"));
    Eq(2, System.Text.RegularExpressions.Regex.Matches(html, "class=\"paper\"").Count);
    True(html.Contains("data:image/png;base64,"));
    True(html.Contains("<svg"));
});

Test("HTML for empty job says nothing to print", () => True(HtmlRenderer.Render(Parse(Array.Empty<byte>())).Contains("Nothing to print")));

// ---- status replies --------------------------------------------------------------------------

byte[] Reply(PrinterState state, params byte[][] chunks)
{
    var responder = new StatusResponder();
    var all = new List<byte>();
    foreach (var c in chunks) all.AddRange(responder.Feed(c, state));
    return all.ToArray();
}

Test("DLE EOT 1..4 while online report a healthy printer", () =>
{
    var r = Reply(PrinterState.Online, Bytes(0x10, 0x04, 1, 0x10, 0x04, 2, 0x10, 0x04, 3, 0x10, 0x04, 4));
    Eq(4, r.Length);
    True(r.All(b => b == 0x12));
});

Test("DLE EOT 1 reports offline for every non-online state", () =>
{
    foreach (var s in new[] { PrinterState.PaperOut, PrinterState.CoverOpen, PrinterState.Offline })
        Eq((byte)0x1E, Reply(s, Bytes(0x10, 0x04, 1))[0]);
});

Test("paper out and cover open are visible in DLE EOT 2 / 4", () =>
{
    Eq((byte)0x72, Reply(PrinterState.PaperOut, Bytes(0x10, 0x04, 4))[0]);
    True((Reply(PrinterState.PaperOut, Bytes(0x10, 0x04, 2))[0] & 0x20) != 0);
    True((Reply(PrinterState.CoverOpen, Bytes(0x10, 0x04, 2))[0] & 0x04) != 0);
});

Test("GS r 1 paper sensor: 0x00 normally, paper-end bits when out of paper", () =>
{
    Eq((byte)0x00, Reply(PrinterState.Online, Bytes(GS, 'r', 1))[0]);
    Eq((byte)0x0C, Reply(PrinterState.PaperOut, Bytes(GS, 'r', 1))[0]);
    Eq(1, Reply(PrinterState.Online, Bytes(GS, 'r', 49)).Length);
});

Test("GS I printer id and GS a ASB replies", () =>
{
    Eq(1, Reply(PrinterState.Online, Bytes(GS, 'I', 1)).Length);
    var name = Reply(PrinterState.Online, Bytes(GS, 'I', 67));
    Eq((byte)0x5F, name[0]);
    Eq((byte)0, name[^1]);
    var asb = Reply(PrinterState.CoverOpen, Bytes(GS, 'a', 0xFF));
    Eq(4, asb.Length);
    True((asb[0] & 0x20) != 0);
    Eq(0, Reply(PrinterState.Online, Bytes(GS, 'a', 0)).Length);
});

Test("queries split across chunks are answered once", () =>
{
    var r = Reply(PrinterState.Online, Bytes(0x10), Bytes(0x04), Bytes(1));
    Eq(1, r.Length);
    var r2 = Reply(PrinterState.Online, Bytes("text", GS), Bytes('r'), Bytes(1));
    Eq(1, r2.Length);
});

Test("query bytes inside image data or text arguments are not answered", () =>
{
    // 4x1 raster whose data contains DLE EOT 1 and GS r 1
    var r = Reply(PrinterState.Online, Bytes(GS, 'v', '0', 0, 4, 0, 1, 0, 0x10, 0x04, 0x01, GS));
    Eq(0, r.Length);
    // ESC t 0x10 (code page argument) then a real query
    var r2 = Reply(PrinterState.Online, Bytes(ESC, 't', 0x10, 0x10, 0x04, 1));
    Eq(1, r2.Length);
    // barcode data containing GS r 1 after GS k
    var r3 = Reply(PrinterState.Online, Bytes(GS, 'k', 4, GS, 'r', 1, 0, "text\n"));
    Eq(0, r3.Length);
});

Test("a query embedded in a print job is answered without disturbing parsing", () =>
{
    var job = Bytes("Order 1\n", 0x10, 0x04, 1, "Order 2\n");
    Eq(1, Reply(PrinterState.Online, job).Length);
    Eq(2, Lines(Parse(job)).Count);
});

// ---- runner ----------------------------------------------------------------------------------

int failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"  PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {ex.Message}"); }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;
