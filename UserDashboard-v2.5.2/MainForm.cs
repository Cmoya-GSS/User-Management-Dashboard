using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using UserDashboard.Services;
using UserDashboard.Models;
using UserDashboard.Forms;

namespace UserDashboard;

/// <summary>
/// The main "User Management Dashboard" window.
///
/// v2 enhancements:
///   * Async load with marquee progress bar (UI never freezes)
///   * Multi-select grid + bulk Enable / Disable / Reset Password
///   * Keyboard shortcuts: F5, Ctrl+F, Ctrl+N, Esc, Delete, Enter
///   * Right-click "Copy" submenu (username, display name, email, SAM)
///   * Window position/size and column widths persisted between sessions
/// </summary>
internal sealed class MainForm : Form
{
    // ---------- Services / state ------------------------------------------
    private readonly AdService    _ad;
    private readonly RdsService   _rds;
    private readonly UserSettings _settings;
    private List<AdUserInfo> _allUsers      = new();
    private List<RdsSession> _sessions      = new();
    private List<string>     _validGroups   = new();
    private List<string>     _presentGroups = new();
    private bool _isBusy;       // true while a long-running AD op is on a worker thread
    private bool _suppressAccountViewEvent;
    private bool _gridModeSessions; // true when the grid shows RDS sessions (Logged In tab)

    // ---------- UI controls -----------------------------------------------
    private readonly TextBox  _txtSearch;
    private readonly Button   _btnRefresh, _btnImport, _btnExport, _btnAdd, _btnInfo;
    private readonly Label    _lblGroup, _lblTip;
    private readonly ComboBox _cmbGroup;
    private readonly FlowLayoutPanel _viewTabs;
    private readonly RadioButton _rbViewActive, _rbViewDisabled, _rbViewAll, _rbViewLoggedIn;
    private readonly DataGridView _grid;
    private int _gridTop;
    private readonly StatusStrip  _statusStrip;
    private readonly ToolStripStatusLabel    _statusLabel;
    private readonly ToolStripStatusLabel    _countLabel;
    private readonly ToolStripProgressBar    _progress;
    private readonly Panel _bottomPanel;
    private readonly System.Windows.Forms.Timer _searchTimer;

    // ---------- Context menu ---------------------------------------------
    private readonly ContextMenuStrip _ctx;
    private readonly ToolStripMenuItem _miReset, _miToggleAcct, _miUnlock, _miManageGrp, _miCompanyCodes, _miLogoff;
    private readonly ToolStripMenuItem _miBulkEnable, _miBulkDisable, _miBulkGroups, _miBulkCompanyCodes, _miBulkLogoff;
    private readonly ToolStripMenuItem _miSessionLogoff;
    private readonly ToolStripMenuItem _miCopy;
    private readonly ToolStripMenuItem _miCopyUsername, _miCopyDisplay, _miCopyEmail, _miCopySam;
    private readonly ToolStripSeparator _sepBulk, _sepCopy;

    // ---------- Info dropdown menu ---------------------------------------
    private readonly ContextMenuStrip _infoMenu;
    private readonly ToolStripMenuItem _miAbout, _miShortcuts, _miOpenAuditLog, _miPasswordPage, _miSessions, _miDarkMode;

    public MainForm()
    {
        // -- Services -------------------------------------------------------
        _ad       = new AdService();
        _rds      = new RdsService();
        _settings = UserSettings.Load();
        // NOTE: AuditLogger.Initialize() is deferred to the Shown handler below.
        // The audit logger fires WriteFailed events if it can't write, and those
        // events need the StatusStrip / StatusLabel to be constructed first.

        // -- Form -----------------------------------------------------------
        DpiHelper.ConfigureForm(this);
        Text          = $"User Management Dashboard  v{AppConfig.Version}";
        Size          = DpiHelper.DefaultMainFormSize();
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview    = true;   // so the form sees keystrokes before child controls
        MinimumSize   = DpiHelper.MainFormMinimumSize();

        // Restore persisted window state (size/pos/maximized) before Show
        ApplyPersistedWindowState();

        // -- Status bar (label | spring-spacer | row count | progress bar) --
        _statusLabel = new ToolStripStatusLabel { Text = "Ready", Spring = true,
                                                  TextAlign = ContentAlignment.MiddleLeft };
        _countLabel  = new ToolStripStatusLabel { Text = "" };
        _progress    = new ToolStripProgressBar
        {
            Style   = ProgressBarStyle.Marquee,
            Visible = false,
            Width   = 140
        };
        _statusStrip = new StatusStrip();
        _statusStrip.Items.AddRange(new ToolStripItem[] { _statusLabel, _countLabel, _progress });
        Controls.Add(_statusStrip);

        // -- Bottom action bar (above the status bar) -----------------------
        _bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        Controls.Add(_bottomPanel);

        // -- Top controls ---------------------------------------------------
        Font = new Font("Segoe UI", 9f);

        _txtSearch = new TextBox
        {
            Location    = new Point(12, 14),
            Size        = new Size(300, 30),
            Font        = new Font("Segoe UI", 10f),
            BorderStyle = BorderStyle.FixedSingle,
            Anchor      = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(_txtSearch);
        UiChrome.SetCueBanner(_txtSearch, "Search users, groups, last login\u2026");

        // Debounced search (250ms) so we don't refilter on every keystroke.
        _searchTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
        _txtSearch.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };

        _btnRefresh = NewToolbarButton("\u21BB  Refresh", 318, Constants.GssGreen,   96);
        _btnImport  = NewToolbarButton("Import CSV",     420, Constants.GssGreen,  104);
        _btnExport  = NewToolbarButton("Export to CSV",  530, Constants.GssOrange, 112);
        _btnAdd     = NewToolbarButton("Add User",       648, Constants.GssOrange,  90);
        _btnInfo    = NewToolbarButton("Info \u25BE",    744, Constants.GssOrange,  78);
        Controls.AddRange(new Control[] { _btnRefresh, _btnImport, _btnExport, _btnAdd, _btnInfo });

        _btnImport.Click += OnImportCsv;
        _btnExport.Click += OnExportCsv;
        _btnAdd   .Click += OnAddUser;
        _btnInfo  .Click += OnInfoDropdown;

        // Info dropdown menu (shown when the Info button is clicked)
        _infoMenu        = new ContextMenuStrip();
        _miAbout         = new ToolStripMenuItem("About / Information");
        _miShortcuts     = new ToolStripMenuItem("Keyboard Shortcuts");
        _miOpenAuditLog  = new ToolStripMenuItem("Open Audit Log Folder");
        _miPasswordPage  = new ToolStripMenuItem("End-User Password Page (RD Web)");
        _miSessions      = new ToolStripMenuItem("Active RDS Sessions...");
        _miDarkMode      = new ToolStripMenuItem("Dark Mode") { CheckOnClick = true };
        _infoMenu.Items.Add(_miAbout);
        _infoMenu.Items.Add(_miShortcuts);
        _infoMenu.Items.Add(_miOpenAuditLog);
        _infoMenu.Items.Add(_miPasswordPage);
        _infoMenu.Items.Add(_miSessions);
        _infoMenu.Items.Add(new ToolStripSeparator());
        _infoMenu.Items.Add(_miDarkMode);

        _miAbout.Click             += OnAbout;
        _miShortcuts.Click         += OnShowShortcuts;
        _miOpenAuditLog.Click      += OnOpenAuditLog;
        _miPasswordPage.Click      += OnShowPasswordPage;
        _miSessions.Click          += OnShowSessions;
        _miDarkMode.CheckedChanged += OnDarkModeToggled;

        _lblGroup = new Label { Text = "Group:", AutoSize = true,
                                Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _cmbGroup = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width  = 230,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        Controls.Add(_lblGroup);
        Controls.Add(_cmbGroup);
        _cmbGroup.SelectedIndexChanged += OnGroupComboChanged;

        _lblTip = new Label
        {
            Text      = "Tip: F5 reload  •  Ctrl+F search  •  Ctrl+N add  •  Shift/Ctrl-click for multi-select",
            AutoSize  = true,
            ForeColor = Color.FromArgb(90, 110, 100),
            Font      = new Font("Segoe UI", 8.25f),
            Location  = new Point(12, 50)
        };
        Controls.Add(_lblTip);

        // -- View tabs (account status + live Logged In sessions) -----------
        _viewTabs = new FlowLayoutPanel
        {
            Location      = new Point(10, 72),
            Size          = new Size(620, 32),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            Padding       = new Padding(0),
            Margin        = Padding.Empty,
            Anchor        = AnchorStyles.Top | AnchorStyles.Left
        };
        _rbViewActive    = NewAccountViewTab("Active",     true);
        _rbViewDisabled  = NewAccountViewTab("Disabled",   false);
        _rbViewAll       = NewAccountViewTab("All",        false);
        _rbViewLoggedIn  = NewAccountViewTab("Logged In",  false);
        _viewTabs.Controls.AddRange(new Control[]
        {
            _rbViewActive, _rbViewDisabled, _rbViewAll, _rbViewLoggedIn
        });
        Controls.Add(_viewTabs);
        RestoreAccountView();
        _rbViewActive.CheckedChanged    += OnAccountViewChanged;
        _rbViewDisabled.CheckedChanged  += OnAccountViewChanged;
        _rbViewAll.CheckedChanged       += OnAccountViewChanged;
        _rbViewLoggedIn.CheckedChanged  += OnAccountViewChanged;

        // Wired after the Logged In tab exists so the handler can read it safely.
        _btnRefresh.Click += async (_, _) =>
        {
            if (_isBusy) return;
            if (_rbViewLoggedIn.Checked) await LoadSessionsViewAsync();
            else await LoadUsersAsync();
        };

        // -- Grid -----------------------------------------------------------
        _gridTop               = 110;
        const int bottomMargin = 16;

        _grid = new DataGridView
        {
            Location                    = new Point(12, _gridTop),
            Size                        = new Size(ClientSize.Width - 24,
                                              ClientSize.Height - _gridTop - _statusStrip.Height - _bottomPanel.Height - bottomMargin),
            Anchor                      = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight         = 36,
            SelectionMode               = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect                 = true,                               // bulk actions
            AllowUserToAddRows          = false,
            AllowUserToResizeRows       = false,
            RowHeadersVisible           = false,
            AutoSizeColumnsMode         = DataGridViewAutoSizeColumnsMode.None, // None so per-column widths persist
            BorderStyle                 = BorderStyle.FixedSingle,
            // Horizontal-only borders so a selected row paints as one solid bar
            // instead of white vertical gutters between cells.
            CellBorderStyle             = DataGridViewCellBorderStyle.SingleHorizontal,
            ShowCellToolTips            = true,
            EnableHeadersVisualStyles   = false,
            BackgroundColor             = Color.White
        };
        _grid.RowTemplate.Height = 30;
        Controls.Add(_grid);

        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(0, 130, 68),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            Padding   = new Padding(8, 0, 4, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Constants.AlternatingRowBack
        };
        _grid.DefaultCellStyle.Padding = new Padding(8, 0, 4, 0);
        _grid.DefaultCellStyle.Font    = new Font("Segoe UI", 9f);

        // Columns
        foreach (var col in Constants.AllColumns)
        {
            int idx = _grid.Columns.Add(col, col);
            _grid.Columns[idx].HeaderCell.ToolTipText =
                "Double-click to edit; right-click row(s) for actions";
        }
        // Mark read-only columns visually disabled when idle, but use the same
        // selection colors as editable columns so FullRowSelect paints solidly.
        foreach (var name in Constants.ReadOnlyColumns)
        {
            var col = _grid.Columns[name]!;
            col.ReadOnly = true;
            col.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor          = Constants.ReadonlyColBack,
                ForeColor          = SystemColors.GrayText,
                SelectionBackColor = ThemeManager.Current.GridSelectionBack,
                SelectionForeColor = ThemeManager.Current.GridSelectionFore
            };
        }

        // Restore persisted column widths (or set sensible defaults)
        ApplyPersistedColumnWidths();

        _grid.CellMouseDown    += OnCellMouseDown;
        _grid.CellEndEdit      += OnCellEndEdit;
        _grid.SortCompare      += OnGridSortCompare;
        _grid.CellFormatting   += OnGridCellFormatting;
        _grid.SelectionChanged += (_, _) => UpdateCountLabel();

        // -- Context menu ---------------------------------------------------
        _ctx              = new ContextMenuStrip();
        _miReset          = new ToolStripMenuItem("Reset Password");
        _miToggleAcct     = new ToolStripMenuItem("Disable User");
        _miUnlock         = new ToolStripMenuItem("Unlock Account");
        _miManageGrp      = new ToolStripMenuItem("Manage Security Groups");
        _miCompanyCodes   = new ToolStripMenuItem("Company Codes...");
        _miLogoff         = new ToolStripMenuItem("Log Off RDS Sessions...");
        _miSessionLogoff  = new ToolStripMenuItem("Log Off Selected...");
        _sepBulk          = new ToolStripSeparator();
        _miBulkEnable     = new ToolStripMenuItem("Enable Selected Users");
        _miBulkDisable    = new ToolStripMenuItem("Disable Selected Users");
        _miBulkGroups     = new ToolStripMenuItem("Manage Groups for Selected Users...");
        _miBulkCompanyCodes = new ToolStripMenuItem("Company Codes for Selected Users...");
        _miBulkLogoff     = new ToolStripMenuItem("Log Off Selected Users' Sessions...");
        _sepCopy          = new ToolStripSeparator();
        _miCopy           = new ToolStripMenuItem("Copy");
        _miCopyUsername   = new ToolStripMenuItem("Username (UPN)");
        _miCopyDisplay    = new ToolStripMenuItem("Display Name");
        _miCopyEmail      = new ToolStripMenuItem("Email (UPN)");
        _miCopySam        = new ToolStripMenuItem("SAM Account Name");
        _miCopy.DropDownItems.AddRange(new ToolStripItem[]
        {
            _miCopyUsername, _miCopyDisplay, _miCopyEmail, _miCopySam
        });
        _ctx.Items.AddRange(new ToolStripItem[]
        {
            _miReset, _miToggleAcct, _miUnlock, _miManageGrp, _miCompanyCodes, _miLogoff,
            _miSessionLogoff,
            _sepBulk,
            _miBulkEnable, _miBulkDisable, _miBulkGroups, _miBulkCompanyCodes, _miBulkLogoff,
            _sepCopy,
            _miCopy
        });
        _grid.ContextMenuStrip = _ctx;

        _ctx.Opening      += OnContextMenuOpening;
        _miReset.Click    += OnResetPassword;
        _miToggleAcct.Click += OnToggleAccount;
        _miUnlock.Click     += OnUnlockAccount;
        _miManageGrp.Click  += OnManageGroups;
        _miCompanyCodes.Click += OnCompanyCodes;
        _miLogoff.Click     += (_, _) => OnLogoffSessions();
        _miSessionLogoff.Click += async (_, _) => await LogoffSelectedGridSessionsAsync();
        _miBulkEnable.Click  += (_, _) => OnBulkToggle(enable: true);
        _miBulkDisable.Click += (_, _) => OnBulkToggle(enable: false);
        _miBulkGroups.Click  += OnBulkManageGroups;
        _miBulkCompanyCodes.Click += OnBulkCompanyCodes;
        _miBulkLogoff.Click  += (_, _) => OnLogoffSessions();
        _miCopyUsername.Click += (_, _) => CopySelectedField(u => UpnFor(u));
        _miCopyDisplay.Click  += (_, _) => CopySelectedField(u => u.DisplayName ?? "");
        _miCopyEmail.Click    += (_, _) => CopySelectedField(u => UpnFor(u));
        _miCopySam.Click      += (_, _) => CopySelectedField(u => u.SamAccountName);

        // -- Tooltips -------------------------------------------------------
        var tt = new ToolTip { ShowAlways = true, InitialDelay = 200,
                               ReshowDelay = 100, AutoPopDelay = 8000 };
        tt.SetToolTip(_txtSearch, "Search by name, username, group, or last login.\r\n" +
                                  "Examples:\r\n- smith admin  -> users with BOTH words\r\n" +
                                  "- \"global shop\" -> exact phrase\r\n" +
                                  "Shortcut: Ctrl+F to focus, Esc to clear.");
        tt.SetToolTip(_btnRefresh, "Reload users from Active Directory, or refresh sessions on the Logged In tab (F5)");
        tt.SetToolTip(_btnImport, "Import users from CSV into Active Directory");
        tt.SetToolTip(_btnExport, "Export current view to CSV");
        tt.SetToolTip(_btnAdd,    "Create a new user (Ctrl+N)");
        tt.SetToolTip(_cmbGroup,  "Filter by security group");
        tt.SetToolTip(_rbViewActive,   "Show only enabled accounts (disabled accounts are hidden)");
        tt.SetToolTip(_rbViewDisabled, "Show only disabled accounts");
        tt.SetToolTip(_rbViewAll,      "Show every account, enabled and disabled");
        tt.SetToolTip(_rbViewLoggedIn, "Show users currently logged into RDS session hosts (Active / Disconnected)");
        tt.SetToolTip(_grid,      "Double-click to edit; right-click for actions; F5 to reload");

        // -- Resize / DPI handling ------------------------------------------
        Resize += (_, _) =>
        {
            _grid.Size = new Size(
                ClientSize.Width - 24,
                ClientSize.Height - _gridTop - _statusStrip.Height - _bottomPanel.Height - bottomMargin);
            LayoutTopRightControls();
        };
        DpiChanged += (_, _) =>
        {
            MinimumSize = DpiHelper.MainFormMinimumSize();
            LayoutTopRightControls();
        };

        // -- Form-level keyboard shortcuts ---------------------------------
        KeyDown += OnFormKeyDown;

        // -- Persist window/column state on close --------------------------
        FormClosing += OnFormClosing;

        Shown += async (_, _) =>
        {
            LayoutTopRightControls();

            // Apply persisted theme on first paint
            ThemeManager.SetTheme(_settings.IsDarkMode);
            _miDarkMode.Checked = _settings.IsDarkMode;
            ApplyTheme();
            UpdateViewChrome();

            // Initialize audit log subsystem now that the StatusStrip & menus exist.
            // Any write failure raised here will route to OnAuditWriteFailed which
            // can now safely touch UI controls.
            AuditLogger.WriteFailed += OnAuditWriteFailed;
            AuditLogger.Initialize();

            await LoadUsersAsync();
        };
    }

    // ====================================================================
    //  Settings persistence
    // ====================================================================

    private void ApplyPersistedWindowState()
    {
        // Only restore if all four bounds were saved AND fall within a visible screen.
        if (_settings.WindowX.HasValue && _settings.WindowY.HasValue &&
            _settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
        {
            var bounds = new Rectangle(
                _settings.WindowX.Value, _settings.WindowY.Value,
                _settings.WindowWidth.Value, _settings.WindowHeight.Value);

            // Defensive: if the saved bounds are off-screen (monitor moved/removed),
            // ignore them and let the OS center us.
            bool onScreen = false;
            foreach (var screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(bounds)) { onScreen = true; break; }
            }
            if (onScreen)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds        = DpiHelper.ClampToWorkingArea(bounds);
            }
        }
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    private void ApplyPersistedColumnWidths()
    {
        // First set sensible defaults proportional to the grid width
        int total = Math.Max(_grid.Width - 20, 800);
        var defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { Constants.ColFirstName,      (int)(total * 0.10) },
            { Constants.ColMiddleInitials, (int)(total * 0.06) },
            { Constants.ColLastName,       (int)(total * 0.12) },
            { Constants.ColDisplayName,    (int)(total * 0.18) },
            { Constants.ColUsername,       (int)(total * 0.20) },
            { Constants.ColLastLogin,      (int)(total * 0.12) },
            { Constants.ColSecurityGroups, (int)(total * 0.22) },
        };

        foreach (var col in Constants.AllColumns)
        {
            int? saved = _settings.GetColumnWidth(col);
            if (_grid.Columns[col] != null)
            {
                _grid.Columns[col]!.Width = saved
                    ?? (defaults.TryGetValue(col, out var d) ? d : 100);
            }
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Snapshot the current window placement
        if (WindowState == FormWindowState.Maximized)
        {
            _settings.WindowMaximized = true;
            // Save the *restore* bounds so unmaximizing later returns somewhere sane
            _settings.WindowX      = RestoreBounds.X;
            _settings.WindowY      = RestoreBounds.Y;
            _settings.WindowWidth  = RestoreBounds.Width;
            _settings.WindowHeight = RestoreBounds.Height;
        }
        else if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowMaximized = false;
            _settings.WindowX      = Bounds.X;
            _settings.WindowY      = Bounds.Y;
            _settings.WindowWidth  = Bounds.Width;
            _settings.WindowHeight = Bounds.Height;
        }

        _settings.AccountView = CurrentAccountViewName();

        // Snapshot column widths (user-grid columns only; sessions view is ephemeral)
        if (!_gridModeSessions)
        {
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                if (col?.Name != null) _settings.SetColumnWidth(col.Name, col.Width);
            }
        }

        _settings.Save();
    }

    // ====================================================================
    //  Layout helpers
    // ====================================================================

    // Cached tab fonts - avoid allocating a new Font on every style pass.
    private static readonly Font _tabFont       = new("Segoe UI", 9f, FontStyle.Regular);
    private static readonly Font _tabFontActive = new("Segoe UI", 9f, FontStyle.Bold);

    private static RadioButton NewAccountViewTab(string text, bool isChecked)
    {
        var rb = new RadioButton
        {
            Text        = text,
            AutoSize    = true,
            Appearance  = Appearance.Button,
            FlatStyle   = FlatStyle.Flat,
            Checked     = isChecked,
            Margin      = new Padding(0, 0, 4, 0),
            Padding     = new Padding(12, 6, 12, 6),
            Cursor      = Cursors.Hand,
            Font        = _tabFont
        };
        rb.FlatAppearance.BorderSize = 0;
        rb.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 238, 234);
        rb.FlatAppearance.CheckedBackColor   = Color.Transparent;
        rb.Paint += OnAccountViewTabPaint;
        return rb;
    }

    /// <summary>Draw a brand-green underline under the active view tab.</summary>
    private static void OnAccountViewTabPaint(object? sender, PaintEventArgs e)
    {
        if (sender is not RadioButton { Checked: true } rb) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Constants.GssGreen, 2.5f);
        int y = rb.ClientSize.Height - 2;
        e.Graphics.DrawLine(pen, 8, y, rb.ClientSize.Width - 8, y);
    }

    private string CurrentAccountViewName() =>
        _rbViewLoggedIn.Checked ? "LoggedIn"
      : _rbViewDisabled.Checked ? "Disabled"
      : _rbViewAll.Checked      ? "All"
      : "Active";

    private void RestoreAccountView()
    {
        _suppressAccountViewEvent = true;
        try
        {
            string saved = _settings.AccountView ?? "Active";
            if (string.Equals(saved, "Disabled", StringComparison.OrdinalIgnoreCase))
                _rbViewDisabled.Checked = true;
            else if (string.Equals(saved, "All", StringComparison.OrdinalIgnoreCase))
                _rbViewAll.Checked = true;
            else if (string.Equals(saved, "LoggedIn", StringComparison.OrdinalIgnoreCase))
                _rbViewLoggedIn.Checked = true;
            else
                _rbViewActive.Checked = true;
        }
        finally
        {
            _suppressAccountViewEvent = false;
        }
        StyleAccountViewTabs();
    }

    private async void OnAccountViewChanged(object? sender, EventArgs e)
    {
        if (_suppressAccountViewEvent) return;
        if (sender is RadioButton rb && !rb.Checked) return;

        _settings.AccountView = CurrentAccountViewName();
        _settings.Save();
        StyleAccountViewTabs();
        UpdateViewChrome();

        if (_rbViewLoggedIn.Checked)
            await LoadSessionsViewAsync();
        else
        {
            ConfigureGridForUsers();
            ApplyFilter();
        }
    }

    private void UpdateAccountViewLabels()
    {
        int active   = _allUsers.Count(u => u.Enabled);
        int disabled = _allUsers.Count - active;
        int total    = _allUsers.Count;
        _rbViewActive.Text    = $"Active ({active})";
        _rbViewDisabled.Text  = $"Disabled ({disabled})";
        _rbViewAll.Text       = $"All ({total})";
        _rbViewLoggedIn.Text  = $"Logged In ({_sessions.Count})";
    }

    private void StyleAccountViewTabs()
    {
        var p = ThemeManager.Current;
        Color hover = ThemeManager.IsDark
            ? Color.FromArgb(50, 56, 52)
            : Color.FromArgb(230, 238, 234);

        foreach (var rb in new[] { _rbViewActive, _rbViewDisabled, _rbViewAll, _rbViewLoggedIn })
        {
            rb.FlatAppearance.BorderSize         = 0;
            rb.FlatAppearance.MouseOverBackColor = hover;
            rb.FlatAppearance.CheckedBackColor   = p.PanelBack;
            rb.BackColor = p.PanelBack;

            if (rb.Checked)
            {
                rb.ForeColor = Constants.GssGreen;
                rb.Font      = _tabFontActive;
            }
            else
            {
                rb.ForeColor = ThemeManager.IsDark
                    ? Color.FromArgb(170, 170, 170)
                    : Color.FromArgb(90, 98, 106);
                rb.Font = _tabFont;
            }
        }
    }

    /// <summary>
    /// Enable/disable AD-only chrome when the Logged In sessions tab is active.
    /// </summary>
    private void UpdateViewChrome()
    {
        bool usersView = !_rbViewLoggedIn.Checked;
        if (!_isBusy)
        {
            _btnImport.Enabled = usersView;
            _btnExport.Enabled = usersView;
            _btnAdd.Enabled    = usersView;
            _cmbGroup.Enabled  = usersView;
        }
        _lblGroup.Enabled = usersView;
        _lblTip.Text = usersView
            ? "Tip: F5 reload  •  Ctrl+F search  •  Ctrl+N add  •  Shift/Ctrl-click for multi-select"
            : "Tip: F5 reload sessions  •  Ctrl+F search  •  Right-click to log off selected sessions";
    }

    private static Button NewToolbarButton(string text, int x, Color back, int width)
    {
        var btn = new Button
        {
            Text     = text,
            Location = new Point(x, 12),
            Size     = new Size(width, 32),
            Font     = new Font("Segoe UI", 9f),
            // Anchor right so all toolbar buttons follow the right edge of the form
            // together with Info and the Group dropdown. Combined with the search
            // box being anchored Top|Left|Right, the search box absorbs all the
            // horizontal slack when the window widens and the button cluster stays
            // glued to the right side.
            Anchor   = AnchorStyles.Top | AnchorStyles.Right
        };
        UiChrome.StyleBrandButton(btn, back);
        return btn;
    }

    private void LayoutTopRightControls()
    {
        // Layout strategy:
        //   - The right-side trio (Info button, "Group:" label, Group combo) is
        //     right-anchored, so it normally hugs the right edge.
        //   - As the window narrows, the Group combo shrinks first (down to 80px).
        //   - As a final defensive measure, if the right trio would slide under
        //     the left toolbar (Refresh / Import / Export / Add User), we clamp
        //     it to start just past the left toolbar. MinimumSize keeps the
        //     window from being narrowed enough to actually trigger that clamp.

        const int rightMargin       = 10;
        const int gapAfterLeftBlock = 10;
        const int gapInfoLabel      = 10;
        const int gapLabelCombo     = 6;
        const int comboMaxWidth     = 230;
        const int comboMinWidth     = 80;

        int leftEnd     = _btnAdd.Right;                 // right edge of "Add User"
        int formRight   = ClientSize.Width - rightMargin;

        int infoWidth   = _btnInfo.Width;
        int labelWidth  = _lblGroup.PreferredWidth;

        // Total horizontal space available for the right trio.
        int available = formRight - (leftEnd + gapAfterLeftBlock);

        // Combo gets whatever's left after Info + Label + the gaps,
        // clamped to its allowed range.
        int comboWidth = available - infoWidth - gapInfoLabel - labelWidth - gapLabelCombo;
        if (comboWidth > comboMaxWidth) comboWidth = comboMaxWidth;
        if (comboWidth < comboMinWidth) comboWidth = comboMinWidth;
        _cmbGroup.Width = comboWidth;

        // Position from the right edge inward.
        int comboX = formRight - comboWidth;
        int labelX = comboX - labelWidth - gapLabelCombo;
        int infoX  = labelX - infoWidth - gapInfoLabel;

        // Defensive clamp: if MinimumSize was somehow bypassed (rare DPI quirks)
        // and Info would still overlap Add User, shove the trio right.
        int leftLimit = leftEnd + gapAfterLeftBlock;
        if (infoX < leftLimit)
        {
            int delta = leftLimit - infoX;
            infoX  += delta;
            labelX += delta;
            comboX += delta;
        }

        _btnInfo .Location = new Point(infoX,  12);
        _lblGroup.Location = new Point(labelX, 18);
        _cmbGroup.Location = new Point(comboX, 14);
        _cmbGroup.Height   = 28;
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void UpdateCountLabel()
    {
        int shown    = _grid.Rows.Cast<DataGridViewRow>().Count(r => !r.IsNewRow);
        int selected = _grid.SelectedRows.Count;

        if (_rbViewLoggedIn.Checked)
        {
            int hosts = _sessions.Select(s => s.HostServer)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            string sessionView = selected > 1
                ? $"Showing {shown} of {_sessions.Count} session(s)  •  {selected} selected  •  {hosts} host(s)"
                : $"Showing {shown} of {_sessions.Count} session(s)  •  {hosts} host(s)";
            _countLabel.Text = sessionView;
            return;
        }

        int total    = _allUsers.Count;
        int disabled = _allUsers.Count(u => !u.Enabled);

        string view = selected > 1
            ? $"Showing {shown} of {total}  •  {selected} selected"
            : $"Showing {shown} of {total}";
        if (_rbViewActive.Checked && disabled > 0)
            view += $"  •  {disabled} disabled hidden";
        _countLabel.Text = view;
    }

    // ====================================================================
    //  Busy-state handling (used by every async AD operation)
    // ====================================================================

    private void SetBusy(bool busy, string? statusMessage = null)
    {
        _isBusy = busy;
        _progress.Visible = busy;
        bool usersView = !_rbViewLoggedIn.Checked;
        _btnRefresh.Enabled = !busy;
        _btnImport.Enabled = !busy && usersView;
        _btnExport.Enabled = !busy && usersView;
        _btnAdd.Enabled    = !busy && usersView;
        _txtSearch.Enabled = !busy;
        _cmbGroup.Enabled  = !busy && usersView;
        _viewTabs.Enabled  = !busy;
        _grid.Enabled      = !busy;
        UseWaitCursor      = busy;
        if (statusMessage != null) SetStatus(statusMessage);
    }

    // ====================================================================
    //  Async data loading
    // ====================================================================

    /// <summary>
    /// Load all users from AD on a background thread, marshal results back
    /// to the UI thread to populate the grid. The window stays responsive
    /// while loading and a marquee progress bar is shown.
    /// </summary>
    private async Task LoadUsersAsync()
    {
        if (_isBusy) return;
        SetBusy(true, "Loading users from Active Directory...");

        try
        {
            // Heavy work off the UI thread.
            var (users, groups) = await Task.Run(() =>
            {
                var u = _ad.LoadAllUsers();
                // Replace the imprecise lastLogonTimestamp with the accurate
                // per-DC lastLogon (best-effort; falls back on any failure).
                try { _ad.EnrichLastLogonAccurate(u); } catch { }
                List<string> g;
                try   { g = _ad.GetAllSecurityGroupNamesUnderUsersContainer(); }
                catch { g = new List<string>(); }
                return (u, g);
            });

            _allUsers       = users;
            _validGroups    = groups;
            _presentGroups  = ExtractPresentGroupNames(_allUsers);

            RebuildGroupCombo();
            UpdateAccountViewLabels();

            if (_rbViewLoggedIn.Checked)
            {
                // AD cache refreshed; stay on Logged In and reload live sessions.
                // Clear busy before hand-off so LoadSessionsViewAsync can take over.
                SetBusy(false);
                await LoadSessionsViewAsync();
                return;
            }

            ConfigureGridForUsers();
            ApplyFilter();          // honors the operator's search, group, and account-status tab

            SetStatus($"Loaded {_allUsers.Count} users from {_ad.DnsRoot}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Error reading users from Active Directory:\n" + ex.Message,
                "AD Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _allUsers = new List<AdUserInfo>();
            if (!_rbViewLoggedIn.Checked)
            {
                ConfigureGridForUsers();
                PopulateGrid(_allUsers);
            }
            UpdateCountLabel();
        }
        finally
        {
            // Safe even after the Logged In hand-off: LoadSessionsViewAsync clears
            // its own busy flag before returning, and a second SetBusy(false) is a no-op.
            SetBusy(false);
        }
    }

    private bool _suppressGroupComboEvent;

    private void OnGroupComboChanged(object? sender, EventArgs e)
    {
        if (_suppressGroupComboEvent) return;
        ApplyFilter();
    }

    private void RebuildGroupCombo()
    {
        _suppressGroupComboEvent = true;
        try
        {
            // Remember the current selection so a refresh doesn't reset the
            // operator's filter back to "All Groups".
            string? previous = _cmbGroup.SelectedIndex > 0
                ? _cmbGroup.SelectedItem?.ToString() : null;

            _cmbGroup.Items.Clear();
            _cmbGroup.Items.Add("All Groups");
            foreach (var g in _presentGroups)
                _cmbGroup.Items.Add(g);

            int restored = 0;
            if (!string.IsNullOrEmpty(previous))
            {
                for (int i = 1; i < _cmbGroup.Items.Count; i++)
                {
                    if (string.Equals(_cmbGroup.Items[i]?.ToString(), previous,
                                      StringComparison.OrdinalIgnoreCase))
                    { restored = i; break; }
                }
            }
            _cmbGroup.SelectedIndex = restored;
        }
        finally
        {
            _suppressGroupComboEvent = false;
        }
    }

    /// <summary>Apply the current search + group + account-status filter and repopulate the grid.</summary>
    private void ApplyFilter()
    {
        if (_rbViewLoggedIn.Checked)
        {
            ApplySessionsFilter();
            return;
        }

        string query = _txtSearch.Text.Trim();
        string? selectedGroup = _cmbGroup.SelectedIndex > 0
            ? _cmbGroup.SelectedItem?.ToString() : null;

        IEnumerable<AdUserInfo> filtered = _allUsers;

        if (_rbViewActive.Checked)
            filtered = filtered.Where(u => u.Enabled);
        else if (_rbViewDisabled.Checked)
            filtered = filtered.Where(u => !u.Enabled);

        if (!string.IsNullOrEmpty(query))
        {
            // Multi-token AND search with quoted phrases:
            //   smith admin    -> matches rows containing BOTH "smith" AND "admin"
            //   "global shop"  -> single token (with the space)
            var tokens = Regex.Matches(query, "(\"[^\"]+\"|\\S+)")
                .Cast<Match>()
                .Select(m => m.Value.Trim())
                .Select(t => t.StartsWith("\"") && t.EndsWith("\"") ? t.Trim('"') : t)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToArray();

            if (tokens.Length > 0)
            {
                filtered = filtered.Where(u =>
                {
                    string[] haystacks = BuildHaystacks(u);
                    foreach (var tok in tokens)
                    {
                        bool any = haystacks.Any(h =>
                            h != null && h.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!any) return false;
                    }
                    return true;
                });
            }
        }

        if (!string.IsNullOrEmpty(selectedGroup))
        {
            string sel = AdService.NormalizeGroupName(selectedGroup!);
            filtered = filtered.Where(u =>
                u.MemberOf.Any(dn => AdService.NormalizeGroupName(dn) == sel));
        }

        PopulateGrid(filtered.ToList());
        UpdateCountLabel();
    }

    private static string[] BuildHaystacks(AdUserInfo u)
    {
        string groupNames = string.Join("; ",
            u.MemberOf.Select(AdService.ExtractCn).Where(s => !string.IsNullOrWhiteSpace(s)));
        string lastLogin = u.LastLogonDate.HasValue
            ? u.LastLogonDate.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "Never";
        return new[]
        {
            u.DisplayName ?? "", u.GivenName ?? "", u.Surname ?? "",
            u.SamAccountName, groupNames, lastLogin
        };
    }

    // ====================================================================
    //  Logged In (RDS sessions) view
    // ====================================================================

    private void ConfigureGridForUsers()
    {
        if (!_gridModeSessions) return;

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        _grid.Columns.Clear();
        _grid.ReadOnly = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

        foreach (var col in Constants.AllColumns)
        {
            int idx = _grid.Columns.Add(col, col);
            _grid.Columns[idx].HeaderCell.ToolTipText =
                "Double-click to edit; right-click row(s) for actions";
        }
        foreach (var name in Constants.ReadOnlyColumns)
        {
            var col = _grid.Columns[name]!;
            col.ReadOnly = true;
            col.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor          = Constants.ReadonlyColBack,
                ForeColor          = SystemColors.GrayText,
                SelectionBackColor = ThemeManager.Current.GridSelectionBack,
                SelectionForeColor = ThemeManager.Current.GridSelectionFore
            };
        }

        _gridModeSessions = false;
        ApplyPersistedColumnWidths();
        ApplyTheme();
        _grid.ResumeLayout();
    }

    private void ConfigureGridForSessions()
    {
        if (_gridModeSessions) return;

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        _grid.Columns.Clear();
        _grid.ReadOnly = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        _grid.Columns.Add("User",  "User");
        _grid.Columns.Add("Host",  "Session Host");
        _grid.Columns.Add("Id",    "Session ID");
        _grid.Columns.Add("State", "State");
        _grid.Columns.Add("Idle",  "Idle Time");
        _grid.Columns["Id"]!.FillWeight    = 40;
        _grid.Columns["State"]!.FillWeight = 55;
        _grid.Columns["Idle"]!.FillWeight  = 55;
        // Numeric types so clicking the header sorts by duration / id, not text.
        _grid.Columns["Id"]!.ValueType     = typeof(int);
        _grid.Columns["Idle"]!.ValueType   = typeof(long);
        _grid.Columns["Idle"]!.SortMode    = DataGridViewColumnSortMode.Automatic;

        _gridModeSessions = true;
        ApplyTheme();
        _grid.ResumeLayout();
    }

    /// <summary>
    /// Query every RDS session host and show Active/Disconnected sessions in
    /// the main grid (Logged In tab). Same WTS data as Info → Active RDS Sessions.
    /// </summary>
    private async Task LoadSessionsViewAsync()
    {
        if (!_rds.IsAvailable())
        {
            ConfigureGridForSessions();
            _sessions = new List<RdsSession>();
            PopulateSessionsGrid(_sessions);
            UpdateAccountViewLabels();
            UpdateCountLabel();
            SetStatus("RDS session query is not available on this machine.");
            MessageBox.Show(this, "RDS session query is not available on this machine.",
                "Logged In", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_isBusy) return;
        ConfigureGridForSessions();
        SetBusy(true, "Querying session hosts for logged-in users...");
        try
        {
            _sessions = await Task.Run(() => _rds.GetAllSessions());
            if (IsDisposed || Disposing) return;
            ApplySessionsFilter();
            UpdateAccountViewLabels();
            int hosts = _sessions.Select(s => s.HostServer)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            SetStatus($"Loaded {_sessions.Count} logged-in session(s) across {hosts} host(s).");
        }
        catch (RdsAccessDeniedException ex)
        {
            if (IsDisposed || Disposing) return;
            _sessions = new List<RdsSession>();
            PopulateSessionsGrid(_sessions);
            UpdateAccountViewLabels();
            UpdateCountLabel();
            SetStatus("Access denied querying session hosts.");
            MessageBox.Show(this, ex.Message, "Logged In",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing) return;
            _sessions = new List<RdsSession>();
            PopulateSessionsGrid(_sessions);
            UpdateAccountViewLabels();
            UpdateCountLabel();
            SetStatus("Session query failed.");
            MessageBox.Show(this, "Failed to query sessions:\r\n" + ex.Message,
                "Logged In", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
                SetBusy(false);
        }
    }

    private void ApplySessionsFilter()
    {
        if (!_gridModeSessions) ConfigureGridForSessions();

        IEnumerable<RdsSession> filtered = _sessions;
        string query = _txtSearch.Text.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            var tokens = Regex.Matches(query, "(\"[^\"]+\"|\\S+)")
                .Cast<Match>()
                .Select(m => m.Value.Trim())
                .Select(t => t.StartsWith("\"") && t.EndsWith("\"") ? t.Trim('"') : t)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToArray();

            if (tokens.Length > 0)
            {
                filtered = filtered.Where(s =>
                {
                    string hay = $"{s.QualifiedUser} {s.HostServer} {s.UnifiedSessionId} {s.FriendlyState}";
                    foreach (var tok in tokens)
                    {
                        if (hay.IndexOf(tok, StringComparison.OrdinalIgnoreCase) < 0)
                            return false;
                    }
                    return true;
                });
            }
        }

        PopulateSessionsGrid(filtered
            .OrderBy(s => s.HostServer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList());
        UpdateCountLabel();
    }

    private void PopulateSessionsGrid(List<RdsSession> sessions)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        var built = new List<DataGridViewRow>(sessions.Count);
        foreach (var s in sessions)
        {
            bool prot = RdsService.IsProtectedAccount(s.UserName);
            var row = new DataGridViewRow();
            row.CreateCells(_grid,
                prot ? s.QualifiedUser + "  [PROTECTED]" : s.QualifiedUser,
                s.HostServer, s.UnifiedSessionId, s.FriendlyState, s.IdleSortTicks);
            row.Tag = s;
            if (prot)
                ApplyProtectedSessionStyle(row);
            built.Add(row);
        }

        if (built.Count > 0)
            _grid.Rows.AddRange(built.ToArray());

        _grid.ResumeLayout();
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
        if (!_gridModeSessions || e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Columns[e.ColumnIndex].Name != "Idle") return;
        e.Value = RdsSession.FormatIdleCell(e.Value);
        e.FormattingApplied = true;
    }

    private List<RdsSession> GetSelectedSessions()
    {
        var result = new List<RdsSession>();
        foreach (DataGridViewRow row in _grid.SelectedRows)
        {
            if (row?.Tag is RdsSession s)
                result.Add(s);
        }
        return result;
    }

    private async Task LogoffSelectedGridSessionsAsync()
    {
        var targets = GetSelectedSessions();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Select one or more sessions first.",
                "Logged In", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

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

        SetBusy(true, $"Logging off {targets.Count} session(s)...");
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
                        $"Session {s.UnifiedSessionId} on {s.HostServer} (Logged In tab)");
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
                SetBusy(false);
        }

        if (IsDisposed || Disposing) return;
        SetStatus($"Logoff complete: {ok} succeeded, {fail} failed.");
        await LoadSessionsViewAsync();
    }

    /// <summary>Repopulate the grid from a list of users.</summary>
    private void PopulateGrid(List<AdUserInfo> users)
    {
        // Suspend layout + painting during bulk population. Without this, each
        // Rows.Add triggers a full re-layout/repaint, which is O(n^2) and was
        // the cause of multi-minute loads on larger domains. SuspendLayout +
        // building rows off-grid, then adding in one AddRange, collapses it.
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        var built = new List<DataGridViewRow>(users.Count);
        foreach (var u in users)
        {
            string groups = string.Join("; ",
                u.MemberOf
                 .Select(AdService.ExtractCn)
                 .Where(g => !string.IsNullOrWhiteSpace(g) &&
                             !Constants.ExcludedGroups.Contains(g) &&
                             (_validGroups.Count == 0 ||
                              _validGroups.Contains(g, StringComparer.OrdinalIgnoreCase)))
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderBy(g => g, StringComparer.OrdinalIgnoreCase));

            string lastLogin = u.LastLogonDate.HasValue
                ? u.LastLogonDate.Value.ToString("yyyy-MM-dd HH:mm",
                                                 CultureInfo.InvariantCulture)
                : "Never";

            string username = UpnFor(u);

            var row = new DataGridViewRow();
            row.CreateCells(_grid,
                u.GivenName, u.Initials, u.Surname, u.DisplayName,
                username, lastLogin, groups);
            row.Tag = u.SamAccountName;
            ApplyEnabledStyle(row, u.Enabled, u.LockedOut);
            built.Add(row);
        }

        if (built.Count > 0)
            _grid.Rows.AddRange(built.ToArray());   // single batched insert

        _grid.ResumeLayout();
    }

    private string UpnFor(AdUserInfo u) =>
        !string.IsNullOrWhiteSpace(u.UserPrincipalName)
            ? u.UserPrincipalName!
            : (u.SamAccountName + _ad.UpnSuffix);

    private static List<string> ExtractPresentGroupNames(List<AdUserInfo> users)
    {
        return users
            .SelectMany(u => u.MemberOf.Select(AdService.ExtractCn))
            .Where(g => !string.IsNullOrWhiteSpace(g) && !Constants.ExcludedGroups.Contains(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Cached fonts - allocating a Font per row during grid population was a
    // major contributor to slow loads (a new GDI object + invalidation each row).
    private static readonly Font _italicFont = new Font("Segoe UI", 9f, FontStyle.Italic);

    /// <summary>
    /// Protected session rows stay distinct via a soft tint + italic, but always
    /// use the theme's high-contrast selection colors so the highlight stays readable.
    /// (Gray ForeColor on the system blue selection was nearly illegible.)
    /// </summary>
    private static void ApplyProtectedSessionStyle(DataGridViewRow row)
    {
        var p = ThemeManager.Current;
        var s = row.DefaultCellStyle;
        s.BackColor          = ThemeManager.IsDark
            ? Color.FromArgb(55, 50, 40)
            : Color.FromArgb(255, 247, 230);
        s.ForeColor          = p.GridFore;
        s.SelectionBackColor = p.GridSelectionBack;
        s.SelectionForeColor = p.GridSelectionFore;
        s.Font               = _italicFont;
    }

    private static void ClearProtectedSessionStyle(DataGridViewRow row)
    {
        var s = row.DefaultCellStyle;
        s.BackColor          = Color.Empty;
        s.ForeColor          = Color.Empty;
        s.SelectionBackColor = Color.Empty;
        s.SelectionForeColor = Color.Empty;
        s.Font               = null!;
    }

    private static void ApplyEnabledStyle(DataGridViewRow row, bool enabled, bool lockedOut = false)
    {
        var p = ThemeManager.Current;

        // Apply ONE row-level DefaultCellStyle instead of mutating every cell's
        // Style individually. This cuts style writes (and grid invalidations)
        // from ~7 per row to 1, which is the bulk of the load-time win.
        var s = row.DefaultCellStyle;
        if (enabled && lockedOut)
        {
            // Locked (but enabled): soft amber. Right-click offers "Unlock Account".
            s.BackColor          = Color.FromArgb(255, 243, 205);
            s.SelectionBackColor = Color.FromArgb(255, 224, 150);
            s.ForeColor          = Color.FromArgb( 90,  60,   0);
            s.SelectionForeColor = Color.FromArgb( 90,  60,   0);
            s.Font               = null!;
        }
        else if (enabled)
        {
            s.BackColor          = Color.Empty;
            s.SelectionBackColor = Color.Empty;
            s.ForeColor          = Color.Empty;
            s.SelectionForeColor = Color.Empty;
            s.Font               = null!;
        }
        else
        {
            s.BackColor          = p.DisabledRowBack;
            s.SelectionBackColor = p.DisabledRowSelBack;
            s.ForeColor          = p.DisabledRowFore;
            s.SelectionForeColor = p.DisabledRowFore;
            s.Font               = _italicFont;   // cached, not per-row
        }

        try
        {
            row.Cells[Constants.ColDisplayName].ToolTipText =
                enabled ? "" : "Account is DISABLED in Active Directory";
        }
        catch { /* best effort */ }
    }

    // ====================================================================
    //  Keyboard shortcuts (Form.KeyDown - reaches us before child controls)
    // ====================================================================

    private async void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc in the search box clears it (also when search box has focus).
        if (e.KeyCode == Keys.Escape && _txtSearch.Focused)
        {
            _txtSearch.Clear();
            ApplyFilter();
            e.Handled = true;
            return;
        }

        // Ignore other shortcuts while text input is in flight, except F5/Ctrl+combos.
        if (e.Modifiers == Keys.Control)
        {
            switch (e.KeyCode)
            {
                case Keys.F:
                    _txtSearch.Focus();
                    _txtSearch.SelectAll();
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                case Keys.N:
                    if (!_isBusy && !_rbViewLoggedIn.Checked) { OnAddUser(this, EventArgs.Empty); }
                    e.Handled = e.SuppressKeyPress = true;
                    return;
            }
        }

        switch (e.KeyCode)
        {
            case Keys.F5:
                if (!_isBusy)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    if (_rbViewLoggedIn.Checked) await LoadSessionsViewAsync();
                    else await LoadUsersAsync();
                }
                return;

            case Keys.Delete when _grid.Focused && _grid.SelectedRows.Count > 0
                               && !_rbViewLoggedIn.Checked:
                if (_grid.SelectedRows.Count > 1)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    OnBulkToggle(enable: false);
                }
                else
                {
                    e.Handled = e.SuppressKeyPress = true;
                    OnToggleAccount(this, EventArgs.Empty);
                }
                return;

            case Keys.Enter when _grid.Focused && _grid.SelectedRows.Count == 1
                              && !_grid.IsCurrentCellInEditMode
                              && !_rbViewLoggedIn.Checked:
                e.Handled = e.SuppressKeyPress = true;
                OnResetPassword(this, EventArgs.Empty);
                return;
        }
    }

    // ====================================================================
    //  Grid event handlers
    // ====================================================================

    private void OnCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;

        // If the right-clicked row is already part of a multi-selection, leave the
        // selection alone so bulk actions work intuitively.
        var row = _grid.Rows[e.RowIndex];
        if (!row.Selected)
        {
            _grid.ClearSelection();
            row.Selected = true;
            _grid.CurrentCell = row.Cells[0];
        }
    }

    private void OnCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_gridModeSessions || e.RowIndex < 0 || e.ColumnIndex < 0) return;

        var row = _grid.Rows[e.RowIndex];
        string? sam = row.Tag as string;
        if (string.IsNullOrEmpty(sam)) return;

        string colName = _grid.Columns[e.ColumnIndex].Name;
        if (Constants.ReadOnlyColumns.Contains(colName)) return;

        string? newValue = row.Cells[e.ColumnIndex].Value?.ToString();

        try
        {
            switch (colName)
            {
                case Constants.ColFirstName:
                    _ad.SetUserAttribute(sam!, "givenName", newValue);
                    break;
                case Constants.ColMiddleInitials:
                    _ad.SetUserAttribute(sam!, "initials", newValue);
                    break;
                case Constants.ColLastName:
                    _ad.SetUserAttribute(sam!, "sn", newValue);
                    break;
                case Constants.ColDisplayName:
                    _ad.SetDisplayName(sam!, newValue ?? string.Empty);
                    break;
                default:
                    return;
            }

            // Refresh row from AD so the grid reflects the actual directory state.
            var fresh = _ad.GetUser(sam!);
            ReplaceCachedUser(sam!, fresh);
            RefreshRow(row, fresh);

            AuditLogger.Write("EditUser", sam!, $"{colName}='{newValue}'");
            SetStatus("Changes saved");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Change failed:\r\n\r\n" + ex.Message);
            SetStatus("Change failed");
        }
    }

    private void ReplaceCachedUser(string sam, AdUserInfo fresh)
    {
        for (int i = 0; i < _allUsers.Count; i++)
        {
            if (string.Equals(_allUsers[i].SamAccountName, sam, StringComparison.OrdinalIgnoreCase))
            {
                _allUsers[i] = fresh;
                return;
            }
        }
    }

    private void RefreshRow(DataGridViewRow row, AdUserInfo u)
    {
        row.Cells[Constants.ColFirstName     ].Value = u.GivenName;
        row.Cells[Constants.ColMiddleInitials].Value = u.Initials;
        row.Cells[Constants.ColLastName      ].Value = u.Surname;
        row.Cells[Constants.ColDisplayName   ].Value = u.DisplayName;
        row.Cells[Constants.ColUsername      ].Value = UpnFor(u);
        row.Tag = u.SamAccountName;
        ApplyEnabledStyle(row, u.Enabled, u.LockedOut);
    }

    // ====================================================================
    //  Selection helpers
    // ====================================================================

    private AdUserInfo? GetSelectedUser()
    {
        if (_grid.SelectedRows.Count == 0) return null;
        string? sam = _grid.SelectedRows[0].Tag as string;
        if (string.IsNullOrEmpty(sam)) return null;
        return _allUsers.FirstOrDefault(u =>
            string.Equals(u.SamAccountName, sam, StringComparison.OrdinalIgnoreCase));
    }

    private List<AdUserInfo> GetSelectedUsers()
    {
        var result = new List<AdUserInfo>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _grid.SelectedRows)
        {
            if (row?.Tag is string sam && seen.Add(sam))
            {
                var u = _allUsers.FirstOrDefault(x =>
                    string.Equals(x.SamAccountName, sam, StringComparison.OrdinalIgnoreCase));
                if (u != null) result.Add(u);
            }
        }
        return result;
    }

    // ====================================================================
    //  Context menu
    // ====================================================================

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Logged In tab: only session logoff (and separator for spacing).
        if (_rbViewLoggedIn.Checked)
        {
            int sessionCount = GetSelectedSessions().Count;
            _miReset.Visible = _miToggleAcct.Visible = _miUnlock.Visible = false;
            _miManageGrp.Visible = _miCompanyCodes.Visible = _miLogoff.Visible = false;
            _miBulkEnable.Visible = _miBulkDisable.Visible = false;
            _miBulkGroups.Visible = _miBulkCompanyCodes.Visible = _miBulkLogoff.Visible = false;
            _miCopy.Visible = _sepCopy.Visible = false;
            _sepBulk.Visible = false;

            _miSessionLogoff.Visible = true;
            _miSessionLogoff.Enabled = sessionCount > 0 && _rds.IsAvailable();
            _miSessionLogoff.Text = sessionCount <= 1
                ? "Log Off Selected..."
                : $"Log Off {sessionCount} Sessions...";
            return;
        }

        _miSessionLogoff.Visible = false;
        _miReset.Visible = _miToggleAcct.Visible = _miManageGrp.Visible = _miCompanyCodes.Visible = true;
        _miCopy.Visible = _sepCopy.Visible = true;

        var selected = GetSelectedUsers();
        int count = selected.Count;

        // Single-user items only enabled when exactly one row is selected.
        bool single = count == 1;
        _miReset.Enabled       = single;
        _miUnlock.Visible      = single && selected.Count == 1 && selected[0].LockedOut;
        _miManageGrp.Enabled   = single;
        _miCompanyCodes.Enabled = single;
        _miToggleAcct.Enabled  = single;
        _miLogoff.Visible      = single;
        if (single)
        {
            _miToggleAcct.Text = selected[0].Enabled ? "Disable User" : "Enable User";
        }
        else
        {
            _miToggleAcct.Text = "Disable User";
        }

        // Bulk items only show + enabled when >1 rows selected.
        bool multi = count > 1;
        _sepBulk.Visible       = multi || single;  // separator also divides single logoff from copy
        _miBulkEnable.Visible  = multi;
        _miBulkDisable.Visible = multi;
        _miBulkGroups.Visible  = multi;
        _miBulkCompanyCodes.Visible = multi;
        _miBulkLogoff.Visible  = multi;
        _miBulkEnable.Text     = $"Enable {count} Selected Users";
        _miBulkDisable.Text    = $"Disable {count} Selected Users";
        _miBulkGroups.Text     = $"Manage Groups for {count} Selected Users...";
        _miBulkCompanyCodes.Text = $"Company Codes for {count} Selected Users...";
        _miBulkLogoff.Text     = $"Log Off {count} Selected Users' Sessions...";

        // Logoff requires the RDS RemoteDesktop module. If unavailable, hide both.
        if (!_rds.IsAvailable())
        {
            _miLogoff.Visible     = false;
            _miBulkLogoff.Visible = false;
        }

        // Copy submenu enabled when at least one row is selected.
        _miCopy.Enabled = count >= 1;
    }

    private void OnResetPassword(object? sender, EventArgs e)
    {
        var u = GetSelectedUser();
        if (u == null) return;

        string display = !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName! : u.SamAccountName;
        using var dlg = new ResetPasswordForm(_ad, u.SamAccountName, display);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            SetStatus($"Password reset for {u.SamAccountName}");
        }
    }

    private async void OnToggleAccount(object? sender, EventArgs e)
    {
        var u = GetSelectedUser();
        if (u == null) return;

        string action = u.Enabled ? "Disable" : "Enable";
        string name   = !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName! : u.SamAccountName;

        var res = MessageBox.Show(this,
            $"Are you sure you want to {action} the account for:\r\n\r\n{name} ({u.SamAccountName})?",
            $"{action} User",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (res != DialogResult.Yes) return;

        SetBusy(true, $"{action} {u.SamAccountName}...");
        try
        {
            await Task.Run(() =>
            {
                if (u.Enabled) _ad.DisableAccount(u.SamAccountName);
                else           _ad.EnableAccount (u.SamAccountName);
            });

            AuditLogger.Write($"{action} User", u.SamAccountName,
                              $"{action}d account for '{name}'");
            SetStatus($"{action}d {u.SamAccountName}");

            u.Enabled = !u.Enabled;
            UpdateAccountViewLabels();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{action} failed for '{u.SamAccountName}':\r\n{ex.Message}");
            SetStatus($"{action} failed");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Every group the dashboard can manage: the AD query of all security
    /// groups in the Users container (_validGroups - includes groups with ZERO
    /// members) unioned with names derived from current user memberships
    /// (_presentGroups - covers memberships that live outside that container).
    /// </summary>
    private List<string> AllManageableGroups()
    {
        return _validGroups
            .Concat(_presentGroups)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Where(g => !CompanyCodeHelper.IsCompanyCodeGroup(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OnManageGroups(object? sender, EventArgs e)
    {
        var u = GetSelectedUser();
        if (u == null) return;

        using var dlg = new ManageGroupsForm(_ad, u, AllManageableGroups());
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                var fresh = _ad.GetUser(u.SamAccountName);
                ReplaceCachedUser(u.SamAccountName, fresh);
                ApplyFilter();
            }
            catch
            {
                _ = LoadUsersAsync();
            }
        }
    }

    private void OnCompanyCodes(object? sender, EventArgs e)
    {
        var u = GetSelectedUser();
        if (u == null) return;

        using var dlg = new CompanyCodesForm(_ad, u);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var fresh = _ad.GetUser(u.SamAccountName);
            ReplaceCachedUser(u.SamAccountName, fresh);
            try { _validGroups = _ad.GetAllSecurityGroupNamesUnderUsersContainer(); }
            catch { }
            _presentGroups = ExtractPresentGroupNames(_allUsers);
            RebuildGroupCombo();
            ApplyFilter();
            SetStatus($"Company codes updated for {u.SamAccountName}");
        }
        catch
        {
            _ = LoadUsersAsync();
        }
    }

    private DataGridViewRow? FindRowBySam(string sam)
    {
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r != null && !r.IsNewRow &&
                string.Equals(r.Tag as string, sam, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return null;
    }

    // ====================================================================
    //  Bulk actions (multi-row)
    // ====================================================================

    /// <summary>
    /// Bulk add/remove the selected users to/from security groups. Uses the
    /// full manageable group list (including groups with no members yet).
    /// </summary>
    private async void OnBulkManageGroups(object? sender, EventArgs e)
    {
        var users = GetSelectedUsers();
        if (users.Count < 2) return;

        using var dlg = new BulkGroupsForm(_ad, users, AllManageableGroups());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.ChangedUsers.Count == 0) { SetStatus("Bulk groups: no changes needed."); return; }

        // Refresh just the users whose membership changed.
        SetBusy(true, "Refreshing updated users...");
        try
        {
            foreach (var sam in dlg.ChangedUsers)
            {
                try
                {
                    var fresh = await Task.Run(() => _ad.GetUser(sam));
                    ReplaceCachedUser(sam, fresh);
                }
                catch { /* fall through to full reload below */ }
            }
            _presentGroups = ExtractPresentGroupNames(_allUsers);
            RebuildGroupCombo();
            ApplyFilter();
            SetStatus($"Bulk groups: {dlg.ChangedUsers.Count} user(s) updated.");
        }
        catch
        {
            _ = LoadUsersAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnBulkCompanyCodes(object? sender, EventArgs e)
    {
        var users = GetSelectedUsers();
        if (users.Count < 2) return;

        using var dlg = new BulkCompanyCodesForm(_ad, users);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.ChangedUsers.Count == 0) { SetStatus("Bulk company codes: no changes needed."); return; }

        SetBusy(true, "Refreshing updated users...");
        try
        {
            foreach (var sam in dlg.ChangedUsers)
            {
                try
                {
                    var fresh = await Task.Run(() => _ad.GetUser(sam));
                    ReplaceCachedUser(sam, fresh);
                }
                catch { /* fall through */ }
            }
            try { _validGroups = await Task.Run(() => _ad.GetAllSecurityGroupNamesUnderUsersContainer()); }
            catch { }
            _presentGroups = ExtractPresentGroupNames(_allUsers);
            RebuildGroupCombo();
            ApplyFilter();
            SetStatus($"Bulk company codes: {dlg.ChangedUsers.Count} user(s) updated.");
        }
        catch
        {
            _ = LoadUsersAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnBulkToggle(bool enable)
    {
        var users = GetSelectedUsers();
        if (users.Count == 0) return;

        // Filter to those that actually need the change.
        var targets = users.Where(u => u.Enabled != enable).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this,
                enable ? "All selected users are already enabled."
                       : "All selected users are already disabled.",
                "No Change Needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string verb = enable ? "ENABLE" : "DISABLE";
        var preview = string.Join("\r\n",
            targets.Take(10).Select(u =>
                $"  • {(string.IsNullOrWhiteSpace(u.DisplayName) ? u.SamAccountName : u.DisplayName)}  ({u.SamAccountName})"));
        if (targets.Count > 10) preview += $"\r\n  ...and {targets.Count - 10} more";

        var res = MessageBox.Show(this,
            $"You are about to {verb} {targets.Count} user account(s):\r\n\r\n{preview}\r\n\r\nProceed?",
            $"Bulk {verb}",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (res != DialogResult.Yes) return;

        SetBusy(true, $"{verb}: 0 / {targets.Count}...");
        var failures = new List<string>();
        int done = 0;

        try
        {
            foreach (var u in targets)
            {
                string sam = u.SamAccountName;
                try
                {
                    await Task.Run(() =>
                    {
                        if (enable) _ad.EnableAccount(sam);
                        else        _ad.DisableAccount(sam);
                    });

                    u.Enabled = enable;

                    AuditLogger.Write($"Bulk{(enable ? "Enable" : "Disable")}", sam,
                                      "Bulk operation via dashboard");
                }
                catch (Exception ex)
                {
                    failures.Add($"{sam}: {ex.Message}");
                }

                done++;
                SetStatus($"{verb}: {done} / {targets.Count}...");
            }
        }
        finally
        {
            SetBusy(false);
        }

        int success = targets.Count - failures.Count;
        UpdateAccountViewLabels();
        ApplyFilter();
        SetStatus($"Bulk {verb.ToLowerInvariant()} complete. Success: {success}, failed: {failures.Count}");

        if (failures.Count > 0)
        {
            string details = string.Join("\r\n", failures.Take(10));
            if (failures.Count > 10) details += $"\r\n...and {failures.Count - 10} more";
            MessageBox.Show(this,
                $"{success} succeeded, {failures.Count} failed.\r\n\r\nFirst failures:\r\n{details}",
                "Bulk Operation Result",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ====================================================================
    //  RDS session logoff
    // ====================================================================

    /// <summary>
    /// Query the broker for sessions belonging to the selected user(s), show a
    /// confirmation dialog listing them, and log off all of them on confirm.
    /// Handles single and bulk selection identically (operates on whatever is
    /// selected). Aggressive: logs off all session states.
    /// </summary>
    private async void OnLogoffSessions()
    {
        var users = GetSelectedUsers();
        if (users.Count == 0) return;

        // Protected accounts (Administrator, GLOBALSHOP, ...) cannot be logged
        // off from the dashboard. Strip them out up front; RdsService blocks
        // them as a backstop even if this check were bypassed.
        var protectedSel = users.Where(u => RdsService.IsProtectedAccount(u.SamAccountName)).ToList();
        if (protectedSel.Count > 0)
        {
            users = users.Where(u => !RdsService.IsProtectedAccount(u.SamAccountName)).ToList();
            MessageBox.Show(this,
                "These accounts are PROTECTED - the dashboard will not log off their sessions:\r\n\r\n" +
                string.Join("\r\n", protectedSel.Select(u => $"  • {u.SamAccountName}")) +
                (users.Count > 0 ? "\r\n\r\nContinuing with the remaining selection." : ""),
                "Protected Accounts", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (users.Count == 0) return;
        }

        var sams = users.Select(u => u.SamAccountName).ToList();

        SetBusy(true, "Querying RDS sessions...");
        List<RdsSession> sessions;
        try
        {
            sessions = await Task.Run(() => _rds.GetSessionsForUsers(sams));
        }
        catch (RdsAccessDeniedException)
        {
            ShowRdsPermissionMessage();
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Could not query RDS sessions:\r\n\r\n" + ex.Message,
                "RDS Logoff", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        if (sessions.Count == 0)
        {
            string who = users.Count == 1
                ? $"'{users[0].SamAccountName}'"
                : $"the {users.Count} selected users";
            MessageBox.Show(this,
                $"No active RDS sessions found for {who}.",
                "RDS Logoff", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Detect whether the operator's own account is among the sessions.
        string me = SafeCurrentSam();
        bool includesSelf = !string.IsNullOrEmpty(me) &&
            sessions.Any(s => string.Equals(s.UserName, me, StringComparison.OrdinalIgnoreCase));

        using var dlg = new LogoffConfirmForm(sessions, includesSelf);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // Perform the logoffs on a background thread.
        SetBusy(true, $"Logging off {sessions.Count} session(s)...");
        var results = new List<LogoffResult>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var s in sessions)
                {
                    var r = _rds.LogoffSession(s);
                    results.Add(r);
                }
            });
        }
        finally
        {
            SetBusy(false);
        }

        int ok   = results.Count(r => r.Success);
        int fail = results.Count - ok;

        // Audit each terminated session individually.
        foreach (var r in results.Where(r => r.Success))
        {
            AuditLogger.Write("LogoffSession", r.Session.UserName,
                $"Logged off session {r.Session.UnifiedSessionId} on {r.Session.HostServer} " +
                $"(state was {r.Session.FriendlyState})");
        }
        foreach (var r in results.Where(r => !r.Success))
        {
            AuditLogger.Write("LogoffSessionFailed", r.Session.UserName,
                $"Failed to log off session {r.Session.UnifiedSessionId} on " +
                $"{r.Session.HostServer}: {r.Error}");
        }

        SetStatus($"Logoff complete. {ok} succeeded, {fail} failed.");

        if (fail > 0)
        {
            // If every failure looks like a permissions problem, show the friendly
            // "admin rights required" message rather than a wall of raw errors.
            bool allAccessDenied = results.Where(r => !r.Success)
                .All(r => RdsErrorLooksLikePermission(r.Error));

            if (ok == 0 && allAccessDenied)
            {
                ShowRdsPermissionMessage();
                return;
            }

            string details = string.Join("\r\n",
                results.Where(r => !r.Success)
                       .Take(10)
                       .Select(r => $"{r.Session.UserName} @ {r.Session.HostServer} " +
                                    $"(session {r.Session.UnifiedSessionId}): {r.Error}"));
            MessageBox.Show(this,
                $"{ok} session(s) logged off, {fail} failed.\r\n\r\nFailures:\r\n{details}",
                "RDS Logoff Result", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show(this,
                $"Successfully logged off {ok} session(s).",
                "RDS Logoff", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Refresh "Last Login" data which may now have changed.
        await LoadUsersAsync();
    }

    /// <summary>Current operator's SAM account name (without domain), best-effort.</summary>
    private static string SafeCurrentSam()
    {
        try
        {
            string full = System.Security.Principal.WindowsIdentity.GetCurrent().Name ?? "";
            int slash = full.IndexOf('\\');
            return slash >= 0 ? full.Substring(slash + 1) : full;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Show the "permissions required" message for RDS logoff. Used whenever a
    /// session query or logoff fails with access-denied. The dashboard manages
    /// sessions via the WTS API against each session host's RDP-Tcp listener, so
    /// the required rights are Query/Reset/Logoff on that listener - NOT admin on
    /// the connection broker.
    /// </summary>
    private void ShowRdsPermissionMessage()
    {
        SetStatus("RDS logoff requires Query/Reset/Logoff rights on the RDP-Tcp listener.");
        MessageBox.Show(this,
            "Access Denied - your account doesn't have permission to log off this session.\r\n\r\n" +
            "The dashboard ends sessions directly on each session host's RDP-Tcp listener. " +
            "To do that, your account needs these rights on the listener: Query (see sessions), " +
            "Logoff (end a session), and Reset (end ANOTHER user's session). These are granted " +
            "through membership in the UserDashboard group; no rights on the connection broker are " +
            "needed.\r\n\r\n" +
            "What to do:\r\n" +
            "  - Confirm your account is a member of the UserDashboard group (a fresh logon may be " +
            "needed for new membership to take effect), or\r\n" +
            "  - Have an administrator run the setup script with -ApplyListener so the listener " +
            "grants the group Query+Reset+Logoff (mask 0x25) on each session host.\r\n\r\n" +
            "Note: a session that was already open before the grant was applied keeps its old " +
            "permissions - that user must log off and back on once for the new rights to apply.\r\n\r\n" +
            "All other dashboard features (user management, password reset, group membership) are " +
            "unaffected - only session logoff needs these listener rights.",
            "RDS Logoff - Permissions Required",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Heuristic: does a per-session logoff error look permission-related?</summary>
    private static bool RdsErrorLooksLikePermission(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        string m = error!.ToLowerInvariant();
        return m.Contains("access is denied")
            || m.Contains("access denied")
            || m.Contains("deployment does not exist")
            || m.Contains("not authorized")
            || m.Contains("unauthorized")
            || m.Contains("permission")
            || m.Contains("administrator")
            || m.Contains("winrm")
            || m.Contains("cannot be reached")
            || m.Contains("rpc server is unavailable");
    }

    // ====================================================================
    //  Copy submenu
    // ====================================================================

    private void CopySelectedField(Func<AdUserInfo, string> selector)
    {
        var users = GetSelectedUsers();
        if (users.Count == 0) return;

        var lines = users.Select(selector)
                         .Where(s => !string.IsNullOrWhiteSpace(s))
                         .ToArray();
        if (lines.Length == 0) return;

        try
        {
            // Clipboard.SetText throws on empty/null - we just guarded against that.
            // It can also throw COM exceptions if the clipboard is locked - retry once.
            try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); }
            catch { Clipboard.SetDataObject(string.Join(Environment.NewLine, lines), true); }

            SetStatus(lines.Length == 1
                ? $"Copied to clipboard: {lines[0]}"
                : $"Copied {lines.Length} values to clipboard");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Copy failed: " + ex.Message,
                "Clipboard Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ====================================================================
    //  Top-bar actions
    // ====================================================================

    private void OnInfoDropdown(object? sender, EventArgs e)
    {
        // Show the menu directly below the Info button.
        _infoMenu.Show(_btnInfo, new Point(0, _btnInfo.Height));
    }

    /// <summary>Unlock a locked-out account (visible only when the selected user is locked).</summary>
    private async void OnUnlockAccount(object? sender, EventArgs e)
    {
        var u = GetSelectedUser();
        if (u == null || !u.LockedOut) return;

        SetBusy(true, $"Unlocking {u.SamAccountName}...");
        try
        {
            await Task.Run(() => _ad.UnlockAccount(u.SamAccountName));
            u.LockedOut = false;
            AuditLogger.Write("UnlockAccount", u.SamAccountName, "Unlocked via dashboard");

            var row = FindRowBySam(u.SamAccountName);
            if (row != null) ApplyEnabledStyle(row, u.Enabled, lockedOut: false);
            SetStatus($"Unlocked account: {u.SamAccountName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unlock failed for '{u.SamAccountName}':\r\n{ex.Message}",
                "Unlock Account", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    /// <summary>Open the cross-host active sessions viewer.</summary>
    private void OnShowSessions(object? sender, EventArgs e)
    {
        if (!_rds.IsAvailable())
        {
            MessageBox.Show(this, "RDS session query is not available on this machine.",
                "Active RDS Sessions", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SessionsViewerForm(_rds);
        dlg.ShowDialog(this);
    }

    /// <summary>
    /// Show the RD Web password-change URL for this deployment so the operator
    /// can hand it to an end user. Yes = copy to clipboard.
    /// </summary>
    private void OnShowPasswordPage(object? sender, EventArgs e)
    {
        string? url = Services.RdWebLocator.GetPasswordChangeUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this,
                "Could not determine the RD Web password page for this deployment.\r\n\r\n" +
                "The dashboard looks up the RD Gateway from the Active Directory group " +
                "'RDS Remote Access Servers'. If that group is empty in this domain, set " +
                "Constants.PasswordChangeUrlOverride and rebuild.",
                "End-User Password Page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dlg = new RdWebPasswordLinkForm(
            "End-User Password Page",
            "End users can change their own password at:\r\n\r\n" +
            "(They sign in with their current credentials and set a new password.)",
            url);
        dlg.ShowDialog(this);
    }

    private void OnAbout(object? sender, EventArgs e)
    {
        // Show audit log status (path and health) so users can verify it's working.
        string auditStatus = AuditLogger.CurrentDirectory != null
            ? $"Audit Log:\r\n  {AuditLogger.CurrentDirectory}\r\n  Windows Event Log sink: " +
              (AuditLogger.EventLogActive ? "active (GSS-UserDashboard)" : "inactive (source not registered)") +
              $"\r\n  Writes since launch: " +
              $"{AuditLogger.SuccessCount} succeeded, {AuditLogger.FailureCount} failed"
            : "Audit Log: NOT CONFIGURED (no writable directory found)";

        string configStatus = AppConfig.ConfigFileLoaded
            ? $"Site config: {AppConfig.ConfigFilePath}"
            : "Site config: none (using built-in defaults)";

        // Show RDS logoff status + the permissions note for the logoff feature.
        string rdsStatus;
        if (!_rds.IsAvailable())
        {
            rdsStatus = "RDS Logoff: not available on this machine.";
        }
        else
        {
            string? broker = _rds.GetBrokerFqdn();
            var diag = _rds.GetHostDiagnostics();
            string hostLines = diag.Count > 0
                ? string.Join("\r\n", diag.Select(d => "    " + d))
                : "    none detected";
            rdsStatus =
                "RDS Session Logoff [WTS/listener build]:\r\n" +
                $"  Connection broker: {(string.IsNullOrEmpty(broker) ? "not detected" : broker)}\r\n" +
                "  Session hosts (this account's access):\r\n" + hostLines + "\r\n" +
                "  NOTE: Logging off RDS sessions uses each session host's RDP-Tcp\r\n" +
                "  listener permission (Query + Logoff). Operators need that delegated\r\n" +
                "  listener permission on the host - no local admin and no connection\r\n" +
                "  broker admin are required. All other features work without it.";
        }

        MessageBox.Show(this,
            $"UserDashboard v{AppConfig.Version}\r\n" +
            configStatus + "\r\n\r\n" +
            "Please submit a cloud request https://www.gss-service.com/ to Delete Users.\r\n\r\n" +
            "Please submit a cloud request for any bugs with this UserDashboard.\r\n\r\n" +
            "New users must change their temporary password at the RD Web page to a unique\r\n" +
            "password only they know before they can log on.\r\n\r\n" +
            auditStatus + "\r\n\r\n" +
            rdsStatus,
            "User Dashboard - About",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <summary>
    /// Open the audit log folder in Explorer. Greyed-out gracefully if the
    /// logger never resolved a directory.
    /// </summary>
    private void OnOpenAuditLog(object? sender, EventArgs e)
    {
        string? dir = AuditLogger.CurrentDirectory;
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
        {
            MessageBox.Show(this,
                "Audit log directory has not been resolved or doesn't exist yet.\r\n\r\n" +
                $"Last error: {AuditLogger.LastFailureMessage ?? "(none)"}",
                "Audit Log",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            // Open Explorer at the audit log folder; if today's file exists, select it.
            string? file = AuditLogger.CurrentFilePath;
            if (file != null && System.IO.File.Exists(file))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
            }
            else
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Couldn't open the audit log folder: " + ex.Message,
                "Audit Log", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Show a one-time warning per session if the audit logger fails to write.
    /// Compliance-critical: silent audit failure is itself a finding, so we
    /// always make the user aware.
    ///
    /// Defensive: this handler can be invoked early (from any thread, even
    /// before the form's handle is created) so we null-check every UI access.
    /// </summary>
    private bool _auditWarnShown;
    private void OnAuditWriteFailed(string message)
    {
        // If the form's window handle isn't created yet we can't safely touch
        // any UI. AuditLogger already tracks the failure count internally, so
        // just bail out - the user can see the failure later via Info > About.
        if (!IsHandleCreated)
        {
            return;
        }

        // Marshal back to UI thread - this event can fire from background tasks.
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)(() => OnAuditWriteFailed(message))); }
            catch { /* form may be closing - safe to ignore */ }
            return;
        }

        // Update status bar (only if it exists - belt + suspenders).
        if (_statusLabel != null)
        {
            try { SetStatus("\u26A0 Audit log error - see Info > About for details"); }
            catch { /* ignore */ }
        }

        if (_auditWarnShown) return;
        _auditWarnShown = true;

        try
        {
            MessageBox.Show(this,
                "WARNING: an audit log write failed.\r\n\r\n" +
                $"Details: {message}\r\n\r\n" +
                "Subsequent failures will be tracked silently. Click Info > About to see " +
                "current audit log location and failure count.",
                "Audit Log Failure",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch { /* never let this kill the app */ }
    }

    private void OnShowShortcuts(object? sender, EventArgs e)
    {
        MessageBox.Show(this,
            "Keyboard shortcuts:\r\n\r\n" +
            "  F5             Refresh (AD users, or sessions on Logged In)\r\n" +
            "  Ctrl+F         Focus the search box\r\n" +
            "  Esc            Clear search (when search box is focused)\r\n" +
            "  Ctrl+N         Add user\r\n" +
            "  Delete         Disable / enable selected user(s)\r\n" +
            "  Enter          Reset password (single selection)\r\n\r\n" +
            "Tips:\r\n" +
            "  - Shift-click or Ctrl-click rows to multi-select.\r\n" +
            "  - Right-click any row for the full action menu.\r\n" +
            "  - Double-click a name cell to edit it inline.\r\n" +
            "  - Use Active / Disabled / All to filter accounts.\r\n" +
            "  - Use Logged In to see who is currently on RDS session hosts.",
            "Keyboard Shortcuts",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OnDarkModeToggled(object? sender, EventArgs e)
    {
        bool dark = _miDarkMode.Checked;
        _settings.IsDarkMode = dark;
        ThemeManager.SetTheme(dark);
        ApplyTheme();
        _settings.Save();   // persist immediately so the next launch picks it up
        SetStatus(dark ? "Dark mode enabled" : "Light mode enabled");
    }

    /// <summary>
    /// Re-style the form, every child control, all menus, and the grid
    /// using <see cref="ThemeManager.Current"/>. Safe to call repeatedly.
    /// </summary>
    private void ApplyTheme()
    {
        var p = ThemeManager.Current;

        // 1. Recursively color the form & all its child controls
        ThemeManager.ApplyTo(this);

        // 2. Theme both ContextMenuStrips (not part of the control tree)
        ThemeManager.ApplyTo(_ctx);
        ThemeManager.ApplyTo(_infoMenu);

        // 3. Tip line on top - special green/dimmed color from palette
        _lblTip.ForeColor = p.TipFore;
        StyleAccountViewTabs();

        // 4. Toolbar buttons keep brand colors + flat hover regardless of theme.
        UiChrome.StyleBrandButton(_btnRefresh, Constants.GssGreen);
        UiChrome.StyleBrandButton(_btnImport,  Constants.GssGreen);
        UiChrome.StyleBrandButton(_btnExport,  Constants.GssOrange);
        UiChrome.StyleBrandButton(_btnAdd,     Constants.GssOrange);
        UiChrome.StyleBrandButton(_btnInfo,    Constants.GssOrange);

        // 5. DataGridView: replace cell styles with the new palette
        _grid.BackgroundColor = p.GridBack;
        _grid.GridColor       = p.GridLines;
        _grid.ColumnHeadersHeight = 36;
        _grid.RowTemplate.Height  = 30;
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor          = p.GridBack,
            ForeColor          = p.GridFore,
            SelectionBackColor = p.GridSelectionBack,
            SelectionForeColor = p.GridSelectionFore,
            Padding            = new Padding(8, 0, 4, 0),
            Font               = new Font("Segoe UI", 9f),
        };
        _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor          = p.GridAlternate,
            ForeColor          = p.GridFore,
            SelectionBackColor = p.GridSelectionBack,
            SelectionForeColor = p.GridSelectionFore,
            Padding            = new Padding(8, 0, 4, 0),
        };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor          = p.HeaderBack,
            ForeColor          = p.HeaderFore,
            Font               = new Font("Segoe UI", 9f, FontStyle.Bold),
            Padding            = new Padding(8, 0, 4, 0),
            Alignment          = DataGridViewContentAlignment.MiddleLeft,
            SelectionBackColor = p.HeaderBack,
            SelectionForeColor = p.HeaderFore,
        };
        // Read-only columns keep a muted idle look, but share the grid selection
        // colors so a selected row highlights edge-to-edge (not cell-by-cell).
        foreach (var name in Constants.ReadOnlyColumns)
        {
            var col = _grid.Columns[name];
            if (col == null) continue;
            col.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor          = p.ReadOnlyCellBack,
                ForeColor          = p.ReadOnlyCellFore,
                SelectionBackColor = p.GridSelectionBack,
                SelectionForeColor = p.GridSelectionFore,
            };
        }

        // 6. Re-apply enabled/disabled row tinting (the disabled colors are theme-dependent).
        //    Walk every row and style based on its cached AdUserInfo.Enabled.
        //    In the Logged In sessions view, re-apply protected-account styling
        //    so theme toggles keep selection text readable.
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            if (_gridModeSessions)
            {
                if (row.Tag is RdsSession sess && RdsService.IsProtectedAccount(sess.UserName))
                    ApplyProtectedSessionStyle(row);
                else
                    ClearProtectedSessionStyle(row);
                continue;
            }

            string? sam = row.Tag as string;
            if (sam == null) continue;
            var u = _allUsers.FirstOrDefault(x =>
                string.Equals(x.SamAccountName, sam, StringComparison.OrdinalIgnoreCase));
            ApplyEnabledStyle(row, u?.Enabled ?? true);
        }

        _grid.Invalidate();
    }

    private async void OnAddUser(object? sender, EventArgs e)
    {
        if (_isBusy || _rbViewLoggedIn.Checked) return;
        using var dlg = new AddUserForm(_ad);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            await LoadUsersAsync();
        }
    }

    // -- CSV export ---------------------------------------------------------

    private void OnExportCsv(object? sender, EventArgs e)
    {
        if (_grid.Rows.Count == 0)
        {
            MessageBox.Show(this, "Nothing to export");
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Filter   = "CSV (*.csv)|*.csv",
            FileName = "AD_Users.csv"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            using var sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8);
            sw.WriteLine("FirstName,MiddleInitials,LastName,DisplayName,Username,LastLogin,SecurityGroups");

            int count = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                sw.WriteLine(string.Join(",", new[]
                {
                    Csv(row.Cells[Constants.ColFirstName     ].Value?.ToString()),
                    Csv(row.Cells[Constants.ColMiddleInitials].Value?.ToString()),
                    Csv(row.Cells[Constants.ColLastName      ].Value?.ToString()),
                    Csv(row.Cells[Constants.ColDisplayName   ].Value?.ToString()),
                    Csv(row.Cells[Constants.ColUsername      ].Value?.ToString()),
                    Csv(row.Cells[Constants.ColLastLogin     ].Value?.ToString()),
                    Csv(row.Cells[Constants.ColSecurityGroups].Value?.ToString()),
                }));
                count++;
            }

            SetStatus($"Exported {count} users");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export failed:\r\n" + ex.Message,
                "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string Csv(string? v)
    {
        v ??= string.Empty;
        bool needs = v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        return needs ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // -- CSV import (now async) --------------------------------------------

    private async void OnImportCsv(object? sender, EventArgs e)
    {
        MessageBox.Show(this,
            "CSV header should include Username, GivenName, MiddleInitial, Surname, " +
            "DisplayName, UserPrincipalName, Password, and Path. GivenName may be left blank, " +
            "but Password and the other required fields must be populated.\r\n\r\n" +
            "Imported accounts receive a temporary password. Each new user must change it " +
            "to a unique password only they know at the RD Web password page before they can log on.",
            "Import Requirements",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);

        using var dlg = new OpenFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            Title  = "Select user import CSV"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        SetBusy(true, "Reading CSV...");
        try
        {
            var rows = await Task.Run(() => ReadCsv(dlg.FileName));
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "The selected CSV file is empty.",
                    "Import CSV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string[] required = { "Username","GivenName","MiddleInitial","Surname",
                                  "DisplayName","UserPrincipalName","Password","Path" };
            var headers = rows[0].Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = required.Where(h => !headers.Contains(h)).ToList();
            if (missing.Count > 0)
            {
                MessageBox.Show(this,
                    "CSV is missing required columns:\r\n\r\n" + string.Join("\r\n", missing),
                    "Invalid CSV Format", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int created = 0, skipped = 0, failed = 0;
            var failMessages = new List<string>();

            static string Get(Dictionary<string, string> d, string k) =>
                d.TryGetValue(k, out var v) ? v : string.Empty;

            int idx = 0;
            foreach (var account in rows)
            {
                idx++;
                string username = Get(account, "Username").Trim();
                if (string.IsNullOrWhiteSpace(username)) continue;

                SetStatus($"Importing {idx} / {rows.Count}: {username}...");

                var missingFields = new List<string>();
                void Need(string key) {
                    if (string.IsNullOrWhiteSpace(Get(account, key)))
                        missingFields.Add(key);
                }
                Need("Username"); Need("Surname"); Need("DisplayName");
                Need("UserPrincipalName"); Need("Password"); Need("Path");

                if (missingFields.Count > 0)
                {
                    failed++;
                    string msg = $"Failed to create {username}: missing required field(s): {string.Join(", ", missingFields)}";
                    failMessages.Add(msg);
                    AuditLogger.Write("ImportFail", username, msg);
                    continue;
                }

                bool exists = await Task.Run(() => _ad.UserExists(username));
                if (exists)
                {
                    skipped++;
                    AuditLogger.Write("ImportSkip", username, "User already exists");
                    continue;
                }

                try
                {
                    await Task.Run(() => _ad.CreateUser(new AdService.CreateUserRequest
                    {
                        SamAccountName    = username,
                        UserPrincipalName = account["UserPrincipalName"].Trim(),
                        DisplayName       = account["DisplayName"].Trim(),
                        Surname           = account["Surname"].Trim(),
                        GivenName         = NullIfBlank(Get(account, "GivenName")),
                        Initials          = NullIfBlank(Get(account, "MiddleInitial")),
                        Password          = account["Password"],
                        Path              = account["Path"].Trim(),
                        PasswordNeverExpires          = false,
                        MustChangePasswordAtNextLogon = true
                    }));
                    created++;
                    AuditLogger.Write("ImportCreate", username,
                        "User created via CSV import; mustChangeAtNextLogon=true");
                }
                catch (Exception ex)
                {
                    failed++;
                    failMessages.Add($"Failed to create {username}: {ex.Message}");
                    AuditLogger.Write("ImportFail", username, ex.Message);
                }
            }

            SetBusy(false);
            await LoadUsersAsync();

            SetStatus($"Import complete. Created: {created} | Skipped: {skipped} | Failed: {failed}");

            string details = failMessages.Count == 0
                ? "No errors."
                : string.Join("\r\n", failMessages.Take(8)) +
                  (failMessages.Count > 8 ? $"\r\n...and {failMessages.Count - 8} more" : "");

            MessageBox.Show(this,
                $"Import complete.\r\n\r\nCreated: {created}\r\nSkipped: {skipped}\r\nFailed: {failed}\r\n\r\nDetails:\r\n{details}",
                "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);

            if (created > 0)
            {
                using var linkDlg = new RdWebPasswordLinkForm(
                    "Imported Users — Password Change Required",
                    $"{created} user(s) were created with a TEMPORARY password.\r\n\r\n" +
                    "Each new user must open the self-service password page and set a " +
                    "unique password that only they know before they can log on.\r\n\r\n" +
                    "Copy this link to send in email or chat:",
                    RdWebLocator.GetPasswordChangeUrl());
                linkDlg.ShowDialog(this);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Import failed");
            MessageBox.Show(this, "Import failed:\r\n\r\n" + ex.Message,
                "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // SetBusy(false) may have already been called above; re-set defensively.
            if (_isBusy) SetBusy(false);
        }
    }

    private static string? NullIfBlank(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Tiny RFC-4180 CSV reader (handles quotes & embedded commas).</summary>
    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        using var sr = new StreamReader(path);

        var headerFields = ParseCsvLine(sr);
        if (headerFields == null) return rows;

        while (true)
        {
            var fields = ParseCsvLine(sr);
            if (fields == null) break;

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headerFields.Count; i++)
            {
                row[headerFields[i]] = i < fields.Count ? fields[i] : "";
            }
            rows.Add(row);
        }
        return rows;
    }

    private static List<string>? ParseCsvLine(StreamReader sr)
    {
        if (sr.EndOfStream) return null;
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        while (true)
        {
            int ci = sr.Read();
            if (ci < 0)
            {
                if (sb.Length > 0 || fields.Count > 0) fields.Add(sb.ToString());
                return fields.Count > 0 ? fields : null;
            }
            char c = (char)ci;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (sr.Peek() == '"') { sr.Read(); sb.Append('"'); }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == ',')           { fields.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"')      { inQuotes = true; }
                else if (c == '\r')     { /* swallow */ }
                else if (c == '\n')     { fields.Add(sb.ToString()); return fields; }
                else                    { sb.Append(c); }
            }
        }
    }

    // ====================================================================
    //  Cleanup
    // ====================================================================

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ad?.Dispose();
            _searchTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
