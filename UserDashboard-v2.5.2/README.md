# UserDashboard

A .NET Framework 4.8 / WinForms desktop dashboard for managing Active Directory
users on a domain-joined Windows machine. C# port of `UserDashboard.ps1`.

## Features

### Core
- DataGridView listing every AD user (excluding service / built-in accounts)
- Multi-token AND search with quoted-phrase support: `smith admin`, `"global shop"`
- Group filter dropdown
- **Inline cell editing** of First Name / Middle Initials / Last Name / Display Name
  → writes straight back to AD on commit
- Right-click row context menu with single-user actions:
  - Reset Password (with password generator + live requirements checklist)
  - Disable / Enable User (label flips to match current state)
  - Manage Security Groups (checkbox list)
  - **Company Codes** (Global Shop visibility): one AD group per code (`CC-100`).
    Users with no company-code groups are unrestricted and see every code.
- "Add User" dialog modeled on the AD `New Object - User` wizard
- CSV Import (bulk create users) and CSV Export (current view)
- Per-day audit log at `C:\GSS\UserDashboard\Log\audit_yyyy-MM-dd.csv`
- Disabled accounts are tinted light-red and italicized

### v2 Enhancements (April 2026)

- **Async loading + marquee progress bar** — UI never freezes during AD enumeration
  or other long-running operations. Toolbar is disabled while busy so users can't
  fire two operations simultaneously.

- **Multi-select + bulk operations.** Shift/Ctrl-click multiple rows, then right-click
  to bulk Enable, Disable, or Reset Passwords. Bulk reset auto-generates a unique
  password per user and offers a one-time CSV download of the user→password mapping.

- **Keyboard shortcuts:**
  - `F5` — reload from AD
  - `Ctrl+F` — focus search
  - `Esc` — clear search (when focused)
  - `Ctrl+N` — Add User
  - `Delete` — disable/enable selected user(s)
  - `Enter` — reset password (single selection)

- **Right-click "Copy" submenu** — copy username (UPN), display name, email, or
  SAM account name to clipboard. Works on multiple rows (joined by newlines)
  for easy paste into tickets, scripts, or emails.

- **Persisted state** — window position, size, maximized state, and per-column
  widths are saved to `%APPDATA%\UserDashboard\settings.xml` and restored next
  time the app opens.

## Project layout (flat)

```
UserDashboard/
├── UserDashboard.csproj         .NET Framework 4.8 / WinForms
├── app.manifest                 DPI-aware, asInvoker
├── UserDashboard.ico            App icon (set via <ApplicationIcon>)
├── UserDashboard-rds.ico        RDS-safe icon (BMP-only, for RemoteApp)
├── Polyfills.cs                 IsExternalInit stub for record types
├── Program.cs                   Entry point + AD preflight
├── Constants.cs                 Brand colors, excluded users/groups
├── CompanyCodeHelper.cs         Global Shop company-code group naming
├── MainForm.cs                  Main dashboard (grid + toolbar + context menu)
├── AdUserInfo.cs                Cached snapshot of one AD user
├── AdService.cs                 All Active Directory operations
├── AuditLogger.cs               CSV audit log
├── PasswordHelper.cs            Password rules + secure random generator
├── PasswordChecklist.cs         Reusable live-requirements UserControl
├── UserSettings.cs              XML-serialized window/column persistence (NEW v2)
├── AddUserForm.cs               "New Object - User" dialog
├── ResetPasswordForm.cs         Password reset dialog
├── ManageGroupsForm.cs          Group membership dialog
├── CompanyCodesForm.cs          Per-user Global Shop company codes
├── BulkCompanyCodesForm.cs      Bulk company-code assignment
└── README/Apply-CompanyCodes.ps1  Logon script that writes GSSLOGIN.DIR
```

## Build & run

Requires **.NET 8 SDK** (for the `dotnet` CLI) and **.NET Framework 4.8 Developer
Pack** (for compiling against net48).

```powershell
cd C:\Dev\UserDashboard
dotnet build -c Release
dotnet run -c Release
```

The distributable EXE is at:
```
bin\Release\net48\UserDashboard.exe
```

Just copy that EXE (with its `.config` and any sibling `.dll`s) to any
domain-joined Windows machine. .NET Framework 4.8 ships with Windows 10 1809+
and Windows Server 2019+ — no separate runtime needed.

## Settings file

User preferences are stored at:
```
%APPDATA%\UserDashboard\settings.xml
```
Delete this file to reset window position and column widths to defaults.

## Audit log

Every mutating operation (edit, enable, disable, reset password, group change,
company codes, create, import) is appended to a daily CSV at:
```
C:\GSS\UserDashboard\Log\audit_yyyy-MM-dd.csv
```
Columns: Timestamp, Operator, Machine, Action, Target, Details.

## Runtime requirements

- Windows 10 / 11 or Windows Server 2016+
- Machine joined to an Active Directory domain
- The launching user needs read access to the domain (and write access to
  whatever attributes / groups they intend to modify)

## Common operational tips

- **Bulk operations require explicit confirmation** with a preview list of
  affected accounts. Bulk password resets only ever show the new passwords once,
  via the optional CSV save dialog.
- **Audit before bulk action** — every individual operation in a bulk run is
  logged separately, so the audit trail is identical to running each one by hand.
- **Window position not saved?** Check that `%APPDATA%\UserDashboard\` is
  writable. The app silently swallows save failures so a locked-down profile
  doesn't prevent it from running.
- **Running over RemoteApp:** publish using `UserDashboard-rds.ico` as the icon
  (the regular `.ico` uses PNG-encoded entries which RDWeb's icon pipeline
  doesn't handle).

## Company codes (Global Shop)

Replaces the old per-combination GPO + `C:\GSS\PowerShell\<code>` copy of
`GSSLOGIN.DIR`.

**Access model**

- One global security group per company code in the Users container, named
  `CC-<code>` (configurable via `CompanyCodeGroupPrefix`).
- The user's default code is stored in the AD `comment` field as `GSSCC:PLA`
  (`CompanyCodePrimaryAttribute`). This is a base-schema attribute; Exchange
  `extensionAttribute*` fields are not required.
- **Users assigned to no company-code groups are unrestricted** — they see
  every company code. The logon script deletes `%USERPROFILE%\GSSLOGIN.DIR`
  in that case so a leftover restriction file cannot linger.
- Assigning one or more codes restricts that user to the checked list.

**Dashboard**

Right-click a user → **Company Codes...** (or bulk-select → **Company Codes
for Selected Users...**). Tick codes, set the default, or add a new code
(creates the AD group). Company-code groups are hidden from the generic
Manage Security Groups dialog so assignment stays in this screen.

**How Global Shop actually sees the change**

AD groups are only the source of truth. Global Shop reads
`%USERPROFILE%\GSSLOGIN.DIR` at launch. The GPO logon script writes that
file. The dashboard also writes it on Apply when the user's profile exists
on this machine (close and reopen Global Shop after Apply).

**GPO setup (same pattern as the original company-code GPOs)**

The original VBS did not run PowerShell from SYSVOL. It launched a **local**
script already on the RDS host (`C:\GSS\PowerShell\<code>\...`). This GPO
works the same way, with one shared folder instead of one folder per code.

1. Run `README\Setup-UserDashboard - Automated.ps1`. **STEP 4c** detects
   `C:\GSS` on each session host, finds an existing `PowerShell` folder
   (any casing) or creates `C:\GSS\PowerShell`, creates `CompanyCodes`,
   and copies `Apply-CompanyCodes.ps1` and `Apply-CompanyCodes.vbs` from
   this README folder. Re-running overwrites the two files. Skip with
   `-SkipCompanyCodes` if you ever need to.
2. Group Policy Management → right-click the **domain** → **Create a GPO
   in this domain, and link it here** → name it e.g. `User Company Codes
   (GSSLOGIN)`. Link at the domain so **both user and computer** settings
   apply to RDS hosts. Do not link it only to a Users OU.
3. Leave **Authenticated Users** with Read and **Apply group policy**.
   Do not security-filter this GPO to `CC-*` groups (unlike the old
   per-combination GPOs). The script is a no-op for unassigned users.
4. Edit the GPO and set these so logon scripts actually run on RDS
   (Fast Logon Optimization otherwise skips them):
   - **Computer Configuration → Policies → Administrative Templates →
     System → Logon → Always wait for the network at computer startup and
     logon** = Enabled
   - **Computer Configuration → Policies → Administrative Templates →
     System → Scripts → Run logon scripts synchronously** = Enabled
   - **User Configuration → Policies → Administrative Templates →
     System → Scripts → Run logon scripts synchronously** = Enabled
5. **User Configuration → Policies → Windows Settings → Scripts → Logon**
   - Click **Show Files** and copy **both** `Apply-CompanyCodes.vbs` and
     `Apply-CompanyCodes.ps1` into that SYSVOL folder.
   - **Add** → Browse → `Apply-CompanyCodes.vbs` → OK → Apply.
     The VBS launches `C:\GSS\PowerShell\CompanyCodes\Apply-CompanyCodes.ps1`
     when that file exists, otherwise the copy next to the VBS.
6. Disable the old per-combination company-code GPOs and remove
   `C:\GSS\PowerShell\<code>` folders so they cannot overwrite GSSLOGIN.DIR.
7. On an RDS host run `gpupdate /force`, then **Sign out** TEST (not
   disconnect) and log on again.

**Confirm the GPO applied** (as TEST, after a full sign-out):

```
gpresult /r
notepad %TEMP%\Apply-CompanyCodes.log
notepad %USERPROFILE%\GSSLOGIN.DIR
```

`gpresult` must list `User Company Codes (GSSLOGIN)` under **Applied Group
Policy Objects**. The log must exist; if it does not, this GPO did not run
(loopback replace on the RDS OU, blocked inheritance, or the VBS was added
without Show Files). If the log exists but GSSLOGIN.DIR still shows an old
code, an old GPO is still copying a file after this one — give this GPO
the highest link order, then disable the old ones.
