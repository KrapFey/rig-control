using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RigControl;

/// <summary>
/// Draws the live tray icon: a rounded tile with a number (a temperature or the airflow), coloured by how hot it is.
/// Drawn at the tray's real pixel size for the current DPI, so it stays sharp at every scale.
/// </summary>
static class TrayArt
{
    static Icon? _last;
    static IntPtr _lastHandle;

    /// <summary>Tile colour: calm accent while cool, amber when warm, red when hot. Airflow always uses the accent.</summary>
    public static Color ColorFor(double value, bool isTemperature) =>
        !isTemperature || value < 60 ? Color.FromArgb(124, 92, 255)
        : value < 80 ? Color.FromArgb(255, 150, 40)
        : Color.FromArgb(235, 50, 70);

    /// <summary>An icon with <paramref name="text"/> (one to three characters) on a tile. Frees the previous one.</summary>
    public static Icon Render(string text, Color tile)
    {
        int size = Math.Max(16, GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem()));
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            float r = size * 0.22f;
            using (var path = RoundedRect(new RectangleF(0, 0, size, size), r))
            using (var fill = new SolidBrush(tile))
                g.FillPath(fill, path);
            // As big as fits: three digits ("100") get a smaller font than two.
            float em = size * (text.Length >= 3 ? 0.50f : 0.70f);
            using var font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, white, new RectangleF(-size * 0.05f, size * 0.02f, size * 1.1f, size), fmt);
        }
        var handle = bmp.GetHicon();
        var icon = Icon.FromHandle(handle);
        // Icon.FromHandle doesn't own the handle: free the previous one ourselves.
        var oldIcon = _last;
        var oldHandle = _lastHandle;
        _last = icon;
        _lastHandle = handle;
        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero) DestroyIcon(oldHandle);
        return icon;
    }

    static GraphicsPath RoundedRect(RectangleF b, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(b.X, b.Y, d, d, 180, 90);
        p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
        p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    const int SM_CXSMICON = 49;
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
}
