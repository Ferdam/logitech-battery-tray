using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace G703BatteryMonitor;

/// <summary>
/// Generates tray icons dynamically:
///   - Battery bar + percentage number for levels below threshold
///   - A small "hidden" transparent icon for when battery is fine
/// </summary>
public static class TrayIconRenderer
{
    // Icon canvas — 16x16 is the standard small tray size
    private const int Size = 16;

    /// <summary>
    /// Returns a transparent 1x1 icon suitable for "hidden" state.
    /// We still need a valid icon object but it will be invisible.
    /// </summary>
    public static Icon MakeInvisibleIcon()
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, Color.Transparent);
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Draws a battery icon with the percentage number inside.
    /// Color: red ≤10%, orange ≤20%, yellow ≤35%, green otherwise.
    /// </summary>
    public static Icon MakeBatteryIcon(int percent)
    {
        using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Color.Transparent);

        // ── Battery outline ─────────────────────────────────────────────────────
        // Battery body: 14×10 at (1, 3); terminal nub: 2×4 at (14, 5)
        var bodyRect  = new Rectangle(0, 2, 13, 11);
        var termRect  = new Rectangle(13, 5, 2, 5);
        var fillRect  = new Rectangle(1, 3,
            Math.Max(1, (int)(11 * percent / 100.0)),
            9);

        var fillColor = percent <= 10 ? Color.FromArgb(220, 50,  50)   // red
                      : percent <= 20 ? Color.FromArgb(230, 100, 30)   // orange
                      : percent <= 35 ? Color.FromArgb(220, 200, 40)   // yellow
                                      : Color.FromArgb(60,  180, 75);  // green

        // Draw terminal nub
        using (var termBrush = new SolidBrush(Color.FromArgb(180, 180, 180)))
            g.FillRectangle(termBrush, termRect);

        // Draw battery fill
        using (var fillBrush = new SolidBrush(fillColor))
            g.FillRectangle(fillBrush, fillRect);

        // Draw battery outline
        using (var outlinePen = new Pen(Color.FromArgb(200, 200, 200), 1))
            g.DrawRectangle(outlinePen, bodyRect);

        // ── Percentage text ─────────────────────────────────────────────────────
        // Render 2-digit number inside the battery body.
        // At 16px we need a tiny font — 6pt bold fits nicely.
        string label = percent >= 100 ? "F" : percent.ToString(); // "F" for full at 100
        float fontSize = label.Length >= 2 ? 5.5f : 6.5f;

        using var font = new Font("Arial", fontSize, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Color.White);

        var textSize = g.MeasureString(label, font);
        float tx = (Size / 2f) - (textSize.Width / 2f) - 1;
        float ty = (Size / 2f) - (textSize.Height / 2f) + 0.5f;

        // Dark shadow for readability on any taskbar color
        using var shadowBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0));
        g.DrawString(label, font, shadowBrush, tx + 1, ty + 1);
        g.DrawString(label, font, textBrush, tx, ty);

        return Icon.FromHandle(bmp.GetHicon());
    }
}
