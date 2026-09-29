using System.Text;

namespace VirtualPOSPrinter.EscPos;

/// <summary>Simulated printer condition reported to status queries.</summary>
public enum PrinterState { Online, PaperOut, CoverOpen, Offline }

/// <summary>
/// Watches the incoming byte stream of one connection and produces the replies a real printer would send to
/// status queries: DLE EOT n, GS r n, GS I n, ESC v and GS a n (automatic status). Feed it every received chunk;
/// commands split across chunks are handled. Print data such as raster images is skipped so its bytes
/// are never mistaken for queries.
/// </summary>
public sealed class StatusResponder
{
    private readonly List<byte> _buf = new();
    private int _pos;
    private long _skip;
    private bool _untilNul;

    /// <summary>Number of queries answered so far on this connection.</summary>
    public int QueriesAnswered { get; private set; }

    private int Avail => _buf.Count - _pos;

    /// <summary>Consumes a chunk and returns the reply bytes to send back (possibly empty).</summary>
    public byte[] Feed(ReadOnlySpan<byte> chunk, PrinterState state)
    {
        _buf.AddRange(chunk.ToArray());
        var reply = new List<byte>();

        while (true)
        {
            if (_skip > 0)
            {
                int t = (int)Math.Min(_skip, Avail);
                _pos += t;
                _skip -= t;
                if (_skip > 0) break;
                continue;
            }
            if (_untilNul)
            {
                while (Avail > 0 && _buf[_pos] != 0) _pos++;
                if (Avail == 0) break;
                _pos++;          // the NUL terminator
                _untilNul = false;
                continue;
            }
            if (Avail == 0) break;

            byte b = _buf[_pos];
            if (b == 0x10) { if (!Dle(reply, state)) break; }
            else if (b == 0x1B) { if (!Esc(reply, state)) break; }
            else if (b == 0x1D) { if (!Gs(reply, state)) break; }
            else _pos++;
        }

        _buf.RemoveRange(0, _pos);
        _pos = 0;
        return reply.ToArray();
    }

    // Each handler returns false when more bytes are needed (nothing consumed).

    private bool Dle(List<byte> reply, PrinterState state)
    {
        if (Avail < 2) return false;
        byte c = _buf[_pos + 1];
        if (c != 0x04 && c != 0x05) { _pos++; return true; }
        if (Avail < 3) return false;
        if (c == 0x04)
        {
            var r = DleEot(_buf[_pos + 2], state);
            if (r != null) { reply.Add(r.Value); QueriesAnswered++; }
        }
        _pos += 3;
        return true;
    }

    private static readonly HashSet<byte> EscOneArg = new("E-!atM23dJG{RV= ".Select(c => (byte)c));

    private bool Esc(List<byte> reply, PrinterState state)
    {
        if (Avail < 2) return false;
        byte c = _buf[_pos + 1];
        if (c == '*')
        {
            if (Avail < 5) return false;
            int m = _buf[_pos + 2], n = _buf[_pos + 3] + _buf[_pos + 4] * 256;
            _skip = (long)n * (m >= 32 ? 3 : 1);
            _pos += 5;
        }
        else if (c == 'v')
        {
            reply.Add(PaperSensor(state));
            QueriesAnswered++;
            _pos += 2;
        }
        else if (c == 'p') { if (Avail < 5) return false; _pos += 5; }
        else if (EscOneArg.Contains(c)) { if (Avail < 3) return false; _pos += 3; }
        else _pos += 2;
        return true;
    }

    private bool Gs(List<byte> reply, PrinterState state)
    {
        if (Avail < 2) return false;
        byte c = _buf[_pos + 1];
        switch ((char)c)
        {
            case 'r':
                {
                    if (Avail < 3) return false;
                    var r = GsR(_buf[_pos + 2], state);
                    if (r != null) { reply.Add(r.Value); QueriesAnswered++; }
                    _pos += 3;
                    return true;
                }
            case 'I':
                {
                    if (Avail < 3) return false;
                    var r = GsI(_buf[_pos + 2]);
                    if (r != null) { reply.AddRange(r); QueriesAnswered++; }
                    _pos += 3;
                    return true;
                }
            case 'a':
                {
                    if (Avail < 3) return false;
                    if (_buf[_pos + 2] != 0) { reply.AddRange(Asb(state)); QueriesAnswered++; }
                    _pos += 3;
                    return true;
                }
            case 'v':
                {
                    if (Avail < 3) return false;
                    if (_buf[_pos + 2] != '0') { _pos += 3; return true; }
                    if (Avail < 8) return false;
                    long w = _buf[_pos + 4] + _buf[_pos + 5] * 256, h = _buf[_pos + 6] + _buf[_pos + 7] * 256;
                    _skip = w * h;
                    _pos += 8;
                    return true;
                }
            case '(':
                {
                    if (Avail < 5) return false;
                    _skip = _buf[_pos + 3] + _buf[_pos + 4] * 256;
                    _pos += 5;
                    return true;
                }
            case '8':
                {
                    if (Avail < 7) return false;
                    _skip = (uint)(_buf[_pos + 3] | _buf[_pos + 4] << 8 | _buf[_pos + 5] << 16 | _buf[_pos + 6] << 24);
                    _pos += 7;
                    return true;
                }
            case 'k':
                {
                    if (Avail < 3) return false;
                    byte m = _buf[_pos + 2];
                    if (m >= 65)
                    {
                        if (Avail < 4) return false;
                        _skip = _buf[_pos + 3];
                        _pos += 4;
                    }
                    else { _pos += 3; _untilNul = true; }
                    return true;
                }
            case 'V':
                {
                    if (Avail < 3) return false;
                    int len = _buf[_pos + 2] is 65 or 66 ? 4 : 3;
                    if (Avail < len) return false;
                    _pos += len;
                    return true;
                }
            case '!': case 'B': case 'H': case 'h': case 'w': case 'f':
                if (Avail < 3) return false;
                _pos += 3;
                return true;
            default:
                _pos += 2;
                return true;
        }
    }

    // ---- replies ---------------------------------------------------------------------------

    /// <summary>DLE EOT n real-time status (n = 1 printer, 2 offline cause, 3 error, 4 paper sensor).</summary>
    public static byte? DleEot(byte n, PrinterState s) => n switch
    {
        1 => s == PrinterState.Online ? (byte)0x12 : (byte)0x1E,
        2 => s switch { PrinterState.CoverOpen => (byte)0x16, PrinterState.PaperOut => (byte)0x32, PrinterState.Offline => (byte)0x52, _ => (byte)0x12 },
        3 => s == PrinterState.Offline ? (byte)0x52 : (byte)0x12,
        4 => s == PrinterState.PaperOut ? (byte)0x72 : (byte)0x12,
        _ => null,
    };

    /// <summary>GS r n transmit status (1/49 paper sensor, 2/50 drawer).</summary>
    public static byte? GsR(byte n, PrinterState s) => n switch
    {
        1 or 49 => PaperSensor(s),
        2 or 50 => (byte)0x00,
        _ => null,
    };

    public static byte PaperSensor(PrinterState s) => s == PrinterState.PaperOut ? (byte)0x0C : (byte)0x00;

    /// <summary>GS I n printer ID (single byte for 1-3, header 0x5F + text + NUL for 65-68).</summary>
    public static byte[]? GsI(byte n)
    {
        static byte[] Text(string t) => new[] { (byte)0x5F }.Concat(Encoding.ASCII.GetBytes(t)).Append((byte)0).ToArray();
        return n switch
        {
            1 or 49 => new byte[] { 0x20 },
            2 or 50 => new byte[] { 0x03 },
            3 or 51 => new byte[] { 0x01 },
            65 => Text("1.0"),
            66 => Text("VirtualPOS"),
            67 => Text("VirtualPOSPrinter"),
            68 => Text("0"),
            _ => null,
        };
    }

    /// <summary>Four-byte automatic status block (ASB).</summary>
    public static byte[] Asb(PrinterState s) => new[]
    {
        (byte)(0x10 | (s != PrinterState.Online ? 0x08 : 0) | (s == PrinterState.CoverOpen ? 0x20 : 0)),
        (byte)(s == PrinterState.Offline ? 0x40 : 0x00),
        PaperSensor(s),
        (byte)0x00,
    };
}
