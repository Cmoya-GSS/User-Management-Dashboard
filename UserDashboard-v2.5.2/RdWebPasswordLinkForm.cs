using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Success / info dialog that shows the RD Web self-service password URL as a
/// real hyperlink (opens in the default browser) and a Copy Link button for
/// email or chat.
/// </summary>
internal sealed class RdWebPasswordLinkForm : Form
{
    public RdWebPasswordLinkForm(string title, string body, string? url)
    {
        DpiHelper.ConfigureForm(this);
        Text            = title;
        Size            = new Size(560, 380);
        MinimumSize     = new Size(480, 320);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar   = false;
        MaximizeBox     = false;
        MinimizeBox     = false;

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            Padding     = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var bodyLbl = new Label
        {
            AutoSize  = true,
            Text      = body,
            Font      = new Font("Segoe UI", 9.5f),
            Margin    = new Padding(16, 16, 16, 8),
            MaximumSize = new Size(510, 0)
        };

        var link = new LinkLabel
        {
            AutoSize  = true,
            Text      = string.IsNullOrWhiteSpace(url) ? "(password page URL is not available)" : url,
            Font      = new Font("Segoe UI", 9.5f, FontStyle.Underline),
            Margin    = new Padding(16, 4, 16, 4),
            LinkColor = Color.FromArgb(0, 102, 204),
            Enabled   = !string.IsNullOrWhiteSpace(url)
        };
        if (!string.IsNullOrWhiteSpace(url))
        {
            link.Links.Add(0, url!.Length, url);
            link.LinkClicked += (_, e) => OpenInBrowser(e.Link.LinkData as string);
        }

        var hint = new Label
        {
            AutoSize  = true,
            Text      = "Click the link to open it, or copy it to send in email or chat.",
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(80, 80, 80),
            Margin    = new Padding(16, 0, 16, 4)
        };

        var buttonBar = new FlowLayoutPanel
        {
            AutoSize      = true,
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding       = new Padding(10, 8, 12, 10)
        };
        var btnOk = new Button
        {
            Text         = "OK",
            Width        = 90,
            Height       = 32,
            DialogResult = DialogResult.OK,
            BackColor    = Constants.GssOrange,
            ForeColor    = Color.White,
            FlatStyle    = FlatStyle.Flat,
            Margin       = new Padding(6, 0, 0, 0)
        };
        var btnCopy = new Button
        {
            Text      = "Copy Link",
            Width     = 110,
            Height    = 32,
            FlatStyle = FlatStyle.Flat,
            Enabled   = !string.IsNullOrWhiteSpace(url),
            Margin    = new Padding(6, 0, 0, 0)
        };
        btnCopy.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Clipboard.SetText(url);
                btnCopy.Text = "Copied";
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { btnCopy.Text = "Copy Link"; t.Stop(); t.Dispose(); };
                t.Start();
            }
            catch
            {
                MessageBox.Show(this, "Could not copy to the clipboard. Another app may have it locked.",
                    title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        buttonBar.Controls.Add(btnOk);
        buttonBar.Controls.Add(btnCopy);

        root.Controls.Add(bodyLbl,   0, 0);
        root.Controls.Add(link,      0, 1);
        root.Controls.Add(hint,      0, 2);
        root.Controls.Add(buttonBar, 0, 3);
        Controls.Add(root);

        AcceptButton = btnOk;
        CancelButton = btnOk;
        Shown += (_, _) => btnOk.Focus();
        Load  += (_, _) => ThemeManager.ApplyTo(this);
    }

    private static void OpenInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not open the default browser:\r\n" + ex.Message,
                "Open Link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
