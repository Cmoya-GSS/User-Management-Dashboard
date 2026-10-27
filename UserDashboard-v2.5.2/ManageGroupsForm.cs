using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using UserDashboard.Models;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Group-membership management dialog. Lists every security group the
/// dashboard considers "manageable" and pre-checks the ones the user is
/// currently a member of. Mirrors $miManageGrp.Add_Click in the PS file.
/// </summary>
internal sealed class ManageGroupsForm : Form
{
    private readonly AdService _ad;
    private readonly AdUserInfo _user;
    private readonly CheckedListBox _list;
    private readonly HashSet<string> _initiallyChecked;

    public ManageGroupsForm(AdService ad, AdUserInfo user, IEnumerable<string> availableGroups)
    {
        _ad   = ad;
        _user = user;

        DpiHelper.ConfigureForm(this);
        Text          = "Manage Security Groups";
        Size          = new Size(540, 370);
        StartPosition = FormStartPosition.CenterParent;

        var lbl = new Label
        {
            AutoSize = true,
            Text     = $"Tick the groups that {user.SamAccountName} should belong to:",
            Location = new Point(10, 10)
        };
        Controls.Add(lbl);

        _list = new CheckedListBox
        {
            CheckOnClick   = true,
            IntegralHeight = false,
            Location       = new Point(10, 40),
            Size           = new Size(500, 240)
        };
        Controls.Add(_list);

        // Available groups, filtered against the excluded list.
        var avail = availableGroups
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Where(g => !Constants.ExcludedGroups.Contains(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _list.Items.AddRange(avail);

        // Pre-check current memberships.
        _initiallyChecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dn in user.MemberOf)
        {
            string cn = AdService.ExtractCn(dn);
            if (string.IsNullOrWhiteSpace(cn)) continue;
            if (Constants.ExcludedGroups.Contains(cn)) continue;
            _initiallyChecked.Add(cn);
        }
        for (int i = 0; i < _list.Items.Count; i++)
        {
            var name = _list.Items[i]?.ToString() ?? string.Empty;
            if (_initiallyChecked.Contains(name)) _list.SetItemChecked(i, true);
        }

        var btnApply  = new Button { Text = "Apply",  Location = new Point(330, 300), Width = 80 };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(430, 300), Width = 80,
                                     DialogResult = DialogResult.Cancel };
        Controls.Add(btnApply);
        Controls.Add(btnCancel);
        AcceptButton = btnApply;
        CancelButton = btnCancel;

        btnApply.Click += OnApply;

        // Apply current theme on Load (after all child controls are attached)
        Load += (_, _) => ThemeManager.ApplyTo(this);
    }

    private void OnApply(object? sender, EventArgs e)
    {
        var want = _list.CheckedItems.Cast<object>()
                                     .Select(o => (o?.ToString() ?? string.Empty).Trim())
                                     .Where(s => !string.IsNullOrEmpty(s))
                                     .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toAdd    = want.Except(_initiallyChecked, StringComparer.OrdinalIgnoreCase).ToList();
        var toRemove = _initiallyChecked.Except(want, StringComparer.OrdinalIgnoreCase).ToList();

        if (toAdd.Any(g => g.Equals(Constants.UserDashboardAccessGroup, StringComparison.OrdinalIgnoreCase)))
        {
            using var confirm = new UserDashboardGroupConfirmForm(new[] { _user.SamAccountName });
            if (confirm.ShowDialog(this) != DialogResult.OK)
                return;
        }

        var errors = new List<string>();

        foreach (var g in toAdd)
        {
            try { _ad.AddUserToGroup(_user.SamAccountName, g); }
            catch (Exception ex) { errors.Add($"Add '{g}': {ex.Message}"); }
        }
        foreach (var g in toRemove)
        {
            try { _ad.RemoveUserFromGroup(_user.SamAccountName, g); }
            catch (Exception ex) { errors.Add($"Remove '{g}': {ex.Message}"); }
        }

        AuditLogger.Write("ManageGroups", _user.SamAccountName,
            $"+{string.Join(",", toAdd)}; -{string.Join(",", toRemove)}");

        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                "Some changes failed:\r\n\r\n" + string.Join("\r\n", errors),
                "Group Membership",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
