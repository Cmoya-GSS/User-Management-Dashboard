using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using UserDashboard.Models;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Bulk add / remove / clear company-code group membership for the selected
/// users. Clearing all company-code groups leaves those users unrestricted.
/// </summary>
internal sealed class BulkCompanyCodesForm : Form
{
    private readonly AdService _ad;
    private readonly List<AdUserInfo> _users;
    private readonly CheckedListBox _list;
    private readonly RadioButton _rbAdd, _rbRemove, _rbClear;
    private readonly TextBox _txtNew;

    public List<string> ChangedUsers { get; } = new();

    public BulkCompanyCodesForm(AdService ad, List<AdUserInfo> users)
    {
        _ad    = ad;
        _users = users;

        DpiHelper.ConfigureForm(this);
        Text            = "Bulk Company Codes";
        Size            = new Size(560, 520);
        MinimumSize     = new Size(480, 440);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        Font            = new Font("Segoe UI", 9f);
        ShowInTaskbar   = false;

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 6,
            Padding     = new Padding(12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var header = new Label
        {
            AutoSize = true,
            Text     = $"Apply company-code changes to {_users.Count} selected user(s):",
            Margin   = new Padding(0, 0, 0, 6)
        };
        root.Controls.Add(header, 0, 0);

        var policy = new Label
        {
            AutoSize    = true,
            MaximumSize = new Size(510, 0),
            Margin      = new Padding(0, 0, 0, 8),
            Text        = "Users with no company-code groups stay unrestricted and see every " +
                          "company code. Use Clear to put selected users back in that state."
        };
        root.Controls.Add(policy, 0, 1);

        var radios = new FlowLayoutPanel
        {
            AutoSize      = true,
            FlowDirection = FlowDirection.TopDown,
            Dock          = DockStyle.Fill,
            Margin        = new Padding(0, 0, 0, 8),
            WrapContents  = false
        };
        _rbAdd = new RadioButton
        {
            Text     = "ADD the selected users to the checked company codes",
            AutoSize = true,
            Checked  = true,
            Margin   = new Padding(0, 0, 0, 2)
        };
        _rbRemove = new RadioButton
        {
            Text     = "REMOVE the selected users from the checked company codes",
            AutoSize = true,
            Margin   = new Padding(0, 0, 0, 2)
        };
        _rbClear = new RadioButton
        {
            Text     = "CLEAR all company-code groups (unrestricted — see every code)",
            AutoSize = true,
            Margin   = new Padding(0)
        };
        radios.Controls.Add(_rbAdd);
        radios.Controls.Add(_rbRemove);
        radios.Controls.Add(_rbClear);
        root.Controls.Add(radios, 0, 2);

        _list = new CheckedListBox
        {
            CheckOnClick   = true,
            IntegralHeight = false,
            Dock           = DockStyle.Fill,
            Margin         = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(_list, 0, 3);

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
            Height = 28
        };
        addRow.Controls.Add(lblNew, 0, 0);
        addRow.Controls.Add(_txtNew, 1, 0);
        addRow.Controls.Add(btnAdd, 2, 0);
        root.Controls.Add(addRow, 0, 4);

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
            Text   = "Apply",
            Width  = 90,
            Height = 32
        };
        buttonBar.Controls.Add(btnCancel);
        buttonBar.Controls.Add(btnApply);
        root.Controls.Add(buttonBar, 0, 5);

        AcceptButton = btnApply;
        CancelButton = btnCancel;

        ReloadCodes();

        _rbClear.CheckedChanged += (_, _) =>
        {
            _list.Enabled   = !_rbClear.Checked;
            _txtNew.Enabled = !_rbClear.Checked;
            btnAdd.Enabled  = !_rbClear.Checked;
        };
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

        Load += (_, _) => ThemeManager.ApplyTo(this);
    }

    private void ReloadCodes(string? ensureChecked = null)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int i in _list.CheckedIndices)
        {
            string? s = _list.Items[i]?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) existing.Add(s!);
        }
        if (!string.IsNullOrWhiteSpace(ensureChecked))
            existing.Add(ensureChecked!);

        var codes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _ad.GetAllSecurityGroupNamesUnderUsersContainer())
        {
            string? code = CompanyCodeHelper.CodeFromGroupName(name);
            if (!string.IsNullOrWhiteSpace(code)) codes.Add(code!);
        }
        foreach (var u in _users)
            foreach (var code in CompanyCodeHelper.CodesFromUser(u))
                codes.Add(code);

        _list.Items.Clear();
        foreach (var code in codes)
        {
            int i = _list.Items.Add(code);
            if (existing.Contains(code)) _list.SetItemChecked(i, true);
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

        ReloadCodes(code);
        _txtNew.Clear();
        _txtNew.Focus();
    }

    private List<string> CheckedCodes()
    {
        return _list.CheckedItems.Cast<object>()
                    .Select(o => (o?.ToString() ?? string.Empty).Trim().ToUpperInvariant())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    private void OnApply(object? sender, EventArgs e)
    {
        bool clearing = _rbClear.Checked;
        bool adding   = _rbAdd.Checked;
        var codes     = CheckedCodes();

        if (!clearing && codes.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one company code first.",
                "Bulk Company Codes", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string prompt = clearing
            ? $"CLEAR all company-code groups for {_users.Count} user(s)?\r\n\r\n" +
              "They will be unrestricted and see every company code at next logon."
            : $"{(adding ? "ADD" : "REMOVE")} {_users.Count} user(s) " +
              $"{(adding ? "to" : "from")} {codes.Count} company code(s):\r\n\r\n  " +
              string.Join(", ", codes) + "\r\n\r\nProceed?";

        var res = MessageBox.Show(this, prompt, "Bulk Company Codes",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (res != DialogResult.Yes) return;

        var errors = new List<string>();
        int changes = 0, skipped = 0;

        foreach (var u in _users)
        {
            var current = new HashSet<string>(CompanyCodeHelper.CodesFromUser(u),
                                              StringComparer.OrdinalIgnoreCase);
            bool userChanged = false;

            IEnumerable<string> targets = clearing ? current.ToList() : codes;
            foreach (var code in targets)
            {
                bool isMember = current.Contains(code);
                if (!clearing && adding && isMember)    { skipped++; continue; }
                if (!clearing && !adding && !isMember)  { skipped++; continue; }
                if (clearing && !isMember)              { skipped++; continue; }

                string g = CompanyCodeHelper.GroupNameForCode(code);
                try
                {
                    if (clearing || !adding) _ad.RemoveUserFromGroup(u.SamAccountName, g);
                    else                     _ad.AddUserToGroup(u.SamAccountName, g);
                    changes++;
                    userChanged = true;
                    if (adding && !clearing) current.Add(code);
                    else                     current.Remove(code);
                }
                catch (Exception ex)
                {
                    errors.Add($"{u.SamAccountName} / '{g}': {ex.Message}");
                }
            }

            if (clearing || (!adding && current.Count == 0))
            {
                try
                {
                    _ad.SetCompanyCodePrimary(u.SamAccountName, null);
                    userChanged = true;
                }
                catch (Exception ex)
                {
                    errors.Add($"{u.SamAccountName} / default: {ex.Message}");
                }
            }
            else if (adding && userChanged)
            {
                try
                {
                    string? existing = null;
                    try
                    {
                        existing = _ad.GetCompanyCodePrimary(u.SamAccountName);
                    }
                    catch { /* treat as unset */ }

                    bool primaryValid = !string.IsNullOrWhiteSpace(existing)
                        && current.Contains(existing!);
                    if (!primaryValid && current.Count > 0)
                    {
                        string primary = current.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                                                .First();
                        _ad.SetCompanyCodePrimary(u.SamAccountName, primary);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{u.SamAccountName} / default: {ex.Message}");
                }
            }

            if (userChanged)
            {
                ChangedUsers.Add(u.SamAccountName);
                string detail = clearing
                    ? "cleared (unrestricted)"
                    : $"{(adding ? "+" : "-")}{string.Join(",", codes)} (bulk)";
                AuditLogger.Write("BulkCompanyCodes", u.SamAccountName, detail);

                try
                {
                    string? primary = current.Count == 0
                        ? null
                        : _ad.GetCompanyCodePrimary(u.SamAccountName);
                    if (!string.IsNullOrWhiteSpace(primary) &&
                        !current.Contains(primary!))
                        primary = null;
                    CompanyCodeLoginFile.Apply(u.SamAccountName, current, primary);
                }
                catch
                {
                    /* profile write is best-effort; AD membership is already saved */
                }
            }
        }

        string summary = $"{changes} change(s) applied, {skipped} no-op(s) skipped.";
        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                summary + "\r\n\r\nSome changes failed:\r\n\r\n" + string.Join("\r\n", errors),
                "Bulk Company Codes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show(this, summary, "Bulk Company Codes",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
