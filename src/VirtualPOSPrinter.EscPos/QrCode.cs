namespace VirtualPOSPrinter.EscPos;

/// <summary>
/// Minimal QR Code encoder (byte mode, versions 1-40, all error correction levels, automatic mask selection).
/// Follows ISO/IEC 18004; structured after the well-known public-domain-style reference layouts.
/// </summary>
public sealed class QrCode
{
    private static readonly int[][] EccCodewordsPerBlock =
    {
        new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly int[][] NumErrorCorrectionBlocks =
    {
        new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    private static readonly int[] FormatBits = { 1, 0, 3, 2 };   // L, M, Q, H

    private readonly bool[,] _modules;     // [row, col], true = dark
    private readonly bool[,] _isFunction;

    public int Version { get; }
    public int Size { get; }
    public QrEcc Ecc { get; }

    public bool this[int row, int col] => _modules[row, col];

    private QrCode(int version, QrEcc ecc, byte[] dataCodewords)
    {
        Version = version;
        Ecc = ecc;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];

        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(dataCodewords));

        int bestMask = 0, bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            int penalty = Penalty();
            if (penalty < bestPenalty) { bestMask = mask; bestPenalty = penalty; }
            ApplyMask(mask);   // undo (XOR)
        }
        ApplyMask(bestMask);
        DrawFormatBits(bestMask);
    }

    /// <summary>Encodes bytes in byte mode with the smallest version that fits. Throws if the data is too long.</summary>
    public static QrCode Encode(byte[] data, QrEcc ecc)
    {
        int version = 1;
        for (; ; version++)
        {
            if (version > 40) throw new ArgumentException("Data too long for a QR code");
            int countBits = version <= 9 ? 8 : 16;
            long used = 4 + countBits + 8L * data.Length;
            if (used <= 8L * DataCodewords(version, ecc)) break;
        }

        var bits = new List<bool>();
        void Append(int value, int len) { for (int i = len - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }

        Append(0x4, 4);
        Append(data.Length, version <= 9 ? 8 : 16);
        foreach (var b in data) Append(b, 8);

        int capacityBits = DataCodewords(version, ecc) * 8;
        Append(0, Math.Min(4, capacityBits - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);
        for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) Append(pad, 8);

        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) codewords[i >> 3] |= (byte)(0x80 >> (i & 7));

        return new QrCode(version, ecc, codewords);
    }

    // ---- capacity --------------------------------------------------------------------------

    private static int RawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int ver, QrEcc ecc) =>
        RawDataModules(ver) / 8 - EccCodewordsPerBlock[(int)ecc][ver] * NumErrorCorrectionBlocks[(int)ecc][ver];

    // ---- function patterns -----------------------------------------------------------------

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var pos = AlignmentPositions();
        int n = pos.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)))
                    DrawAlignment(pos[i], pos[j]);

        DrawFormatBits(0);   // reserve the area
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size)
                    SetFunction(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1) return Array.Empty<int>();
        int numAlign = Version / 7 + 2;
        int step = Version == 32 ? 26 : (Version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private void DrawFormatBits(int mask)
    {
        int data = FormatBits[(int)Ecc] << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;

        bool Bit(int i) => ((bits >> i) & 1) != 0;
        for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(i));
        SetFunction(8, 7, Bit(6));
        SetFunction(8, 8, Bit(7));
        SetFunction(7, 8, Bit(8));
        for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(i));

        for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(i));
        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        int rem = Version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        int bits = Version << 12 | rem;
        for (int i = 0; i < 18; i++)
        {
            bool dark = ((bits >> i) & 1) != 0;
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, dark);
            SetFunction(b, a, dark);
        }
    }

    // ---- error correction ------------------------------------------------------------------

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int ecc = (int)Ecc;
        int numBlocks = NumErrorCorrectionBlocks[ecc][Version];
        int blockEccLen = EccCodewordsPerBlock[ecc][Version];
        int rawCodewords = RawDataModules(Version) / 8;
        int numShortBlocks = numBlocks - rawCodewords % numBlocks;
        int shortBlockLen = rawCodewords / numBlocks;

        var divisor = ReedSolomonDivisor(blockEccLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = new byte[datLen];
            Array.Copy(data, k, dat, 0, datLen);
            k += datLen;

            var block = new byte[shortBlockLen + 1];
            Array.Copy(dat, block, datLen);
            var rs = ReedSolomonRemainder(dat, divisor);
            Array.Copy(rs, 0, block, block.Length - blockEccLen, blockEccLen);
            blocks.Add(block);
        }

        var result = new byte[rawCodewords];
        for (int i = 0, k = 0; i < blocks[0].Length; i++)
            for (int j = 0; j < blocks.Count; j++)
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    result[k++] = blocks[j][i];
        return result;
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }

    private static int Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    // ---- placement, mask, penalty ----------------------------------------------------------

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < Size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (!_isFunction[y, x] && invert) _modules[y, x] = !_modules[y, x];
            }
    }

    private int Penalty()
    {
        int result = 0;
        int dark = 0;

        for (int a = 0; a < Size; a++)
            for (int dir = 0; dir < 2; dir++)
            {
                bool Get(int i) => dir == 0 ? _modules[a, i] : _modules[i, a];

                // N1: runs of 5+ same-colour modules; N3: finder-like patterns
                int run = 1;
                for (int i = 1; i < Size; i++)
                {
                    if (Get(i) == Get(i - 1)) { run++; if (run == 5) result += 3; else if (run > 5) result++; }
                    else run = 1;
                }
                for (int i = 0; i + 11 <= Size; i++)
                {
                    bool p1 = Get(i) && !Get(i + 1) && Get(i + 2) && Get(i + 3) && Get(i + 4) && !Get(i + 5) && Get(i + 6);
                    if (!p1) continue;
                    bool lightBefore = i >= 4 && !Get(i - 1) && !Get(i - 2) && !Get(i - 3) && !Get(i - 4);
                    bool lightAfter = i + 10 < Size && !Get(i + 7) && !Get(i + 8) && !Get(i + 9) && !Get(i + 10);
                    if (lightBefore) result += 40;
                    if (lightAfter) result += 40;
                }
            }

        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (_modules[y, x]) dark++;
                // N2: 2x2 blocks
                if (x + 1 < Size && y + 1 < Size)
                {
                    var c = _modules[y, x];
                    if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) result += 3;
                }
            }

        // N4: dark/light balance
        int total = Size * Size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += Math.Max(k, 0) * 10;
        return result;
    }
}
