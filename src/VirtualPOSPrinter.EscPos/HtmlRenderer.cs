using System.Net;
using System.Text;

namespace VirtualPOSPrinter.EscPos;

public sealed class RenderOptions
{
    public int PaperWidthMm { get; set; } = 80;
    public string? Title { get; set; }
    public int PaperWidthDots => PaperWidthMm == 58 ? 384 : 576;
}

/// <summary>
/// Renders a parsed job as a self-contained HTML page. One printer dot is one CSS pixel, so an 80 mm
/// receipt is 576 px wide with a 12x24 px character cell, like the real printer.
/// </summary>
public static class HtmlRenderer
{
    public static string Render(ParseResult result, RenderOptions? options = null)
    {
        options ??= new RenderOptions();
        int dots = options.PaperWidthDots;
        var sb = new StringBuilder(16 * 1024);

        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>")
          .Append(WebUtility.HtmlEncode(options.Title ?? "Receipt")).Append("</title><style>").Append(Css(dots)).Append("</style></head><body>");

        sb.Append("<div class=\"info\">")
          .Append(result.Receipts.Count).Append(result.Receipts.Count == 1 ? " receipt" : " receipts")
          .Append(" &middot; ").Append(options.PaperWidthMm).Append(" mm paper");
        if (result.Warnings.Count > 0)
        {
            sb.Append(" &middot; <details><summary>").Append(result.Warnings.Count).Append(" skipped</summary><ul>");
            foreach (var w in result.Warnings) sb.Append("<li>").Append(WebUtility.HtmlEncode(w)).Append("</li>");
            sb.Append("</ul></details>");
        }
        sb.Append("</div><div class=\"wrap\">");

        if (result.Receipts.Count == 0)
            sb.Append("<div class=\"empty\">Nothing to print in this job.</div>");

        for (int i = 0; i < result.Receipts.Count; i++)
        {
            sb.Append("<div class=\"paper\">");
            foreach (var block in result.Receipts[i].Blocks) RenderBlock(sb, block, dots);
            sb.Append("</div>");
            if (result.Receipts.Count > 1)
                sb.Append("<div class=\"cut\">&#9986; cut &middot; ").Append(i + 1).Append(" of ").Append(result.Receipts.Count).Append("</div>");
            else
                sb.Append("<div class=\"cut\">&#9986; cut</div>");
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string Css(int dots) => $@"
:root {{ color-scheme: light; }}
html, body {{ margin: 0; background: #d8dadd; font-family: 'Segoe UI', sans-serif; }}
.info {{ padding: 6px 14px; font-size: 12px; color: #445; }}
.info details {{ display: inline; }} .info summary {{ display: inline; cursor: pointer; color: #a60; }}
.info ul {{ margin: 4px 0 0 0; padding-left: 20px; }}
.wrap {{ display: flex; flex-direction: column; align-items: center; padding: 6px 14px 30px; }}
.paper {{ width: {dots}px; padding: 14px; background: #fff; color: #111; box-shadow: 0 1px 6px rgba(0,0,0,.35); overflow: hidden; }}
.cut {{ font-size: 11px; color: #778; margin: 6px 0 12px; letter-spacing: .05em; }}
.empty {{ color: #667; padding: 30px; }}
.line {{ display: block; white-space: pre; font-size: 0; line-height: 0; overflow: visible; }}
.r {{ display: inline-block; vertical-align: top; white-space: pre; overflow: visible; text-align: left; color: #000; -webkit-text-stroke: .25px; font-family: 'Courier New', Courier, monospace; font-variant-ligatures: none; }}
.fa {{ font-size: 20px; line-height: 24px; }}
.fb {{ font-size: 15px; line-height: 17px; }}
.s {{ display: inline-block; transform-origin: 0 0; white-space: pre; }}
.b {{ font-weight: bold; }}
.u1 .s, .u1.ns {{ text-decoration: underline; }}
.u2 .s, .u2.ns {{ text-decoration: underline; text-decoration-thickness: 3px; }}
.r.inv {{ background: #000; color: #fff; -webkit-text-stroke-color: #fff; }}
.blk {{ display: block; font-size: 0; line-height: 0; }}
.blk img, .blk svg {{ display: inline-block; vertical-align: top; }}
.bc {{ display: inline-block; text-align: center; max-width: 100%; }}
.hri {{ font-family: 'Courier New', Courier, monospace; font-size: 20px; line-height: 24px; white-space: pre; color: #000; -webkit-text-stroke: .25px; }}
.ph {{ display: inline-block; border: 1px dashed #999; padding: 6px 10px; font: 14px 'Courier New', monospace; color: #666; line-height: 1.2; }}
@media print {{
  html, body {{ background: #fff; }}
  .info, .cut {{ display: none; }}
  .wrap {{ padding: 0; display: block; }}
  .paper {{ box-shadow: none; margin: 0; }}
}}
";

    private static string AlignCss(Align a) => a switch { Align.Center => "center", Align.Right => "right", _ => "left" };

    private static void RenderBlock(StringBuilder sb, Block block, int dots)
    {
        switch (block)
        {
            case TextLine line: RenderLine(sb, line); break;
            case FeedBlock feed: sb.Append("<div style=\"height:").Append(feed.Dots).Append("px\"></div>"); break;
            case ImageBlock img: RenderImage(sb, img); break;
            case QrBlock qr: RenderQr(sb, qr); break;
            case BarcodeBlock bc: RenderBarcode(sb, bc, dots); break;
        }
    }

    private static void RenderLine(StringBuilder sb, TextLine line)
    {
        sb.Append("<div class=\"line\" style=\"text-align:").Append(AlignCss(line.Align)).Append(";height:").Append(line.HeightDots).Append("px\">");
        foreach (var run in line.Runs)
        {
            int cw = run.CharWidth * run.WidthMul;
            int ch = run.CharHeight * run.HeightMul;
            var cls = new StringBuilder("r ").Append(run.FontB ? "fb" : "fa");
            if (run.Bold) cls.Append(" b");
            if (run.Underline > 0) cls.Append(" u").Append(Math.Min(run.Underline, 2));
            if (run.Invert) cls.Append(" inv");

            bool scaled = run.WidthMul > 1 || run.HeightMul > 1;
            if (!scaled) cls.Append(" ns");

            sb.Append("<span class=\"").Append(cls).Append("\" style=\"width:").Append(cw * run.Text.Length).Append("px;height:").Append(ch).Append("px\">");
            if (scaled)
                sb.Append("<span class=\"s\" style=\"transform:scale(").Append(run.WidthMul).Append(',').Append(run.HeightMul).Append(")\">")
                  .Append(WebUtility.HtmlEncode(run.Text)).Append("</span>");
            else
                sb.Append(WebUtility.HtmlEncode(run.Text));
            sb.Append("</span>");
        }
        sb.Append("</div>");
    }

    private static void RenderImage(StringBuilder sb, ImageBlock img)
    {
        var png = PngEncoder.EncodeMono(img.Width, img.Height, img.Bits);
        sb.Append("<div class=\"blk\" style=\"text-align:").Append(AlignCss(img.Align)).Append("\"><img alt=\"\" style=\"width:")
          .Append(img.Width * img.ScaleX).Append("px;height:").Append(img.Height * img.ScaleY)
          .Append("px;image-rendering:pixelated;max-width:100%\" src=\"data:image/png;base64,")
          .Append(Convert.ToBase64String(png)).Append("\"></div>");
    }

    private static void RenderQr(StringBuilder sb, QrBlock qr)
    {
        QrCode code;
        try { code = QrCode.Encode(qr.Data, qr.Ecc); }
        catch (Exception)
        {
            Placeholder(sb, qr.Align, "QR code: data too long");
            return;
        }

        int px = code.Size * qr.ModuleSize;
        var path = new StringBuilder();
        for (int y = 0; y < code.Size; y++)
        {
            int x = 0;
            while (x < code.Size)
            {
                if (!code[y, x]) { x++; continue; }
                int start = x;
                while (x < code.Size && code[y, x]) x++;
                path.Append('M').Append(start).Append(' ').Append(y).Append('h').Append(x - start).Append("v1h-").Append(x - start).Append('z');
            }
        }

        sb.Append("<div class=\"blk\" style=\"text-align:").Append(AlignCss(qr.Align)).Append("\"><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"")
          .Append(px).Append("\" height=\"").Append(px).Append("\" viewBox=\"0 0 ").Append(code.Size).Append(' ').Append(code.Size)
          .Append("\" shape-rendering=\"crispEdges\"><path d=\"").Append(path).Append("\"/></svg></div>");
    }

    private static void RenderBarcode(StringBuilder sb, BarcodeBlock bc, int dots)
    {
        var enc = Barcodes.Encode(bc.Kind, bc.Data);
        if (enc == null)
        {
            Placeholder(sb, bc.Align, $"[{Barcodes.KindName(bc.Kind)}] {System.Text.Encoding.Latin1.GetString(bc.Data)}");
            return;
        }

        int modules = enc.Modules.Length;
        int width = modules * bc.ModuleWidth;
        var path = new StringBuilder();
        for (int x = 0; x < modules;)
        {
            if (!enc.Modules[x]) { x++; continue; }
            int start = x;
            while (x < modules && enc.Modules[x]) x++;
            path.Append('M').Append(start).Append(" 0h").Append(x - start).Append("v1h-").Append(x - start).Append('z');
        }

        const string hriClass = "hri";
        var hri = WebUtility.HtmlEncode(enc.Text);
        sb.Append("<div class=\"blk\" style=\"text-align:").Append(AlignCss(bc.Align)).Append("\"><div class=\"bc\">");
        if (bc.HriPosition is 1 or 3) sb.Append("<div class=\"").Append(hriClass).Append("\">").Append(hri).Append("</div>");
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" style=\"display:block;width:").Append(width).Append("px;max-width:")
          .Append(dots).Append("px;height:").Append(bc.HeightDots).Append("px\" viewBox=\"0 0 ").Append(modules)
          .Append(" 1\" preserveAspectRatio=\"none\" shape-rendering=\"crispEdges\"><path d=\"").Append(path).Append("\"/></svg>");
        if (bc.HriPosition is 2 or 3) sb.Append("<div class=\"").Append(hriClass).Append("\">").Append(hri).Append("</div>");
        sb.Append("</div></div>");
    }

    private static void Placeholder(StringBuilder sb, Align align, string text) =>
        sb.Append("<div class=\"blk\" style=\"text-align:").Append(AlignCss(align)).Append("\"><span class=\"ph\">")
          .Append(WebUtility.HtmlEncode(text)).Append("</span></div>");
}
