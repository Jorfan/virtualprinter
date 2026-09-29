namespace VirtualPOSPrinter.EscPos;

/// <summary>
/// Parses an ESC/POS byte stream into a receipt document model (one <see cref="Receipt"/> per cut).
/// Unknown or truncated commands are skipped and reported in <see cref="ParseResult.Warnings"/>; parsing never throws.
/// </summary>
public sealed class EscPosParser
{
    private const byte DLE = 0x10, ESC = 0x1B, FS = 0x1C, GS = 0x1D;
    private const int DefaultSpacing = 30;
    private const int MaxWarnings = 50;

    private readonly byte[] _d;
    private int _p;
    private readonly ParserOptions _o;
    private readonly int _paperDots;

    private readonly List<Receipt> _receipts = new();
    private List<Block> _blocks = new();
    private readonly List<string> _warnings = new();
    private readonly List<byte> _pending = new();

    // Current (unfinished) line
    private readonly List<Run> _line = new();
    private int _lineWidth;
    private int _lineHeight;
    private Align _lineAlign;
    private bool _swallowLf;   // an ESC * image line is followed by an LF that must not add a blank line

    // Print state (reset by ESC @)
    private Align _align;
    private bool _bold, _invert, _fontB;
    private int _underline, _wMul = 1, _hMul = 1, _spacing = DefaultSpacing, _escT;

    // Barcode / QR state
    private int _bcHeight = 162, _bcWidth = 3, _bcHri;
    private bool _bcHriFontB;
    private byte[] _qrData = Array.Empty<byte>();
    private int _qrModule = 3;
    private QrEcc _qrEcc = QrEcc.L;

    private EscPosParser(byte[] data, ParserOptions options)
    {
        _d = data;
        _o = options;
        _paperDots = options.PaperWidthDots;
    }

    public static ParseResult Parse(byte[] data, ParserOptions? options = null)
    {
        var parser = new EscPosParser(data ?? Array.Empty<byte>(), options ?? new ParserOptions());
        try { parser.Run(); }
        catch (Exception ex) { parser.Warn("Parser stopped: " + ex.Message); }   // defensive: never break the preview
        return parser.Finish();
    }

    // ---- main loop -------------------------------------------------------------------------

    private void Run()
    {
        while (_p < _d.Length)
        {
            byte b = _d[_p++];
            switch (b)
            {
                case 0x0A: LineFeed(); break;
                case 0x0D: case 0x00: break;
                case 0x09: Tab(); break;
                case 0x0C: FlushText(); if (_line.Count > 0) NewLine(); break;
                case ESC: FlushText(); HandleEsc(); break;
                case GS: FlushText(); HandleGs(); break;
                case DLE: FlushText(); HandleDle(); break;
                case FS: FlushText(); HandleFs(); break;
                default:
                    if (b >= 0x20) { _pending.Add(b); _swallowLf = false; }
                    break;
            }
        }
        FlushText();
        if (_line.Count > 0) NewLine();
    }

    private ParseResult Finish()
    {
        EndReceipt();
        return new ParseResult(_receipts, _warnings);
    }

    private void Warn(string msg)
    {
        if (_warnings.Count < MaxWarnings) _warnings.Add(msg);
        else if (_warnings.Count == MaxWarnings) _warnings.Add("(further warnings suppressed)");
    }

    // ---- byte access -----------------------------------------------------------------------

    /// <summary>Next byte, or -1 at end of data.</summary>
    private int Next() => _p < _d.Length ? _d[_p++] : -1;

    private void Skip(int n) => _p = (int)Math.Min((long)_p + Math.Max(n, 0), _d.Length);

    private byte[] Take(int n)
    {
        n = Math.Clamp(n, 0, _d.Length - _p);
        var r = new byte[n];
        Array.Copy(_d, _p, r, 0, n);
        _p += n;
        return r;
    }

    // ---- text & lines ----------------------------------------------------------------------

    private int FontHeight(bool fontB) => fontB ? 17 : 24;

    private void LineFeed()
    {
        FlushText();
        if (_swallowLf && _line.Count == 0) { _swallowLf = false; return; }
        _swallowLf = false;
        NewLine();
    }

    private void Tab()
    {
        int cw = (_fontB ? 9 : 12) * _wMul;
        int col = _lineWidth / cw + _pending.Count;
        int n = 8 - col % 8;
        for (int i = 0; i < n; i++) _pending.Add(0x20);
    }

    private int CodePage => _o.CodePageOverride ?? CodePages.FromEscT(_escT) ?? 437;

    private void FlushText()
    {
        if (_pending.Count == 0) return;
        var text = CodePages.Get(CodePage).GetString(_pending.ToArray());
        _pending.Clear();

        int cw = (_fontB ? 9 : 12) * _wMul;
        while (text.Length > 0)
        {
            int avail = (_paperDots - _lineWidth) / cw;
            if (avail <= 0)
            {
                if (_line.Count > 0) { NewLine(); continue; }
                avail = 1;
            }
            int take = Math.Min(avail, text.Length);
            AddRun(text[..take], cw);
            text = text[take..];
            if (text.Length > 0) NewLine();
        }
    }

    private void AddRun(string text, int cellWidth)
    {
        if (_line.Count == 0) { _lineAlign = _align; _lineHeight = 0; }
        _line.Add(new Run(text, _bold, _underline, _invert, _fontB, _wMul, _hMul));
        _lineWidth += cellWidth * text.Length;
        _lineHeight = Math.Max(_lineHeight, _spacing + (_hMul - 1) * FontHeight(_fontB));
    }

    private void NewLine()
    {
        if (_line.Count == 0)
            _blocks.Add(new TextLine(_align, Array.Empty<Run>(), _spacing + (_hMul - 1) * FontHeight(_fontB)));
        else
            _blocks.Add(new TextLine(_lineAlign, _line.ToArray(), _lineHeight));
        _line.Clear();
        _lineWidth = 0;
        _lineHeight = 0;
    }

    /// <summary>Finish any buffered text before adding a non-text block.</summary>
    private void BeginBlock()
    {
        FlushText();
        if (_line.Count > 0) NewLine();
        _swallowLf = false;
    }

    private void Feed(int dots)
    {
        BeginBlock();
        if (dots > 0) _blocks.Add(new FeedBlock(dots));
    }

    private void Cut()
    {
        BeginBlock();
        EndReceipt();
    }

    private void EndReceipt()
    {
        bool hasContent = _blocks.Any(b => b is not FeedBlock && !(b is TextLine t && t.Runs.Count == 0));
        if (hasContent) _receipts.Add(new Receipt(_blocks));
        _blocks = new List<Block>();
    }

    private void ResetState()
    {
        _align = Align.Left;
        _bold = _invert = _fontB = false;
        _underline = 0;
        _wMul = _hMul = 1;
        _spacing = DefaultSpacing;
        _escT = 0;
    }

    // ---- ESC -------------------------------------------------------------------------------

    private void HandleEsc()
    {
        int at = _p - 1;
        int c = Next();
        if (c < 0) return;
        int n;
        switch (c)
        {
            case '@': ResetState(); break;
            case 'E': n = Next(); if (n >= 0) _bold = (n & 1) != 0; break;
            case 'G': n = Next(); if (n >= 0) _bold = (n & 1) != 0; break;          // double-strike shown as bold
            case '-': n = Next(); if (n >= 0) _underline = n & 3; break;
            case '!':
                n = Next();
                if (n >= 0)
                {
                    _fontB = (n & 0x01) != 0;
                    _bold = (n & 0x08) != 0;
                    _hMul = (n & 0x10) != 0 ? 2 : 1;
                    _wMul = (n & 0x20) != 0 ? 2 : 1;
                    _underline = (n & 0x80) != 0 ? 1 : 0;
                }
                break;
            case 'a': n = Next(); if (n >= 0) _align = (n & 3) switch { 1 => Align.Center, 2 => Align.Right, _ => Align.Left }; break;
            case 't': n = Next(); if (n >= 0) { _escT = n; if (CodePages.FromEscT(n) == null && _o.CodePageOverride == null) Warn($"ESC t {n}: unknown code page, using CP437"); } break;
            case 'M': n = Next(); if (n >= 0) _fontB = (n & 1) != 0; break;
            case '2': _spacing = DefaultSpacing; break;
            case '3': n = Next(); if (n >= 0) _spacing = n; break;
            case 'd': n = Next(); if (n >= 0) Feed(n * _spacing); break;
            case 'J': n = Next(); if (n >= 0) Feed(n); break;
            case 'i': case 'm': Cut(); break;                                        // ESC i / ESC m: cut
            case '*': BitImage(); break;
            case '&': DefineUserChars(); break;
            case 'D': while (_p < _d.Length && _d[_p++] != 0) { } break;              // tab stops (default 8 kept)
            case 'p': Skip(3); break;                                                // cash drawer pulse
            case 'c': Skip(2); break;                                                // panel buttons etc.
            case 'W': Skip(8); break;
            case '$': case '\\': Skip(2); break;
            case '=': case 'R': case 'V': case '{': case ' ': case 'U': case '%':
            case '?': case 'T': case 'u': case 'v': case '4': case '5':
                if (c is 'v') break;                                                 // ESC v has no argument in most printers
                Skip(1); break;
            case 'L': case 'S': case '<': break;                                     // mode switches without visible effect
            default: Warn($"Unknown ESC 0x{c:X2} at offset {at}"); break;
        }
    }

    /// <summary>ESC * m nL nH d...: column-oriented bit image, printed as one line.</summary>
    private void BitImage()
    {
        int m = Next(), nl = Next(), nh = Next();
        if (m < 0 || nl < 0 || nh < 0) return;
        int width = nl + nh * 256;
        bool tall = m == 32 || m == 33;
        int bytesPerCol = tall ? 3 : 1;
        int cols = Math.Min(width, (_d.Length - _p) / bytesPerCol);
        var data = Take(cols * bytesPerCol);
        if (cols < width) Warn("ESC * image truncated");
        if (cols == 0) return;

        int height = bytesPerCol * 8;
        int rowBytes = (cols + 7) / 8;
        var bits = new byte[rowBytes * height];
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < height; y++)
                if ((data[x * bytesPerCol + y / 8] & (0x80 >> (y % 8))) != 0)
                    bits[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));

        BeginBlock();
        _blocks.Add(new ImageBlock(_align, cols, height, bits, 1, 1));
        _swallowLf = true;
    }

    private void DefineUserChars()
    {
        int y = Next(), c1 = Next(), c2 = Next();
        if (y < 0 || c1 < 0 || c2 < 0) return;
        for (int c = c1; c <= c2 && _p < _d.Length; c++)
        {
            int x = Next();
            if (x < 0) return;
            Skip(x * y);
        }
        Warn("User-defined characters are not rendered");
    }

    // ---- GS --------------------------------------------------------------------------------

    private void HandleGs()
    {
        int at = _p - 1;
        int c = Next();
        if (c < 0) return;
        int n;
        switch (c)
        {
            case '!':
                n = Next();
                if (n >= 0) { _wMul = ((n >> 4) & 7) + 1; _hMul = (n & 7) + 1; }
                break;
            case 'B': n = Next(); if (n >= 0) _invert = (n & 1) != 0; break;
            case 'V':
                n = Next();
                if (n == 65 || n == 66) Skip(1);
                if (n >= 0) Cut();
                break;
            case 'v': RasterImage(); break;
            case 'k': Barcode(); break;
            case '(': FunctionBlock(); break;
            case '8': FunctionBlock8(); break;
            case 'H': n = Next(); if (n >= 0) _bcHri = n & 3; break;
            case 'h': n = Next(); if (n > 0) _bcHeight = n; break;
            case 'w': n = Next(); if (n > 0) _bcWidth = Math.Clamp(n, 1, 6); break;
            case 'f': n = Next(); if (n >= 0) _bcHriFontB = (n & 1) != 0; break;
            case 'a': case 'r': case 'I': case '/': case 'C': case 'D': Skip(1); break;   // status, info, misc: 1 argument
            case 'P': case 'L': case 'W': Skip(2); break;
            case '*': { int x = Next(), y = Next(); if (x >= 0 && y >= 0) Skip(x * y * 8); break; }
            default: Warn($"Unknown GS 0x{c:X2} at offset {at}"); break;
        }
    }

    /// <summary>GS v 0 m xL xH yL yH d...: raster bit image.</summary>
    private void RasterImage()
    {
        int sub = Next();
        if (sub != '0' && sub != 0x30) { Warn("Unsupported GS v sub-command"); return; }
        int m = Next(), xl = Next(), xh = Next(), yl = Next(), yh = Next();
        if (m < 0 || xl < 0 || xh < 0 || yl < 0 || yh < 0) return;
        int wBytes = xl + xh * 256, h = yl + yh * 256;
        if (wBytes == 0 || h == 0) return;

        long need = (long)wBytes * h;
        int avail = (int)Math.Min(need, _d.Length - _p);
        int rows = avail / wBytes;
        if (rows < h) Warn("Raster image truncated");
        var data = Take(rows * wBytes);
        Skip(avail - rows * wBytes);
        if (rows == 0) return;

        BeginBlock();
        _blocks.Add(new ImageBlock(_align, wBytes * 8, rows, data, (m & 1) != 0 ? 2 : 1, (m & 2) != 0 ? 2 : 1));
    }

    /// <summary>GS k: barcode, in "terminated by NUL" (m 0-6) or "length prefixed" (m 65-73) form.</summary>
    private void Barcode()
    {
        int m = Next();
        if (m < 0) return;
        byte[] data;
        int kind;
        if (m <= 6)
        {
            int start = _p;
            while (_p < _d.Length && _d[_p] != 0 && _p - start < 255) _p++;
            data = _d[start.._p];
            if (_p < _d.Length && _d[_p] == 0) _p++;
            kind = m;
        }
        else if (m >= 65 && m <= 73)
        {
            int len = Next();
            if (len < 0) return;
            data = Take(len);
            kind = m - 65;
        }
        else { Warn($"Unknown barcode type {m}"); return; }

        BeginBlock();
        _blocks.Add(new BarcodeBlock(_align, kind, data, _bcHeight, _bcWidth, _bcHri, _bcHriFontB));
    }

    /// <summary>GS ( x pL pH ...: function blocks. Only QR (GS ( k, cn 49) is rendered; the rest are skipped by length.</summary>
    private void FunctionBlock()
    {
        int sub = Next(), pl = Next(), ph = Next();
        if (sub < 0 || pl < 0 || ph < 0) return;
        var body = Take(pl + ph * 256);
        if (sub != 'k') return;                                   // GS ( L, GS ( A, ... : not rendered
        if (body.Length < 2) return;

        int cn = body[0], fn = body[1];
        if (cn != 49) { Warn($"Unsupported 2D symbol (cn {cn})"); return; }
        switch (fn)
        {
            case 67: if (body.Length >= 3) _qrModule = Math.Clamp((int)body[2], 1, 16); break;
            case 69: if (body.Length >= 3) _qrEcc = (QrEcc)Math.Clamp(body[2] - 48, 0, 3); break;
            case 80: _qrData = body.Length > 3 ? body[3..] : Array.Empty<byte>(); break;
            case 81:
                if (_qrData.Length > 0)
                {
                    BeginBlock();
                    _blocks.Add(new QrBlock(_align, _qrData, _qrModule, _qrEcc));
                }
                break;
        }
    }

    /// <summary>GS 8 L p1 p2 p3 p4 ...: graphics with 32-bit length. Skipped.</summary>
    private void FunctionBlock8()
    {
        int sub = Next();
        if (sub < 0) return;
        int p1 = Next(), p2 = Next(), p3 = Next(), p4 = Next();
        if (p4 < 0) return;
        Skip(p1 | p2 << 8 | p3 << 16 | p4 << 24);
    }

    // ---- DLE / FS --------------------------------------------------------------------------

    private void HandleDle()
    {
        int c = Next();
        switch (c)
        {
            case 0x04: case 0x05: Skip(1); break;     // DLE EOT n, DLE ENQ n: real-time status / request
            case 0x14: Skip(3); break;                // DLE DC4 fn m t
            case -1: break;
            default: Warn($"Unknown DLE 0x{c:X2}"); break;
        }
    }

    private void HandleFs()
    {
        int c = Next();
        switch (c)
        {
            case '!': case 'C': case '-': case 'W': Skip(1); break;
            case 'S': case 'p': Skip(2); break;
            case 'q':
                {
                    int count = Next();
                    for (int i = 0; i < count && _p < _d.Length; i++)
                    {
                        int xl = Next(), xh = Next(), yl = Next(), yh = Next();
                        if (yh < 0) return;
                        Skip((xl + xh * 256) * (yl + yh * 256) * 8);
                    }
                    break;
                }
            case '&': case '.': case -1: break;
            default: Warn($"Unknown FS 0x{c:X2}"); break;
        }
    }
}
