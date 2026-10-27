using System;
using System.Drawing;
using System.Windows.Forms;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Password-reset dialog. Mirrors the $miReset.Add_Click block in
/// UserDashboard.ps1 (lines ~1363-1540).
/// </summary>
internal sealed class ResetPasswordForm : Form
{
    private readonly AdService _ad;
    private readonly string _samAccountName;
    private readonly TextBox _txtPwd;
    private readonly ComboBox _cmbLength;
    private readonly PasswordChecklist _checklist;
    private readonly CheckBox _chkMustChange;

    public ResetPasswordForm(AdService ad, string samAccountName, string displayName)
    {
        _ad = ad;
        _samAccountName = samAccountName;

        DpiHelper.ConfigureForm(this);
        Text            = $"Reset Password - {displayName}";
        Size            = new Size(640, 345);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        Font            = new Font("Segoe UI", 9f);
        BackColor       = Color.White;

        var lblPwd = new Label
        {
            Text     = "New Password:",
            Location = new Point(15, 18),
            AutoSize = true
        };
        Controls.Add(lblPwd);

        _txtPwd = new TextBox
        {
            Location              = new Point(15, 40),
            Size                  = new Size(170, 23),
            UseSystemPasswordChar = false
        };
        Controls.Add(_txtPwd);

        _cmbLength = new ComboBox
        {
            Location      = new Point(195, 39),
            Size          = new Size(60, 23),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cmbLength.Items.AddRange(new object[] { "11", "12", "14", "16" });
        _cmbLength.SelectedItem = "11";
        Controls.Add(_cmbLength);

        var btnGen = new Button
        {
            Text      = "Password Generator",
            Location  = new Point(265, 38),
            Size      = new Size(145, 28),
            BackColor = Constants.GssGreen,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnGen.Click += (_, _) => GeneratePassword();
        Controls.Add(btnGen);

        _checklist = new PasswordChecklist
        {
            Location = new Point(15, 80)
        };
        Controls.Add(_checklist);

        _txtPwd.TextChanged += (_, _) => _checklist.UpdateFor(_txtPwd.Text);

        _chkMustChange = new CheckBox
        {
            Text     = "Require user to change this password at next logon (temp password)",
            AutoSize = true,
            // Below the checklist: PasswordChecklist is a UserControl whose
            // AutoSize will not shrink it under the 150x150 default, so it
            // occupies y=80..230. Place the checkbox clear of that region.
            Location = new Point(15, 238),
            Checked  = true    // FedRAMP / IA-5: admin-set passwords are temporary by default
        };
        Controls.Add(_chkMustChange);
        _chkMustChange.BringToFront();

        // Bottom buttons
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = BackColor };
        Controls.Add(bottom);

        var btnOk = new Button
        {
            Text      = "Reset",
            Width     = 90,
            Location  = new Point(420, 10),
            BackColor = Constants.GssOrange,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        var btnCancel = new Button
        {
            Text         = "Cancel",
            Width        = 90,
            Location     = new Point(515, 10),
            DialogResult = DialogResult.Cancel,
            FlatStyle    = FlatStyle.Flat
        };
        bottom.Controls.Add(btnOk);
        bottom.Controls.Add(btnCancel);

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        btnOk.Click += OnReset;

        // Apply current theme on Load (after all child controls are attached)
        Load += (_, _) => ThemeManager.ApplyTo(this);
    }

    private void GeneratePassword()
    {
        int length = int.TryParse(_cmbLength.SelectedItem?.ToString(), out var n)
            ? n : Constants.MinPasswordLength;
        _txtPwd.Text = PasswordHelper.Generate(length);
        _txtPwd.SelectAll();
        _txtPwd.Focus();
    }

    private void OnReset(object? sender, EventArgs e)
    {
        string pwd = _txtPwd.Text.Trim();
        if (string.IsNullOrWhiteSpace(pwd))
        {
            MessageBox.Show(this, "Password cannot be blank.");
            return;
        }

        var missing = PasswordHelper.Validate(pwd);
        if (missing.Count > 0)
        {
            MessageBox.Show(this, "Password must include: " + string.Join(", ", missing));
            _txtPwd.SelectAll();
            _txtPwd.Focus();
            return;
        }

        try
        {
            bool mustChange = _chkMustChange.Checked;
            _ad.ResetPassword(_samAccountName, pwd, mustChange);
            AuditLogger.Write("ResetPassword", _samAccountName,
                mustChange
                    ? "Manual reset via dashboard; user must change at next logon"
                    : "Manual reset via dashboard; password set to never expire");

            string body =
                "Password reset successful.\r\n\r\n" +
                (mustChange
                    ? "This is a TEMP password - the user must set their own at next logon. "
                    : "The password is set to never expire. ") +
                "Hand it to the user through a secure channel.\r\n\r\n" +
                "The user can change this password themselves at:";

            using (var result = new RdWebPasswordLinkForm(
                       "Password Reset", body, RdWebLocator.GetPasswordChangeUrl()))
            {
                result.ShowDialog(this);
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Error resetting password: " + ex.Message);
            _txtPwd.SelectAll();
            _txtPwd.Focus();
        }
    }
}
