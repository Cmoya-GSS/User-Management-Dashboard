using System.Drawing;
using System.Windows.Forms;

namespace UserDashboard.Services;

/// <summary>
/// One named palette of colors used across the dashboard. Two instances
/// exist (Light and Dark) and any form can ask <see cref="ThemeManager"/>
/// for the active one.
/// </summary>
public sealed class ThemePalette
{
    // Window / containers
    public Color FormBack       { get; set; }
    public Color PanelBack      { get; set; }
    public Color ControlFore    { get; set; }
    public Color BorderColor    { get; set; }

    // Inputs (TextBox, ComboBox)
    public Color InputBack      { get; set; }
    public Color InputFore      { get; set; }

    // Status strip
    public Color StatusBack     { get; set; }
    public Color StatusFore     { get; set; }

    // Tip line under the search box
    public Color TipFore        { get; set; }

    // DataGridView
    public Color GridBack            { get; set; }
    public Color GridFore            { get; set; }
    public Color GridAlternate       { get; set; }
    public Color GridLines           { get; set; }
    public Color GridSelectionBack   { get; set; }
    public Color GridSelectionFore   { get; set; }
    public Color HeaderBack          { get; set; }   // we keep brand green in both modes
    public Color HeaderFore          { get; set; }
    public Color ReadOnlyCellBack    { get; set; }
    public Color ReadOnlyCellFore    { get; set; }
    public Color DisabledRowBack     { get; set; }
    public Color DisabledRowSelBack  { get; set; }
    public Color DisabledRowFore     { get; set; }

    // Menus
    public Color MenuBack       { get; set; }
    public Color MenuFore       { get; set; }
}

/// <summary>
/// Active palette + utilities to apply it to control trees.
/// Caller is responsible for triggering re-theme when the choice changes.
/// </summary>
public static class ThemeManager
{
    public static readonly ThemePalette Light = new()
    {
        // Soft neutral canvas (not classic WinForms gray)
        FormBack          = Color.FromArgb(245, 247, 248),
        PanelBack         = Color.FromArgb(245, 247, 248),
        ControlFore       = Color.FromArgb(32, 36, 40),
        BorderColor       = Color.FromArgb(210, 216, 220),

        InputBack         = Color.White,
        InputFore         = Color.FromArgb(32, 36, 40),

        StatusBack        = Color.FromArgb(238, 241, 243),
        StatusFore        = Color.FromArgb(70, 78, 86),
        TipFore           = Color.FromArgb(90, 110, 100),

        GridBack          = Color.White,
        GridFore          = Color.FromArgb(32, 36, 40),
        GridAlternate     = Color.FromArgb(236, 245, 240),
        GridLines         = Color.FromArgb(226, 232, 228),
        // Explicit pair (not SystemColors) so selected rows stay high-contrast
        // even when a row overrides ForeColor (e.g. protected / disabled).
        GridSelectionBack = Color.FromArgb(0, 90, 158),
        GridSelectionFore = Color.White,
        HeaderBack        = Color.FromArgb(0, 130, 68),   // slightly deeper brand green
        HeaderFore        = Color.White,
        ReadOnlyCellBack  = Color.FromArgb(248, 249, 250),
        ReadOnlyCellFore  = Color.FromArgb(110, 118, 126),
        DisabledRowBack   = Color.FromArgb(255, 236, 236),
        DisabledRowSelBack= Color.FromArgb(255, 210, 210),
        DisabledRowFore   = Color.FromArgb(120, 120, 120),

        MenuBack          = Color.White,
        MenuFore          = Color.FromArgb(32, 36, 40),
    };

    public static readonly ThemePalette Dark = new()
    {
        FormBack          = Color.FromArgb(30, 30, 30),    // #1E1E1E
        PanelBack         = Color.FromArgb(37, 37, 38),    // #252526
        ControlFore       = Color.FromArgb(220, 220, 220), // soft white
        BorderColor       = Color.FromArgb(63, 63, 70),

        InputBack         = Color.FromArgb(45, 45, 48),    // #2D2D30
        InputFore         = Color.FromArgb(220, 220, 220),

        StatusBack        = Color.FromArgb(45, 45, 48),
        StatusFore        = Color.FromArgb(220, 220, 220),
        TipFore           = Color.FromArgb(120, 200, 150), // muted green

        GridBack          = Color.FromArgb(30, 30, 30),
        GridFore          = Color.FromArgb(220, 220, 220),
        GridAlternate     = Color.FromArgb(37, 37, 38),
        GridLines         = Color.FromArgb(63, 63, 70),
        GridSelectionBack = Color.FromArgb(0, 100, 60),    // muted brand green
        GridSelectionFore = Color.White,
        HeaderBack        = Color.FromArgb(0, 146, 75),    // brand green stays
        HeaderFore        = Color.White,
        ReadOnlyCellBack  = Color.FromArgb(45, 45, 48),
        ReadOnlyCellFore  = Color.FromArgb(150, 150, 150),
        DisabledRowBack   = Color.FromArgb(77, 26, 26),    // dark muted red
        DisabledRowSelBack= Color.FromArgb(110, 40, 40),
        DisabledRowFore   = Color.FromArgb(180, 180, 180),

        MenuBack          = Color.FromArgb(45, 45, 48),
        MenuFore          = Color.FromArgb(220, 220, 220),
    };

    public static ThemePalette Current { get; private set; } = Light;
    public static bool IsDark => Current == Dark;

    public static void SetTheme(bool dark) => Current = dark ? Dark : Light;

    /// <summary>
    /// Walk a control tree and apply foreground/background colors per the active palette.
    /// Skips controls that have explicit branded colors (toolbar buttons in GSS green/orange):
    /// those have BackColor != SystemColors.Control already, so we leave them alone.
    /// DataGridView is also skipped here - the main form re-styles its grid explicitly via RethemeGrid.
    /// </summary>
    public static void ApplyTo(Control root)
    {
        var p = Current;
        ApplyRecursive(root, p);
    }

    private static void ApplyRecursive(Control c, ThemePalette p)
    {
        switch (c)
        {
            case DataGridView:
                // The main form handles grid theming via its own helper.
                break;

            case TextBox:
            case RichTextBox:
            case ComboBox:
            case ListBox:
            case NumericUpDown:
                c.BackColor = p.InputBack;
                c.ForeColor = p.InputFore;
                break;

            case Label lbl:
                // Don't change AutoSize labels' bg if they're transparent;
                // just update the foreground.
                lbl.ForeColor = p.ControlFore;
                break;

            case Button btn:
                // Toolbar buttons have explicit brand colors set; leave those alone.
                // Default-styled buttons get the panel back/fore so they read correctly.
                if (btn.BackColor == SystemColors.Control || btn.BackColor == Color.Empty)
                {
                    btn.BackColor = p.PanelBack;
                    btn.ForeColor = p.ControlFore;
                }
                break;

            case StatusStrip ss:
                ss.BackColor = p.StatusBack;
                ss.ForeColor = p.StatusFore;
                foreach (ToolStripItem item in ss.Items)
                {
                    item.BackColor = p.StatusBack;
                    item.ForeColor = p.StatusFore;
                }
                break;

            case ToolStrip ts:
                ts.BackColor = p.MenuBack;
                ts.ForeColor = p.MenuFore;
                foreach (ToolStripItem item in ts.Items)
                {
                    item.BackColor = p.MenuBack;
                    item.ForeColor = p.MenuFore;
                }
                break;

            case Panel:
            case GroupBox:
            case UserControl:
                c.BackColor = p.PanelBack;
                c.ForeColor = p.ControlFore;
                break;

            case Form:
                c.BackColor = p.FormBack;
                c.ForeColor = p.ControlFore;
                break;

            default:
                // Leave unrecognized controls alone unless they're using a default palette.
                if (c.BackColor == SystemColors.Control || c.BackColor == Color.White)
                {
                    c.BackColor = p.FormBack;
                }
                if (c.ForeColor == SystemColors.ControlText || c.ForeColor == Color.Black)
                {
                    c.ForeColor = p.ControlFore;
                }
                break;
        }

        foreach (Control child in c.Controls)
            ApplyRecursive(child, p);
    }

    /// <summary>
    /// Theme a ContextMenuStrip's items (which are not part of any control tree).
    /// </summary>
    public static void ApplyTo(ToolStrip menu)
    {
        var p = Current;
        menu.BackColor = p.MenuBack;
        menu.ForeColor = p.MenuFore;
        ApplyMenuItems(menu.Items, p);
    }

    private static void ApplyMenuItems(ToolStripItemCollection items, ThemePalette p)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = p.MenuBack;
            item.ForeColor = p.MenuFore;
            if (item is ToolStripMenuItem mi && mi.HasDropDownItems)
                ApplyMenuItems(mi.DropDownItems, p);
        }
    }
}
