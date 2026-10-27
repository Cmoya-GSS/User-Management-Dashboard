using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using UserDashboard.Models;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Assign Global Shop company codes to one user by toggling CC-* group
/// membership and storing the default code in an AD attribute.
///
/// Policy: a user with zero company-code groups is unrestricted and sees
/// every company code at logon (no GSSLOGIN.DIR restriction).
/// </summary>
internal sealed class CompanyCodesForm : Form
{
    private readonly AdService _ad;
    private readonly AdUserInfo _user;
    private readonly CheckedListBox _list;
    private readonly ComboBox _cmbDefault;
    private readonly Label _lblStatus;
    private readonly TextBox _txtNew;
    private readonly HashSet<string> _initialCodes;
    private readonly string? _initialPrimary;

    public CompanyCodesForm(AdService ad, AdUserInfo user)
    {
        _ad   = ad;
        _user = user;

        DpiHelper.ConfigureForm(this);
        Text            = "Company Codes - " + user.SamAccountName;
        Size            = new Size(560, 520);
        MinimumSize     = new Size(480, 440);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = false;
        MinimizeBox     = false;
        Font            = new Font("Segoe UI", 9f);
        ShowInTaskbar   = false;

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 8,
            Padding     = new Padding(12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var header = new Label
        {
            AutoSize  = true,
            Text      = $"Tick the company codes {user.SamAccountName} should see:",
            Margin    = new Padding(0, 0, 0, 6)
        };
        root.Controls.Add(header, 0, 0);

        var policy = new Label
        {
            AutoSize  = true,
            MaximumSize = new Size(510, 0),
            Margin    = new Padding(0, 0, 0, 8),
            Text      = "Users with no company-code groups are unrestricted and see every " +
                        "company code. Assigning one or more codes limits that user to the " +
                        "checked list. Changes apply at next logon."
        };
        root.Controls.Add(policy, 0, 1);

        _list = new CheckedListBox
        {
            CheckOnClick   = true,
            IntegralHeight = false,
            Dock           = DockStyle.Fill,
            Margin         = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(_list, 0, 2);

        _lblStatus = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(510, 0),
            Font     = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin   = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(_lblStatus, 0, 3);

        var defaultRow = new TableLayoutPanel
        {
            AutoSize     = true,
            Dock         = DockStyle.Fill,
            ColumnCount  = 2,
            RowCount     = 1,
            Margin       = new Padding(0, 0, 0, 8)
        };
        defaultRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        defaultRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        var lblDefault = new Label
        {
            AutoSize = true,
            Text     = "Default company code:",
            Anchor   = AnchorStyles.Left,
            Margin   = new Padding(0, 6, 8, 0)
        };
        _cmbDefault = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock          = DockStyle.Fill,
            Margin        = new Padding(0, 2, 0, 2)
        };
        defaultRow.Controls.Add(lblDefault, 0, 0);
        defaultRow.Controls.Add(_cmbDefault, 1, 0);
        root.Controls.Add(defaultRow, 0, 4);

        var addRow = new TableLayoutPanel
        {
            AutoSize    = true,
            Dock        = DockStyle.Fill,
            ColumnCount = 3,
            RowCount    = 1,
            Margin      = new Padding(0, 0, 0, 8)
        };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lblNew = new Label
        {
            AutoSize = true,
            Text     = "Add company code:",
            Anchor   = AnchorStyles.Left,
            Margin   = new Padding(0, 6, 8, 0)
        };
        _txtNew = new TextBox
        {
            Dock   = DockStyle.Fill,
            Margin = new Padding(0, 2, 8, 2)
        };
        var btnAdd = new Button
        {
            Text   = "Add",
            Width  = 80,
            Height = 28,
            Margin = new Padding(0, 0, 0, 0)
        };
        addRow.Controls.Add(lblNew, 0, 0);
        addRow.Controls.Add(_txtNew, 1, 0);
        addRow.Controls.Add(btnAdd, 2, 0);
        root.Controls.Add(addRow, 0, 5);

        var buttonBar = new FlowLayoutPanel
        {
            AutoSize      = true,
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin        = new Padding(0)
        };
        var btnCancel = new Button
        {
            Text         = "Cancel",
            Width        = 90,
            Height       = 32,
            DialogResult = DialogResult.Cancel,
            Margin       = new Padding(8, 0, 0, 0)
        };
        var btnApply = new Button
        {
            Text      = "Apply",
            Width     = 90,
            Height    = 32,
            Margin    = new Padding(0)
        };
        buttonBar.Controls.Add(btnCancel);
        buttonBar.Controls.Add(btnApply);
        root.Controls.Add(buttonBar, 0, 6);

        AcceptButton = btnApply;
        CancelButton = btnCancel;

        _initialCodes = new HashSet<string>(CompanyCodeHelper.CodesFromUser(user),
                                            StringComparer.OrdinalIgnoreCase);
        _initialPrimary = ReadPrimary();

        ReloadCodes(select: _initialCodes);
        if (!string.IsNullOrWhiteSpace(_initialPrimary))
            TrySelectDefault(_initialPrimary!);

        _list.ItemCheck += (_, e) =>
            BeginInvoke(new Action(SyncDefaultAndStatus));
        _cmbDefault.SelectedIndexChanged += (_, _) => UpdateStatus();
        btnAdd.Click += (_, _) => OnAddCode();
        _txtNew.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OnAddCode();
            }
        };
        btnApply.Click += OnApply;

        Load += (_, _) =>
        {
            ThemeManager.ApplyTo(this);
            SyncDefaultAndStatus();
        };
    }

    private string? ReadPrimary()
    {
        try
        {
            return _ad.GetCompanyCodePrimary(_user.SamAccountName);
        }
        catch
        {
            return null;
        }
    }

    private void ReloadCodes(IEnumerable<string>? select)
    {
        var selectSet = new HashSet<string>(
            select ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var codes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _ad.GetAllSecurityGroupNamesUnderUsersContainer())
        {
            string? code = CompanyCodeHelper.CodeFromGroupName(name);
            if (!string.IsNullOrWhiteSpace(code)) codes.Add(code!);
        }
        foreach (var code in CompanyCodeHelper.CodesFromUser(_user))
            codes.Add(code);

        _list.Items.Clear();
        foreach (var code in codes)
        {
            int i = _list.Items.Add(code);
            if (selectSet.Contains(code)) _list.SetItemChecked(i, true);
        }
    }

    private List<string> CheckedCodes()
    {
        return _list.CheckedItems.Cast<object>()
                    .Select(o => (o?.ToString() ?? string.Empty).Trim().ToUpperInvariant())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    private void SyncDefaultAndStatus()
    {
        var want = CheckedCodes();
        string? previous = _cmbDefault.SelectedItem?.ToString();

        _cmbDefault.BeginUpdate();
        _cmbDefault.Items.Clear();
        foreach (var code in want)
            _cmbDefault.Items.Add(code);
        _cmbDefault.Enabled = want.Count > 0;

        if (want.Count > 0)
        {
            int idx = 0;
            if (!string.IsNullOrWhiteSpace(previous))
            {
                for (int i = 0; i < _cmbDefault.Items.Count; i++)
                {
                    if (string.Equals(_cmbDefault.Items[i]?.ToString(), previous,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
            }
            _cmbDefault.SelectedIndex = idx;
        }
        _cmbDefault.EndUpdate();
        UpdateStatus();
    }

    private void TrySelectDefault(string code)
    {
        for (int i = 0; i < _cmbDefault.Items.Count; i++)
        {
            if (string.Equals(_cmbDefault.Items[i]?.ToString(), code,
                              StringComparison.OrdinalIgnoreCase))
            {
                _cmbDefault.SelectedIndex = i;
                return;
            }
        }
    }

    private void UpdateStatus()
    {
        var want = CheckedCodes();
        if (want.Count == 0)
        {
            _lblStatus.Text = "Unrestricted — this user will see every company code.";
            _lblStatus.ForeColor = Constants.GssGreen;
        }
        else
        {
            string primary = _cmbDefault.SelectedItem?.ToString() ?? want[0];
            _lblStatus.Text = $"Restricted to {want.Count} company code(s). Default: {primary}.";
            _lblStatus.ForeColor = Constants.GssOrange;
        }
    }

    private void OnAddCode()
    {
        if (!CompanyCodeHelper.TryNormalizeCode(_txtNew.Text, out string code, out string error))
        {
            MessageBox.Show(this, error, "Add Company Code",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _txtNew.SelectAll();
            _txtNew.Focus();
            return;
        }

        string groupName = CompanyCodeHelper.GroupNameForCode(code);
        try
        {
            _ad.CreateSecurityGroup(groupName,
                $"Global Shop company code {code}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not create group '{groupName}':\r\n{ex.Message}",
                "Add Company Code",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var keepChecked = CheckedCodes();
        if (!keepChecked.Contains(code, StringComparer.OrdinalIgnoreCase))
            keepChecked.Add(code);

        ReloadCodes(keepChecked);
        SyncDefaultAndStatus();
        TrySelectDefault(code);
        _txtNew.Clear();
        _txtNew.Focus();
    }

    private void OnApply(object? sender, EventArgs e)
    {
        var want = new HashSet<string>(CheckedCodes(), StringComparer.OrdinalIgnoreCase);
        var toAdd    = want.Except(_initialCodes, StringComparer.OrdinalIgnoreCase).ToList();
        var toRemove = _initialCodes.Except(want, StringComparer.OrdinalIgnoreCase).ToList();
        string? primary = want.Count == 0
            ? null
            : (_cmbDefault.SelectedItem?.ToString() ?? want.First());

        bool primaryChanged = !string.Equals(primary ?? string.Empty,
                                             _initialPrimary ?? string.Empty,
                                             StringComparison.OrdinalIgnoreCase);

        if (toAdd.Count == 0 && toRemove.Count == 0 && !primaryChanged)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        var errors = new List<string>();

        foreach (var code in toAdd)
        {
            string g = CompanyCodeHelper.GroupNameForCode(code);
            try { _ad.AddUserToGroup(_user.SamAccountName, g); }
            catch (Exception ex) { errors.Add($"Add '{g}': {ex.Message}"); }
        }
        foreach (var code in toRemove)
        {
            string g = CompanyCodeHelper.GroupNameForCode(code);
            try { _ad.RemoveUserFromGroup(_user.SamAccountName, g); }
            catch (Exception ex) { errors.Add($"Remove '{g}': {ex.Message}"); }
        }

        try
        {
            _ad.SetCompanyCodePrimary(_user.SamAccountName, primary);
        }
        catch (Exception ex)
        {
            errors.Add($"Default company code: {ex.Message}");
        }

        AuditLogger.Write("CompanyCodes", _user.SamAccountName,
            want.Count == 0
                ? "unrestricted (no company-code groups)"
                : $"+{string.Join(",", toAdd)}; -{string.Join(",", toRemove)}; default={primary}");

        // Best-effort: if this user's Windows profile exists on THIS machine,
        // update GSSLOGIN.DIR now. The GPO logon script is the real delivery
        // path, so a missing local profile is normal and is not shown.
        try { CompanyCodeLoginFile.Apply(_user.SamAccountName, want, primary); }
        catch { /* GPO logon script writes the file at next sign-in */ }

        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                "Some changes failed:\r\n\r\n" + string.Join("\r\n", errors),
                "Company Codes",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
