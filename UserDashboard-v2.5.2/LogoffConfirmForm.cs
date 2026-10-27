using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Confirmation dialog listing the RDS sessions that are about to be logged off,
/// grouped by user and host. Returns DialogResult.OK if the operator confirms.
///
/// If <paramref name="includesSelf"/> is true (operator is logging off their own
/// account), a warning banner is shown but the action is still allowed.
/// </summary>
internal sealed class LogoffConfirmForm : Form
{
    public LogoffConfirmForm(IList<RdsSession> sessions, bool includesSelf)
    {
        DpiHelper.ConfigureForm(this);
        Text            = "Log Off RDS Sessions";
        Size            = new Size(640, 520);
        MinimumSize     = new Size(520, 360);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar   = false;
        MinimizeBox     = false;

        // ------------------------------------------------------------------
        // Layout via a 4-row TableLayoutPanel. Unlike docking (which resolves
        // by z-order and was overlapping the body under the header), explicit
        // table rows give each region its own non-overlapping cell:
        //   Row 0 (auto)   header
        //   Row 1 (100%)   session list  <- absorbs all extra height
        //   Row 2 (auto)   warning strip
        //   Row 3 (auto)   button bar
        // ------------------------------------------------------------------
        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            Padding     = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // header
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // body
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // warning
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // buttons

        // -- Row 0: header --------------------------------------------------
        var header = new Label
        {
            AutoSize  = true,
            Text      = $"About to log off {sessions.Count} session(s):",
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            Margin    = new Padding(15, 12, 15, 6),
            TextAlign = ContentAlignment.MiddleLeft
        };

        // -- Row 1: session list -------------------------------------------
        var body = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            ReadOnly    = true,
            Font        = new Font("Consolas", 9.5f),
            BackColor   = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            WordWrap    = false,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Text        = BuildSessionText(sessions),
            DetectUrls  = false,
            TabStop     = false,
            Margin      = new Padding(15, 0, 15, 0)
        };

        // -- Row 2: warning strip ------------------------------------------
        var warn = new Label
        {
            AutoSize  = true,
            Margin    = new Padding(15, 6, 15, 4),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = includesSelf ? Color.Firebrick : Color.FromArgb(120, 70, 0),
            Font      = new Font("Segoe UI", 9f, includesSelf ? FontStyle.Bold : FontStyle.Regular),
            Text      = includesSelf
                ? "\u26A0 One or more of these sessions belongs to YOU. Logging off your own " +
                  "session will end your current work, and may close this dashboard if it's " +
                  "running in that session.\r\n\u26A0 This will terminate any unsaved work."
                : "\u26A0 This will terminate any unsaved work in these sessions."
        };

        // -- Row 3: button bar ---------------------------------------------
        var buttonBar = new FlowLayoutPanel
        {
            AutoSize      = true,
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding       = new Padding(10, 8, 12, 10)
        };
        var btnLogoff = new Button
        {
            Text      = "Log Off Sessions",
            Width     = 150,
            Height    = 32,
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(243, 112, 33),  // GSS orange
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin    = new Padding(6, 0, 0, 0)
        };
        var btnCancel = new Button
        {
            Text      = "Cancel",
            Width     = 100,
            Height    = 32,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            Margin    = new Padding(6, 0, 0, 0)
        };
        // RightToLeft flow: first added sits rightmost.
        buttonBar.Controls.Add(btnLogoff);
        buttonBar.Controls.Add(btnCancel);

        // Assemble the table
        root.Controls.Add(header,    0, 0);
        root.Controls.Add(body,      0, 1);
        root.Controls.Add(warn,      0, 2);
        root.Controls.Add(buttonBar, 0, 3);
        Controls.Add(root);

        AcceptButton = btnLogoff;
        CancelButton = btnCancel;

        // Make Cancel the default focus so an accidental Enter doesn't log people off.
        // Force the body to scroll to the absolute top. ScrollToCaret() alone is
        // unreliable on RichTextBox before full layout, so we also send the Win32
        // WM_VSCROLL/SB_TOP message once the form is shown - that always works.
        Shown += (_, _) =>
        {
            try
            {
                body.SelectionStart  = 0;
                body.SelectionLength = 0;
                body.ScrollToCaret();
                // WM_VSCROLL = 0x0115, SB_TOP = 6 - guarantees scroll to the top line.
                SendMessage(body.Handle, 0x0115, (IntPtr)6, IntPtr.Zero);
            }
            catch { /* cosmetic only */ }
            btnCancel.Focus();
        };

        Load += (_, _) => ThemeManager.ApplyTo(this);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static string BuildSessionText(IList<RdsSession> sessions)
    {
        var sb = new StringBuilder();
        foreach (var group in sessions
                     .GroupBy(s => s.QualifiedUser, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(group.Key);
            foreach (var hostGroup in group
                         .GroupBy(s => s.HostServer, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"    {hostGroup.Key}");
                foreach (var s in hostGroup.OrderBy(x => x.UnifiedSessionId))
                {
                    string idle = s.IdleTime.HasValue
                        ? FormatIdle(s.IdleTime.Value)
                        : "unknown";
                    sb.AppendLine(
                        $"        Session {s.UnifiedSessionId,-5} {s.FriendlyState,-13} idle {idle}");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatIdle(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}:{t.Minutes:D2}";
        return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
