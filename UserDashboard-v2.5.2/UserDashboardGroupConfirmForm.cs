using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Confirmation dialog shown before adding user(s) to the UserDashboard AD
/// group. Requires an explicit checkbox acknowledgement before Apply is enabled.
/// </summary>
internal sealed class UserDashboardGroupConfirmForm : Form
{
    public UserDashboardGroupConfirmForm(IReadOnlyList<string> samAccountNames)
    {
        if (samAccountNames == null || samAccountNames.Count == 0)
            throw new ArgumentException("At least one user is required.", nameof(samAccountNames));

        DpiHelper.ConfigureForm(this);
        Text            = "Confirm UserDashboard Group Membership";
        Size            = new Size(560, 460);
        MinimumSize     = new Size(480, 400);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar   = false;
        MaximizeBox     = false;
        MinimizeBox     = false;

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 5,
            Padding     = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        string headerText = samAccountNames.Count == 1
            ? $"Adding 1 user to the {Constants.UserDashboardAccessGroup} group:"
            : $"Adding {samAccountNames.Count} users to the {Constants.UserDashboardAccessGroup} group:";

        var header = new Label
        {
            AutoSize  = true,
            Text      = headerText,
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            Margin    = new Padding(15, 12, 15, 6),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var body = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            ReadOnly    = true,
            Font        = new Font("Consolas", 9.5f),
            BackColor   = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            WordWrap    = false,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Text        = BuildUserListText(samAccountNames),
            DetectUrls  = false,
            TabStop     = false,
            Margin      = new Padding(15, 0, 15, 0)
        };

        var warn = new Label
        {
            AutoSize  = true,
            Margin    = new Padding(15, 8, 15, 4),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(120, 70, 0),
            Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Text      =
                "\u26A0 Warning: Only System Administrators should be a part of this group.\r\n\r\n" +
                $"Members of the {Constants.UserDashboardAccessGroup} group can:\r\n" +
                "  \u2022 Change other users' passwords\r\n" +
                "  \u2022 Log users out of the system (RDS sessions)\r\n\r\n" +
                "Do not add standard end users to this group."
        };

        string chkText = samAccountNames.Count == 1
            ? "I confirm that the selected user is a System Administrator and should be added to the UserDashboard group."
            : "I confirm that the selected users are System Administrators and should be added to the UserDashboard group.";

        var chk = new CheckBox
        {
            AutoSize = true,
            Margin   = new Padding(15, 4, 15, 4),
            Text     = chkText
        };

        var buttonBar = new FlowLayoutPanel
        {
            AutoSize      = true,
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding       = new Padding(10, 8, 12, 10)
        };
        var btnConfirm = new Button
        {
            Text         = "Add to UserDashboard",
            Width        = 160,
            Height       = 32,
            Enabled      = false,
            DialogResult = DialogResult.OK,
            BackColor    = Constants.GssOrange,
            ForeColor    = Color.White,
            FlatStyle    = FlatStyle.Flat,
            Margin       = new Padding(6, 0, 0, 0)
        };
        var btnCancel = new Button
        {
            Text         = "Cancel",
            Width        = 100,
            Height       = 32,
            DialogResult = DialogResult.Cancel,
            FlatStyle    = FlatStyle.Flat,
            Margin       = new Padding(6, 0, 0, 0)
        };
        buttonBar.Controls.Add(btnConfirm);
        buttonBar.Controls.Add(btnCancel);

        chk.CheckedChanged += (_, _) => btnConfirm.Enabled = chk.Checked;

        root.Controls.Add(header,    0, 0);
        root.Controls.Add(body,      0, 1);
        root.Controls.Add(warn,      0, 2);
        root.Controls.Add(chk,       0, 3);
        root.Controls.Add(buttonBar, 0, 4);
        Controls.Add(root);

        CancelButton = btnCancel;
        Shown += (_, _) => btnCancel.Focus();

        Load += (_, _) => ThemeManager.ApplyTo(this);
    }

    private static string BuildUserListText(IReadOnlyList<string> samAccountNames)
    {
        var ordered = samAccountNames
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        const int previewLimit = 15;
        foreach (var sam in ordered.Take(previewLimit))
            sb.AppendLine(sam);
        if (ordered.Count > previewLimit)
            sb.AppendLine($"... and {ordered.Count - previewLimit} more");
        return sb.ToString();
    }
}
