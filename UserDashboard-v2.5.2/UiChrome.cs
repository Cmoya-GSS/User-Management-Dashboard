using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UserDashboard;

/// <summary>
/// Small WinForms chrome helpers used to give the dashboard a cleaner,
/// more modern look without leaving the existing control set.
/// </summary>
internal static class UiChrome
{
    private const int EmSetCueBanner = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    /// <summary>Win32 cue banner (placeholder) for .NET Framework TextBoxes.</summary>
    public static void SetCueBanner(TextBox textBox, string text)
    {
        void Apply() => SendMessage(textBox.Handle, EmSetCueBanner, (IntPtr)1, text);

        if (textBox.IsHandleCreated) Apply();
        else textBox.HandleCreated += (_, _) => Apply();
    }

    public static Color Lighten(Color c, int amount) => Color.FromArgb(
        c.A,
        Math.Min(255, c.R + amount),
        Math.Min(255, c.G + amount),
        Math.Min(255, c.B + amount));

    public static Color Darken(Color c, int amount) => Color.FromArgb(
        c.A,
        Math.Max(0, c.R - amount),
        Math.Max(0, c.G - amount),
        Math.Max(0, c.B - amount));

    /// <summary>Apply flat brand styling + hover/press shades to a toolbar button.</summary>
    public static void StyleBrandButton(Button btn, Color brand)
    {
        btn.BackColor = brand;
        btn.ForeColor = Color.White;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Lighten(brand, 18);
        btn.FlatAppearance.MouseDownBackColor = Darken(brand, 18);
        btn.Cursor = Cursors.Hand;
        btn.Tag = brand;
    }
}
