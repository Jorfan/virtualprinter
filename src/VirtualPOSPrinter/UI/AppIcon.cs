using System.Drawing.Drawing2D;

namespace VirtualPOSPrinter.UI;

/// <summary>Draws the small printer/receipt icon used for the window and tray (no image assets needed).</summary>
public static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(52, 64, 84));
            g.FillRectangle(body, 2, 12, 28, 14);
            using var paper = new SolidBrush(Color.White);
            g.FillRectangle(paper, 8, 3, 16, 12);
            using var pen = new Pen(Color.FromArgb(120, 130, 150), 1.5f);
            for (int y = 6; y <= 12; y += 3) g.DrawLine(pen, 10, y, 22, y);
            g.FillRectangle(paper, 8, 21, 16, 9);
            using var led = new SolidBrush(Color.FromArgb(60, 200, 120));
            g.FillEllipse(led, 24, 15, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
