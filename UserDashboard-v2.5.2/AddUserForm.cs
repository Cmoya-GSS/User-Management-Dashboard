using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Resizable Add-User dialog modeled on the native AD "New Object - User"
/// wizard. Mirrors $btnAdd.Add_Click in UserDashboard.ps1 (lines ~1674-2102).
/// </summary>
internal sealed class AddUserForm : Form
{
    private readonly AdService _ad;

    private readonly TextBox  _txtFirst, _txtInit, _txtLast, _txtFull;
    private readonly TextBox  _txtUpn, _txtPre2k, _txtPre2kPrefix, _txtPwd;
    private readonly ComboBox _cmbUpn, _cmbPwdLength;
    private readonly PasswordChecklist _checklist;
    private readonly CheckBox _chkShowPwdLink;

    private bool _syncingSam;
    private bool _syncingFull;

    public AddUserForm(AdService ad)
    {
        _ad = ad;

        DpiHelper.ConfigureForm(this);
        Text          = "New Object - User";
        StartPosition = FormStartPosition.CenterParent;
        var work      = DpiHelper.PrimaryWorkingArea;
        int formW     = Math.Min(860, work.Width - 40);
        int formH     = Math.Min(560, work.Height - 80);
        Size          = new Size(Math.Max(formW, 640), Math.Max(formH, 480));
        MinimumSize   = new Size(Math.Max(formW, 640), Math.Max(formH, 480));
        BackColor     = Color.WhiteSmoke;
        Font          = new Font("Segoe UI", 9f);

        // ---------- Header ("Create in: <domain>/Users") -------------------
        var header = new Label
        {
            Text     = $"Create in: {_ad.DnsRoot}/Users",
            AutoSize = true,
            Location = new Point(20, 20),
            Font     = new Font("Segoe UI", 11f),
            BackColor = Color.Transparent
        };
        Controls.Add(header);

        var separator = new Label
        {
            BorderStyle = BorderStyle.Fixed3D,
            Height      = 2,
            Location    = new Point(0, 60),
            Width       = ClientSize.Width,
            Anchor      = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(separator);

        // ---------- Field grid --------------------------------------------
        // Two-column TableLayoutPanel: labels left, controls right.
        var grid = new TableLayoutPanel
        {
            Location    = new Point(20, 80),
            Size        = new Size(ClientSize.Width - 40, 380),
            ColumnCount = 2,
            RowCount    = 7,
            Anchor      = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor   = BackColor
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(grid);

        // Row 0: First / Initials
        _txtFirst = NewBox();
        _txtInit  = new TextBox { Width = 60, Margin = new Padding(0, 6, 0, 6) };
        var firstPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty
        };
        firstPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        firstPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        firstPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        firstPanel.Controls.Add(_txtFirst, 0, 0);
        firstPanel.Controls.Add(new Label { Text = "Initials:", AutoSize = true,
            Margin = new Padding(8, 10, 4, 0) }, 1, 0);
        firstPanel.Controls.Add(_txtInit, 2, 0);
        AddRow(grid, 0, "First name:", firstPanel);

        // Row 1: Last name
        _txtLast = NewBox();
        AddRow(grid, 1, "Last name:", _txtLast);

        // Row 2: Full name (auto-populated)
        _txtFull = NewBox();
        AddRow(grid, 2, "Full name:", _txtFull);

        // Row 3: User logon name (UPN local part + suffix)
        _txtUpn  = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 8, 6) };
        _cmbUpn  = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 220,
            Margin        = new Padding(8, 6, 0, 6)
        };
        _cmbUpn.Items.Add(_ad.UpnSuffix);
        _cmbUpn.SelectedIndex = 0;
        var upnPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        upnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        upnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        upnPanel.Controls.Add(_txtUpn, 0, 0);
        upnPanel.Controls.Add(_cmbUpn, 1, 0);
        AddRow(grid, 3, "User logon name:", upnPanel);

        // Row 4: Pre-Windows 2000 logon
        _txtPre2kPrefix = new TextBox
        {
            Dock      = DockStyle.Fill,
            ReadOnly  = true,
            Text      = _ad.NetBiosDomain + "\\",
            BackColor = Color.WhiteSmoke,
            Margin    = new Padding(0, 6, 8, 6)
        };
        _txtPre2k = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(8, 6, 0, 6) };
        var pre2kPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        pre2kPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        pre2kPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pre2kPanel.Controls.Add(_txtPre2kPrefix, 0, 0);
        pre2kPanel.Controls.Add(_txtPre2k, 1, 0);
        AddRow(grid, 4, "User logon name (pre-Windows 2000):", pre2kPanel);

        // Row 5: Password + length + generate + checklist
        _txtPwd = new TextBox
        {
            Width = 170, Margin = new Padding(0, 6, 8, 6),
            UseSystemPasswordChar = false
        };
        _cmbPwdLength = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 60, Margin = new Padding(0, 6, 8, 6)
        };
        _cmbPwdLength.Items.AddRange(new object[] { "11", "12", "14", "16" });
        _cmbPwdLength.SelectedItem = "11";

        var btnGen = new Button
        {
            Text = "Password Generator", Width = 145, Height = 28,
            BackColor = Constants.GssGreen, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 6, 8, 6)
        };
        btnGen.Click += (_, _) => GeneratePassword();

        _checklist = new PasswordChecklist
        {
            Margin = new Padding(8, 0, 0, 0)
        };

        var pwdPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty
        };
        pwdPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        pwdPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        pwdPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        pwdPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pwdPanel.Controls.Add(_txtPwd,        0, 0);
        pwdPanel.Controls.Add(_cmbPwdLength,  1, 0);
        pwdPanel.Controls.Add(btnGen,         2, 0);
        pwdPanel.Controls.Add(_checklist,     3, 0);
        AddRow(grid, 5, "Password:", pwdPanel);

        // ---------- Bottom buttons ----------------------------------------
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 108, BackColor = BackColor };
        Controls.Add(bottom);

        var policy = new Label
        {
            AutoSize    = true,
            MaximumSize = new Size(620, 0),
            Location    = new Point(20, 8),
            ForeColor   = Color.FromArgb(120, 70, 0),
            Font        = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Text        = "By default the password never expires and is copied to the clipboard after create. " +
                          "Check below to force a temporary password (must change at next logon) instead."
        };
        bottom.Controls.Add(policy);

        _chkShowPwdLink = new CheckBox
        {
            Text     = "Temporary password — require change at next logon (show link)",
            AutoSize = true,
            Checked  = false,  // default: permanent password (never expires)
            Location = new Point(20, 52),
            Anchor   = AnchorStyles.Top | AnchorStyles.Left,
            Cursor   = Cursors.Hand
        };
        bottom.Controls.Add(_chkShowPwdLink);

        var btnCreate = new Button
        {
            Text      = "Create",
            Width     = 110,
            Height    = 30,
            BackColor = Constants.GssOrange,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Anchor    = AnchorStyles.Top | AnchorStyles.Right,
            Location  = new Point(bottom.Width - 130, 68)
        };
        var btnCancel = new Button
        {
            Text         = "Cancel",
            Width        = 0,    // hidden; AD wizard doesn't show one, but we want a CancelButton
            Height       = 0,
            DialogResult = DialogResult.Cancel,
            Visible      = false
        };
        bottom.Controls.Add(btnCreate);
        bottom.Controls.Add(btnCancel);
        bottom.Resize += (_, _) => btnCreate.Location = new Point(bottom.Width - 130, 68);

        AcceptButton = btnCreate;
        CancelButton = btnCancel;

        btnCreate.Click += OnCreate;

        // ---------- Wiring ------------------------------------------------
        _txtFirst.TextChanged += (_, _) => { UpdateFullName(); UpdateSamSuggestion(); };
        _txtInit .TextChanged += (_, _) => { UpdateFullName(); UpdateSamSuggestion(); };
        _txtLast .TextChanged += (_, _) => { UpdateFullName(); UpdateSamSuggestion(); };
        _txtUpn  .TextChanged += (_, _) => SyncPre2kFromUpn();
        _txtPwd  .TextChanged += (_, _) => _checklist.UpdateFor(_txtPwd.Text);

        Shown += (_, _) => _txtFirst.Focus();

        // Apply current theme on Load (after all child controls are attached)
        Load += (_, _) => ThemeManager.ApplyTo(this);
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    private static TextBox NewBox() => new()
    {
        Dock   = DockStyle.Fill,
        Margin = new Padding(0, 6, 0, 6)
    };

    private static void AddRow(TableLayoutPanel grid, int row, string labelText, Control field)
    {
        grid.Controls.Add(new Label
        {
            Text = labelText, AutoSize = true,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 8, 0)
        }, 0, row);
        grid.Controls.Add(field, 1, row);
    }

    private static string SuggestSam(string first, string initials, string last)
    {
        first    = (first ?? "").Trim();
        initials = (initials ?? "").Trim();
        last     = (last ?? "").Trim();
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(last)) return "";

        string fi = new string(first.Where(char.IsLetter).ToArray());
        fi = fi.Length > 0 ? fi.Substring(0, 1).ToUpperInvariant() : "";

        string mi = "";
        if (!string.IsNullOrWhiteSpace(initials))
        {
            string mid = new string(initials.Where(char.IsLetter).ToArray());
            if (mid.Length > 0) mi = mid.Substring(0, 1).ToUpperInvariant();
        }

        string ln = new string(last.Where(c => char.IsLetterOrDigit(c)).ToArray());
        return fi + mi + ln;
    }

    private void UpdateFullName()
    {
        if (_syncingFull) return;
        _syncingFull = true;
        try
        {
            var parts = new[] { _txtFirst.Text.Trim(), _txtInit.Text.Trim(), _txtLast.Text.Trim() }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            _txtFull.Text = string.Join(" ", parts).Trim();
        }
        finally { _syncingFull = false; }
    }

    private void UpdateSamSuggestion()
    {
        if (_syncingSam) return;
        _syncingSam = true;
        try
        {
            var suggest = SuggestSam(_txtFirst.Text, _txtInit.Text, _txtLast.Text);

            // Only overwrite the UPN if the user hasn't customized it.
            string prevUpn = _txtUpn.Tag as string ?? "";
            if (string.IsNullOrEmpty(_txtUpn.Text) || _txtUpn.Text == prevUpn)
                _txtUpn.Text = suggest;
            _txtUpn.Tag = suggest;

            string prevPre = _txtPre2k.Tag as string ?? "";
            if (string.IsNullOrEmpty(_txtPre2k.Text) || _txtPre2k.Text == prevPre)
                _txtPre2k.Text = suggest;
            _txtPre2k.Tag = suggest;
        }
        finally { _syncingSam = false; }
    }

    private void SyncPre2kFromUpn()
    {
        if (_syncingSam) return;
        _syncingSam = true;
        try
        {
            _txtPre2k.Text = _txtUpn.Text.Trim();
            _txtPre2k.Tag  = _txtUpn.Text.Trim();
        }
        finally { _syncingSam = false; }
    }

    private void GeneratePassword()
    {
        int length = int.TryParse(_cmbPwdLength.SelectedItem?.ToString(), out var n)
            ? n : Constants.MinPasswordLength;
        _txtPwd.Text = PasswordHelper.Generate(length);
        _txtPwd.SelectAll();
        _txtPwd.Focus();
    }

    // ====================================================================
    //  Submit
    // ====================================================================

    private void OnCreate(object? sender, EventArgs e)
    {
        string first    = _txtFirst.Text.Trim();
        string initials = _txtInit .Text.Trim();
        string last     = _txtLast .Text.Trim();
        string full     = _txtFull .Text.Trim();
        string user     = _txtUpn  .Text.Trim();
        string pre2k    = _txtPre2k.Text.Trim();
        string pwd      = _txtPwd  .Text;

        // Strip @suffix the user might have typed
        if (user.Contains("@")) user = user.Split('@')[0].Trim();
        if (string.IsNullOrEmpty(pre2k)) pre2k = user;

        if (string.IsNullOrEmpty(full))
        {
            full = string.Join(" ", new[] { first, initials, last }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            if (string.IsNullOrEmpty(full)) full = user;
            _txtFull.Text = full;
        }

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pre2k))
        {
            MessageBox.Show(this,
                "User logon name and pre-Windows 2000 logon name are required.");
            return;
        }

        if (_ad.UserExists(user))
        {
            MessageBox.Show(this, $"Username '{user}' already exists.");
            return;
        }
        if (!string.Equals(pre2k, user, StringComparison.OrdinalIgnoreCase) && _ad.UserExists(pre2k))
        {
            MessageBox.Show(this, $"Pre-Windows 2000 logon name '{pre2k}' already exists.");
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
            string upn = user + (_cmbUpn.SelectedItem?.ToString() ?? _ad.UpnSuffix);
            // Checked  → temporary: must change at next logon (never-expire OFF — AD rule).
            // Unchecked → permanent: password never expires, no forced change.
            bool mustChange   = _chkShowPwdLink.Checked;
            bool neverExpires = !mustChange;
            _ad.CreateUser(new AdService.CreateUserRequest
            {
                SamAccountName    = pre2k,
                UserPrincipalName = upn,
                DisplayName       = full,
                GivenName         = string.IsNullOrWhiteSpace(first) ? null : first,
                Surname           = string.IsNullOrWhiteSpace(last)  ? null : last,
                Initials          = string.IsNullOrWhiteSpace(initials) ? null : initials,
                Password          = pwd,
                Path              = _ad.UsersContainerDn,
                PasswordNeverExpires          = neverExpires,
                MustChangePasswordAtNextLogon = mustChange
            });

            AuditLogger.Write("CreateUser", user,
                $"DisplayName={full};UPN={upn};PreWin2000={_ad.NetBiosDomain}\\{pre2k};" +
                $"mustChangeAtNextLogon={mustChange};passwordNeverExpires={neverExpires}");

            // Always place the password on the clipboard so the operator can
            // paste it into email/chat for the new user.
            bool clipboardOk = TryCopyPasswordToClipboard(pwd);

            string? pwdUrl = RdWebLocator.GetPasswordChangeUrl();
            string clipboardNote = clipboardOk
                ? "The password has been copied to your clipboard so you can provide it to the new user."
                : "Could not copy the password to the clipboard (another app may have it locked). " +
                  "Copy it from the Password field before closing this dialog.";

            if (mustChange)
            {
                string body =
                    $"User '{upn}' created.\r\n\r\n" +
                    clipboardNote + "\r\n\r\n" +
                    "This is a TEMPORARY password. The user must open the self-service " +
                    "password page and set a unique password that only they know before " +
                    "they can log on.\r\n\r\n" +
                    "Send them this link (or open it to walk them through the change):";

                using (var result = new RdWebPasswordLinkForm(
                           "User Created — Password Change Required",
                           body,
                           pwdUrl))
                {
                    result.ShowDialog(this);
                }
            }
            else
            {
                string body =
                    $"User '{upn}' created.\r\n\r\n" +
                    clipboardNote + "\r\n\r\n" +
                    "The password was set as entered and is set to never expire. " +
                    "The user is not required to change it at next logon.\r\n\r\n" +
                    "If they need to change it later, use this password-change page:";

                using (var result = new RdWebPasswordLinkForm(
                           "User Created",
                           body,
                           pwdUrl))
                {
                    result.ShowDialog(this);
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to create user: " + ex.Message);
            _txtPwd.SelectAll();
            _txtPwd.Focus();
        }
    }

    private static bool TryCopyPasswordToClipboard(string password)
    {
        try
        {
            Clipboard.SetText(password ?? "");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
