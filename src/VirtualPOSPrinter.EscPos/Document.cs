namespace VirtualPOSPrinter.EscPos;

public enum Align { Left, Center, Right }

public enum QrEcc { L = 0, M = 1, Q = 2, H = 3 }

/// <summary>A run of text sharing one style. Cell size is 12x24 dots (font A) or 9x17 dots (font B), times the multipliers.</summary>
public sealed record Run(string Text, bool Bold, int Underline, bool Invert, bool FontB, int WidthMul, int HeightMul)
{
    public int CharWidth => FontB ? 9 : 12;
    public int CharHeight => FontB ? 17 : 24;
}

public abstract record Block;

/// <summary>One printed text line (possibly empty). HeightDots is the paper advance for the line.</summary>
public sealed record TextLine(Align Align, IReadOnlyList<Run> Runs, int HeightDots) : Block;

/// <summary>Monochrome bitmap, packed 8 pixels per byte, MSB first, 1 = black. Rows are (Width+7)/8 bytes.</summary>
public sealed record ImageBlock(Align Align, int Width, int Height, byte[] Bits, int ScaleX, int ScaleY) : Block;

public sealed record QrBlock(Align Align, byte[] Data, int ModuleSize, QrEcc Ecc) : Block;

/// <summary>Barcode as sent with GS k. Kind: 0 UPC-A, 1 UPC-E, 2 EAN13, 3 EAN8, 4 Code39, 5 ITF, 6 Codabar, 7 Code93, 8 Code128.</summary>
public sealed record BarcodeBlock(Align Align, int Kind, byte[] Data, int HeightDots, int ModuleWidth, int HriPosition, bool HriFontB) : Block;

public sealed record FeedBlock(int Dots) : Block;

/// <summary>Everything printed between two cuts.</summary>
public sealed record Receipt(IReadOnlyList<Block> Blocks);

public sealed record ParseResult(IReadOnlyList<Receipt> Receipts, IReadOnlyList<string> Warnings);

public sealed class ParserOptions
{
    /// <summary>58 or 80.</summary>
    public int PaperWidthMm { get; set; } = 80;

    /// <summary>Force this Windows code page number for all text (e.g. 737) instead of following ESC t.</summary>
    public int? CodePageOverride { get; set; }

    public int PaperWidthDots => PaperWidthMm == 58 ? 384 : 576;
}
