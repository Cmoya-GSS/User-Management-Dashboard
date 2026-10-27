using System;
using System.Drawing;
using System.Windows.Forms;

namespace UserDashboard;

/// <summary>
/// High-DPI helpers for WinForms. Used with Per-Monitor V2 awareness in
/// app.manifest so the dashboard scales correctly on 4K displays, fractional
/// scaling, and mixed-DPI multi-monitor setups.
/// </summary>
internal static class DpiHelper
{
    /// <summary>Design-time baseline DPI (standard 96 DPI / 100% scaling).</summary>
    public const float DesignDpi = 96f;

    /// <summary>
    /// Apply recommended auto-scaling to a form. Call at the start of every
    /// Form constructor, before setting Size/MinimumSize.
    /// </summary>
    public static void ConfigureForm(Form form)
    {
        form.AutoScaleMode       = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = new SizeF(DesignDpi, DesignDpi);
    }

    /// <summary>Primary monitor working area, or a 1920×1080 fallback.</summary>
    public static Rectangle PrimaryWorkingArea =>
        Screen.PrimaryScreen?.WorkingArea
        ?? new Rectangle(0, 0, 1920, 1080);

    /// <summary>
    /// Default main-window size: up to 1024×700 but never larger than the
    /// current working area minus margins.
    /// </summary>
    public static Size DefaultMainFormSize()
    {
        var work = PrimaryWorkingArea;
        int w = Math.Min(1024, work.Width - 40);
        int h = Math.Min(700,  work.Height - 80);
        return new Size(Math.Max(w, 640), Math.Max(h, 480));
    }

    /// <summary>
    /// Minimum main-window size: never more than ~90% of the working area so
    /// the window can always fit on high-DPI laptops (e.g. 2880×1800 @ 200%).
    /// </summary>
    public static Size MainFormMinimumSize()
    {
        var work = PrimaryWorkingArea;
        int maxW = Math.Max(640, (int)(work.Width  * 0.90));
        int maxH = Math.Max(400, (int)(work.Height * 0.85));
        return new Size(Math.Min(1024, maxW), Math.Min(500, maxH));
    }

    /// <summary>
    /// Clamp saved window bounds so width/height fit the target screen and at
    /// least part of the title bar remains reachable.
    /// </summary>
    public static Rectangle ClampToWorkingArea(Rectangle bounds)
    {
        Screen? screen = null;
        foreach (var s in Screen.AllScreens)
        {
            if (s.WorkingArea.IntersectsWith(bounds))
            {
                screen = s;
                break;
            }
        }
        screen ??= Screen.FromPoint(new Point(bounds.X, bounds.Y));

        Rectangle work = screen.WorkingArea;
        int width  = Math.Min(bounds.Width,  work.Width);
        int height = Math.Min(bounds.Height, work.Height);
        width  = Math.Max(width,  640);
        height = Math.Max(height, 400);

        int x = bounds.X;
        int y = bounds.Y;
        if (x + width  > work.Right)  x = work.Right  - width;
        if (y + height > work.Bottom) y = work.Bottom - height;
        if (x < work.Left)  x = work.Left;
        if (y < work.Top)   y = work.Top;

        return new Rectangle(x, y, width, height);
    }

    /// <summary>Scale a design-time pixel measurement for the given form DPI.</summary>
    public static int Scale(Form form, int designPixels)
    {
        if (designPixels <= 0) return designPixels;
        try
        {
            float dpi = form.DeviceDpi;
            if (dpi <= 0) dpi = DesignDpi;
            return Math.Max(1, (int)Math.Round(designPixels * dpi / DesignDpi));
        }
        catch
        {
            return designPixels;
        }
    }
}
