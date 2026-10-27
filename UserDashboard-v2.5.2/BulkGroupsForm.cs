using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using UserDashboard.Models;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Bulk group-membership dialog: applies ADD or REMOVE of the checked security
/// groups to every selected user. No-ops (user already in / not in a group)
/// are skipped silently, so re-running is always safe.
/// </summary>
internal sealed class BulkGroupsForm : Form
{
    private readonly AdService _ad;
    private readonly List<AdUserInfo> _users;
    private readonly CheckedListBox _list;
    private readonly RadioButton _rbAdd, _rbRemove;

    /// <summary>SAM names whose membership was actually changed (for the caller to refresh).</summary>
    public List<string> ChangedUsers { get; } = new();

    public BulkGroupsForm(AdService ad, List<AdUserInfo> users, IEnumerable<string> availableGroups)
    {
        _ad    = ad;
        _users = users;

        DpiHelper.ConfigureForm(this);
        Text          = "Bulk Manage Security Groups";
        Size          = new Size(560, 470);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox   = false;
        MinimizeBox   = false;

        var lbl = new Label
        {
            AutoSize = true,
            Text     = $"Apply group changes to {_users.Count} selected user(s):",
            Location = new Point(10, 10)
        };
        Controls.Add(lbl);

        _rbAdd = new RadioButton
        {
            Text     = "ADD the selected users to the checked groups",
            AutoSize = true,
            Checked  = true,
            Location = new Point(12, 36)
        };
        _rbRemove = new RadioButton
        {
            Text     = "REMOVE the selected users from the checked groups",
            AutoSize = true,
            Location = new Point(12, 60)
        };
        Controls.Add(_rbAdd);
        Controls.Add(_rbRemove);

        _list = new CheckedListBox
        {
            CheckOnClick   = true,
            IntegralHeight = false,
            Location       = new Point(10, 90),
            Size           = new Size(522, 290)
        };
        Controls.Add(_list);

        var avail = availableGroups
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Where(g => !Constants.ExcludedGroups.Contains(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray();
        _list.Items.AddRange(avail);

        var btnApply  = new Button { Text = "Apply",  Location = new Point(350, 395), Width = 80 };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(450, 395), Width = 80,
                                     DialogResult = DialogResult.Cancel };
        Controls.Add(btnApply);
        Controls.Add(btnCancel);
        AcceptButton = btnApply;
        CancelButton = btnCancel;

        btnApply.Click += OnApply;

        Load += (_, _) => ThemeManager.ApplyTo(this);
    }

    private void OnApply(object? sender, EventArgs e)
    {
        var groups = _list.CheckedItems.Cast<object>()
                          .Select(o => (o?.ToString() ?? string.Empty).Trim())
                          .Where(s => s.Length > 0)
                          .ToList();
        if (groups.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one group first.",
                "Bulk Manage Groups", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool adding = _rbAdd.Checked;
        string verb = adding ? "ADD" : "REMOVE";
        string prep = adding ? "to" : "from";

        var res = MessageBox.Show(this,
            $"{verb} {_users.Count} user(s) {prep} {groups.Count} group(s):\r\n\r\n" +
            "  " + string.Join(", ", groups) + "\r\n\r\nProceed?",
            $"Bulk {verb}", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (res != DialogResult.Yes) return;

        if (adding && groups.Any(g => g.Equals(Constants.UserDashboardAccessGroup, StringComparison.OrdinalIgnoreCase)))
        {
            var toAddToUd = _users
                .Where(u => !UserIsMemberOf(u, Constants.UserDashboardAccessGroup))
                .Select(u => u.SamAccountName)
                .ToList();
            if (toAddToUd.Count > 0)
            {
                using var confirm = new UserDashboardGroupConfirmForm(toAddToUd);
                if (confirm.ShowDialog(this) != DialogResult.OK)
                    return;
            }
        }

        var errors = new List<string>();
        int changes = 0, skipped = 0;

        foreach (var u in _users)
        {
            // Current memberships as CNs, for no-op detection.
            var memberCns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dn in u.MemberOf)
            {
                string cn = AdService.ExtractCn(dn);
                if (!string.IsNullOrWhiteSpace(cn)) memberCns.Add(cn);
            }

            bool userChanged = false;
            foreach (var g in groups)
            {
                bool isMember = memberCns.Contains(g);
                if (adding && isMember)   { skipped++; continue; }   // already in
                if (!adding && !isMember) { skipped++; continue; }   // already out

                try
                {
                    if (adding) _ad.AddUserToGroup(u.SamAccountName, g);
                    else        _ad.RemoveUserFromGroup(u.SamAccountName, g);
                    changes++;
                    userChanged = true;
                }
                catch (Exception ex)
                {
                    errors.Add($"{u.SamAccountName} / '{g}': {ex.Message}");
                }
            }

            if (userChanged)
            {
                ChangedUsers.Add(u.SamAccountName);
                AuditLogger.Write("BulkManageGroups", u.SamAccountName,
                    $"{(adding ? "+" : "-")}{string.Join(",", groups)} (bulk)");
            }
        }

        string summary = $"{changes} change(s) applied, {skipped} no-op(s) skipped.";
        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                summary + "\r\n\r\nSome changes failed:\r\n\r\n" + string.Join("\r\n", errors),
                "Bulk Manage Groups", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show(this, summary, "Bulk Manage Groups",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool UserIsMemberOf(AdUserInfo user, string groupCn)
    {
        foreach (var dn in user.MemberOf)
        {
            string cn = AdService.ExtractCn(dn);
            if (cn.Equals(groupCn, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
