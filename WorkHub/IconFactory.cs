using System.Drawing.Drawing2D;

namespace WorkHub;

/// <summary>Builds simple colored-dot tray icons at runtime (no .ico asset needed).</summary>
internal static class IconFactory
{
    public static Icon Create(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 5, 5, 22, 22);
            using var ring = new Pen(Color.FromArgb(230, 255, 255, 255), 2f);
            g.DrawEllipse(ring, 5, 5, 22, 22);
        }

        // Icon.FromHandle does not own the HICON; keep the returned Icon for the app's
        // lifetime (we never destroy the handle), so the two icons leak nothing meaningful.
        return Icon.FromHandle(bmp.GetHicon());
    }
}
