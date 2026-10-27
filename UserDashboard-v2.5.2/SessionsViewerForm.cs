using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Read-only viewer of every active/disconnected RDS session across all
/// discovered session hosts, with a delegated logoff action. Uses the same
/// WTS query + RDP-Tcp listener permission as the per-user logoff, so it
/// works for non-admin operators.
/// </summary>
internal sealed class SessionsViewerForm : Form
{
    private readonly RdsService _rds;
    private readonly DataGridView _grid;
    private readonly Button _btnRefresh, _btnLogoff, _btnClose;
    private readonly Label _lblStatus;
    private List<RdsSession> _sessions = new();

    public SessionsViewerForm(RdsService rds)
    {
        _rds = rds;

        DpiHelper.ConfigureForm(this);
        Text          = "Active RDS Sessions";
        var work      = DpiHelper.PrimaryWorkingArea;
        int formW     = Math.Min(820, work.Width - 40);
        int formH     = Math.Min(480, work.Height - 80);
        Size          = new Size(Math.Max(formW, 640), Math.Max(formH, 360));
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize   = new Size(Math.Max(Math.Min(640, formW), 480), Math.Max(Math.Min(340, formH), 300));

        _grid = new DataGridView
        {
            Location            = new Point(10, 10),
            Size                = new Size(784, 360),
            Anchor              = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ReadOnly            = true,
            AllowUserToAddRows  = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible   = false,
            SelectionMode       = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect         = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            CellBorderStyle     = DataGridViewCellBorderStyle.SingleHorizontal
        };
        _grid.Columns.Add("User",    "User");
        _grid.Columns.Add("Host",    "Session Host");
        _grid.Columns.Add("Id",      "Session ID");
        _grid.Columns.Add("State",   "State");
        _grid.Columns.Add("Idle",    "Idle Time");
        _grid.Columns["Id"].FillWeight = 40; _grid.Columns["State"].FillWeight = 55;
        _grid.Columns["Idle"].FillWeight = 55;
        _grid.Columns["Id"].ValueType   = typeof(int);
        _grid.Columns["Idle"].ValueType = typeof(long);
        _grid.Columns["Idle"].SortMode  = DataGridViewColumnSortMode.Automatic;
        _grid.SortCompare    += OnGridSortCompare;
        _grid.CellFormatting += OnGridCellFormatting;
        Controls.Add(_grid);

        _lblStatus = new Label
        {
            AutoSize = true,
            Location = new Point(12, 382),
            Anchor   = AnchorStyles.Bottom | AnchorStyles.Left,
            Text     = ""
        };
        Controls.Add(_lblStatus);

        _btnRefresh = new Button
        {
            Text = "Refresh", Width = 90,
            Location = new Point(10, 405),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            BackColor = Constants.GssOrange, ForeColor = Color.White, FlatStyle = FlatStyle.Flat
        };
        _btnLogoff = new Button
        {
            Text = "Log Off Selected...", Width = 140,
            Location = new Point(110, 405),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            BackColor = Constants.GssGreen, ForeColor = Color.White, FlatStyle = FlatStyle.Flat
        };
        _btnClose = new Button
        {
            Text = "Close", Width = 90,
            Location = new Point(704, 405),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(_btnRefresh);
        Controls.Add(_btnLogoff);
        Controls.Add(_btnClose);
        CancelButton = _btnClose;

        _btnRefresh.Click += async (_, _) => await LoadSessionsAsync();
        _btnLogoff.Click  += async (_, _) => await LogoffSelectedAsync();

        Load += async (_, _) =>
        {
            ThemeManager.ApplyTo(this);
            var p = ThemeManager.Current;
            _grid.BackgroundColor = p.GridBack;
            _grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor          = p.GridBack,
                ForeColor          = p.GridFore,
                SelectionBackColor = p.GridSelectionBack,
                SelectionForeColor = p.GridSelectionFore,
            };
            _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor          = p.GridAlternate,
                ForeColor          = p.GridFore,
                SelectionBackColor = p.GridSelectionBack,
                SelectionForeColor = p.GridSelectionFore,
            };
            await LoadSessionsAsync();
        };
    }

    /// <summary>
    /// Idle Time displays as "6h 31m" / "9m" but must sort as a duration.
    /// Session ID is numeric.
    /// </summary>
    private void OnGridSortCompare(object? sender, DataGridViewSortCompareEventArgs e)
    {
        if (e.Column.Name == "Idle" || e.Column.HeaderText == "Idle Time")
        {
            RdsSession? row1 = e.RowIndex1 >= 0 ? _grid.Rows[e.RowIndex1].Tag as RdsSession : null;
            RdsSession? row2 = e.RowIndex2 >= 0 ? _grid.Rows[e.RowIndex2].Tag as RdsSession : null;
            e.SortResult = RdsSession.CompareIdleCells(e.CellValue1, e.CellValue2, row1, row2);
            e.Handled = true;
            return;
        }

        if (e.Column.Name == "Id")
        {
            int.TryParse(Convert.ToString(e.CellValue1), out int id1);
            int.TryParse(Convert.ToString(e.CellValue2), out int id2);
            e.SortResult = id1.CompareTo(id2);
            e.Handled = true;
        }
    }

    private void OnGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Columns[e.ColumnIndex].Name != "Idle") return;
        e.Value = RdsSession.FormatIdleCell(e.Value);
        e.FormattingApplied = true;
    }

    private async Task LoadSessionsAsync()
    {
        _btnRefresh.Enabled = _btnLogoff.Enabled = false;
        _lblStatus.Text = "Querying session hosts...";
        try
        {
            _sessions = await Task.Run(() => _rds.GetAllSessions());
            if (IsDisposed || Disposing) return;   // window closed mid-query
            _grid.Rows.Clear();
            foreach (var s in _sessions.OrderBy(x => x.HostServer, StringComparer.OrdinalIgnoreCase)
                                       .ThenBy(x => x.UserName, StringComparer.OrdinalIgnoreCase))
            {
                bool prot = RdsService.IsProtectedAccount(s.UserName);
                int idx = _grid.Rows.Add(
                    prot ? s.QualifiedUser + "  [PROTECTED]" : s.QualifiedUser,
                    s.HostServer, s.UnifiedSessionId, s.FriendlyState, s.IdleSortTicks);
                _grid.Rows[idx].Tag = s;
                if (prot)
                {
                    // Soft tint + italic when idle; high-contrast selection colors
                    // so the blue/green highlight stays readable (gray-on-blue was not).
                    var p = ThemeManager.Current;
                    var style = _grid.Rows[idx].DefaultCellStyle;
                    style.BackColor          = ThemeManager.IsDark
                        ? Color.FromArgb(55, 50, 40)
                        : Color.FromArgb(255, 247, 230);
                    style.ForeColor          = p.GridFore;
                    style.SelectionBackColor = p.GridSelectionBack;
                    style.SelectionForeColor = p.GridSelectionFore;
                    style.Font               = new Font(_grid.Font, FontStyle.Italic);
                }
            }
            _lblStatus.Text = $"{_sessions.Count} session(s) across " +
                              $"{_sessions.Select(x => x.HostServer).Distinct(StringComparer.OrdinalIgnoreCase).Count()} host(s).";
        }
        catch (RdsAccessDeniedException ex)
        {
            if (IsDisposed || Disposing) return;
            _lblStatus.Text = "Access denied querying session hosts.";
            MessageBox.Show(this, ex.Message, "Active RDS Sessions",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (ObjectDisposedException)
        {
            // Window was closed while the query was in flight - nothing to do.
        }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing) return;
            _lblStatus.Text = "Query failed.";
            MessageBox.Show(this, "Failed to query sessions:\r\n" + ex.Message,
                "Active RDS Sessions", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
                _btnRefresh.Enabled = _btnLogoff.Enabled = true;
        }
    }

    private async Task LogoffSelectedAsync()
    {
        var targets = _grid.SelectedRows.Cast<DataGridViewRow>()
                           .Select(r => r.Tag as RdsSession)
                           .Where(s => s != null)
                           .Cast<RdsSession>()
                           .ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Select one or more sessions first.",
                "Active RDS Sessions", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Protected accounts (Administrator, GLOBALSHOP, ...) cannot be logged
        // off from the dashboard. Drop them here with a clear message; the
        // RdsService backstop would refuse them anyway.
        var blocked = targets.Where(s => RdsService.IsProtectedAccount(s.UserName)).ToList();
        if (blocked.Count > 0)
        {
            targets = targets.Where(s => !RdsService.IsProtectedAccount(s.UserName)).ToList();
            MessageBox.Show(this,
                "These sessions are PROTECTED and will not be logged off:\r\n\r\n" +
                string.Join("\r\n", blocked.Select(s => $"  • {s.QualifiedUser} on {s.HostServer}")) +
                (targets.Count > 0 ? "\r\n\r\nThe remaining selection will proceed." : ""),
                "Protected Accounts", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (targets.Count == 0) return;
        }

        var preview = string.Join("\r\n",
            targets.Take(10).Select(s => $"  • {s.QualifiedUser}  on {s.HostServer} (session {s.UnifiedSessionId})"));
        if (targets.Count > 10) preview += $"\r\n  ...and {targets.Count - 10} more";

        var res = MessageBox.Show(this,
            $"Log off {targets.Count} session(s)?\r\n\r\n{preview}\r\n\r\n" +
            "Users will lose unsaved work in those sessions.",
            "Confirm Log Off", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (res != DialogResult.Yes) return;

        _btnRefresh.Enabled = _btnLogoff.Enabled = false;
        int ok = 0, fail = 0;
        try
        {
            foreach (var s in targets)
            {
                var result = await Task.Run(() => _rds.LogoffSession(s));
                if (IsDisposed || Disposing) return;
                if (result.Success)
                {
                    ok++;
                    AuditLogger.Write("SessionLogoff", s.QualifiedUser,
                        $"Session {s.UnifiedSessionId} on {s.HostServer} (sessions viewer)");
                }
                else
                {
                    fail++;
                    AuditLogger.Write("SessionLogoffFailed", s.QualifiedUser,
                        $"Session {s.UnifiedSessionId} on {s.HostServer}: {result.Error}");
                }
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing)
                _btnRefresh.Enabled = _btnLogoff.Enabled = true;
        }

        if (IsDisposed || Disposing) return;
        _lblStatus.Text = $"Logoff complete: {ok} succeeded, {fail} failed.";
        await LoadSessionsAsync();
    }
}
