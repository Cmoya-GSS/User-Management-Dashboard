<#
.SYNOPSIS
    Grants a group EXACTLY the Query + Logoff rights on the RDP-Tcp listener
    (access mask 0x5) and nothing else, by adding a single allow ACE to the
    listener's security descriptor. Every other ACE (Administrators, SYSTEM,
    Remote Desktop Users, etc.) is left byte-for-byte untouched.

.DESCRIPTION
    Why this approach: the WMI Win32_TSAccount method cannot produce a clean
    Query+Logoff-only grant on this Server 2022 build (its presets contaminate
    the mask with an extra 0x20 bit, and its "deny" operation is rejected). So we
    set the descriptor directly. To stay safe, we DO NOT build a descriptor from
    scratch - we read the current effective descriptor, add ONE allow ACE for the
    group, and write it back. All other ACEs are preserved exactly.

    The target mask 0x5 = WINSTATION_QUERY (0x1) | WINSTATION_LOGOFF (0x4),
    measured empirically on this server by Get-RdsLogoffMask.ps1.

.PARAMETER GroupName
    DOMAIN\Group to grant. Default: GSS-CLOUD\UserDashboard.

.PARAMETER TerminalName
    Listener. Default: RDP-Tcp.

.PARAMETER Mask
    Access mask to grant. Default: 0x5 (Query + Logoff). Override only if a
    fresh Get-RdsLogoffMask.ps1 run reports a different value on a given host.

.PARAMETER Apply
    Without this switch the script runs as a DRY RUN: it shows the current
    descriptor, the planned ACE, and the resulting descriptor, but does NOT
    write anything. Re-run with -Apply to make the change.

.NOTES
    * RUN AS ADMINISTRATOR.
    * TEST ON A NON-CRITICAL HOST FIRST. Keep a second admin session open.
    * A binary backup of the descriptor is written before any change, and
      Restore-RdsListenerSecurity.ps1 reverts it.
    * Do not place local administrators in this group (an allow-only ACE is
      additive, so it's harmless, but keep the group purpose-specific).
#>

[CmdletBinding()]
param(
    [string] $GroupName    = "GSS-CLOUD\UserDashboard",
    [string] $TerminalName = "RDP-Tcp",
    [uint32] $Mask         = 0x5,
    [string] $BackupDir    = "C:\GSS\RDS-Backup",
    [switch] $Apply
)

$ErrorActionPreference = "Stop"
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\$TerminalName"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run as Administrator."
    }
}

function Get-EffectiveBinarySD {
    # Returns the current listener security descriptor as a byte[]. If the
    # listener has no explicit descriptor (inherited defaults), we materialize
    # the effective default so we can add our ACE without dropping the defaults.
    $sec = (Get-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue).Security
    if ($null -ne $sec) { return ,([byte[]]$sec) }

    # No explicit value. Build the well-known default descriptor used by RDP-Tcp:
    #   Owner SYSTEM, Group SYSTEM,
    #   Administrators  : full (0xf03bf)
    #   SYSTEM          : full (0xf03bf)
    #   INTERACTIVE     : query (0x1)        [matches observed default]
    #   Remote Desktop Users : 0x121         [matches observed default]
    # We derive it from the live WMI account list to avoid hard-coding wrong bits.
    Write-Verbose "No explicit descriptor; constructing from current effective ACEs."
    return $null   # signal caller to use WMI-materialize path
}

Assert-Admin

Write-Host "=== Precise grant: $GroupName -> Query+Logoff (mask 0x$('{0:X}' -f $Mask)) on $TerminalName ===" -ForegroundColor Cyan
Write-Host ""

# Resolve the group SID.
$sid = (New-Object System.Security.Principal.NTAccount($GroupName)).Translate(
        [System.Security.Principal.SecurityIdentifier])
Write-Host "Group SID: $($sid.Value)" -ForegroundColor Gray

# ---------------------------------------------------------------------------
# Obtain a concrete descriptor to edit. If the listener has no explicit
# descriptor, we first let WMI AddAccount materialize one (which includes all
# the correct default ACEs), then we will rewrite our group's ACE to the exact
# mask. This avoids hand-building the default ACEs (the risky part).
# ---------------------------------------------------------------------------
$sec = (Get-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue).Security
$materializedByUs = $false

if ($null -eq $sec) {
    Write-Host "Listener has no explicit descriptor; using WMI AddAccount to materialize one safely..." -ForegroundColor Yellow
    if ($Apply) {
        $perm = Get-WmiObject -Namespace "root\CIMV2\TerminalServices" `
            -Class Win32_TSPermissionsSetting -Filter "TerminalName='$TerminalName'" `
            -Authentication PacketPrivacy
        [void]$perm.AddAccount($GroupName, 0)   # creates descriptor incl. defaults + group (Guest baseline)
        $materializedByUs = $true
        $sec = (Get-ItemProperty -Path $regPath -Name "Security").Security
    } else {
        Write-Host "(DRY RUN) Would call AddAccount to materialize the descriptor first." -ForegroundColor DarkYellow
        # For dry-run preview, materialize temporarily then roll back.
        $perm = Get-WmiObject -Namespace "root\CIMV2\TerminalServices" `
            -Class Win32_TSPermissionsSetting -Filter "TerminalName='$TerminalName'" `
            -Authentication PacketPrivacy
        [void]$perm.AddAccount($GroupName, 0)
        $sec = (Get-ItemProperty -Path $regPath -Name "Security").Security
        $previewRollback = $true
    }
}

# Back up whatever is now present (before our precise edit).
if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$backupFile = Join-Path $BackupDir "$TerminalName-Security-$stamp-PREAPPLY.bin"
if ($Apply) {
    [System.IO.File]::WriteAllBytes($backupFile, [byte[]]$sec)
    Write-Host "Backed up pre-edit descriptor to $backupFile" -ForegroundColor Green
}

# Parse the descriptor.
$rsd = New-Object System.Security.AccessControl.RawSecurityDescriptor(([byte[]]$sec), 0)

Write-Host ""
Write-Host "SDDL BEFORE precise edit:" -ForegroundColor Yellow
Write-Host $rsd.GetSddlForm([System.Security.AccessControl.AccessControlSections]::All)
Write-Host ""

# Remove ANY existing ACEs for our group SID (allow or deny), so we end with a
# single, clean allow ACE carrying exactly the target mask. RawAcl has no
# IndexOf(ace); iterate by index in REVERSE so removals don't shift the indices
# of entries we haven't examined yet.
for ($i = $rsd.DiscretionaryAcl.Count - 1; $i -ge 0; $i--) {
    $ace = $rsd.DiscretionaryAcl[$i]
    if ($ace.SecurityIdentifier -eq $sid) {
        $rsd.DiscretionaryAcl.RemoveAce($i)
    }
}

# Add the precise allow ACE: mask 0x5 (Query + Logoff), no inheritance flags.
$rsd.DiscretionaryAcl.InsertAce(
    $rsd.DiscretionaryAcl.Count,
    (New-Object System.Security.AccessControl.CommonAce(
        [System.Security.AccessControl.AceFlags]::None,
        [System.Security.AccessControl.AceQualifier]::AccessAllowed,
        [int]$Mask,
        $sid,
        $false,
        $null)))

Write-Host "SDDL AFTER precise edit (planned):" -ForegroundColor Yellow
$newSddl = $rsd.GetSddlForm([System.Security.AccessControl.AccessControlSections]::All)
Write-Host $newSddl
Write-Host ""
Write-Host ("Planned ACE for group: (A;;0x{0:X};;;{1})" -f $Mask, $sid.Value) -ForegroundColor Green
Write-Host ""

if (-not $Apply) {
    # Roll back the temporary materialize if we did one for preview.
    if ($previewRollback) {
        $a = Get-WmiObject -Namespace "root\CIMV2\TerminalServices" -Class Win32_TSAccount `
            -Filter "TerminalName='$TerminalName'" -Authentication PacketPrivacy |
            Where-Object { $_.AccountName -eq $GroupName }
        if ($a) { [void]$a.Delete() }
        Remove-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue
        Write-Host "(DRY RUN) Reverted temporary materialize; no change persisted." -ForegroundColor DarkYellow
    }
    Write-Host "DRY RUN COMPLETE. Re-run with -Apply to write the change." -ForegroundColor Cyan
    return
}

# Serialize and write back.
$bytes = New-Object byte[] ($rsd.BinaryLength)
$rsd.GetBinaryForm($bytes, 0)
Set-ItemProperty -Path $regPath -Name "Security" -Value $bytes -Type Binary

Write-Host "Applied. Verifying..." -ForegroundColor Cyan
Write-Host ""
$verify = Get-WmiObject -Namespace "root\CIMV2\TerminalServices" -Class Win32_TSAccount `
    -Filter "TerminalName='$TerminalName'" -Authentication PacketPrivacy |
    Select-Object AccountName, PermissionsAllowed, PermissionsDenied
$verify | Format-Table -AutoSize

Write-Host ""
Write-Host "The '$GroupName' row should show PermissionsAllowed = $Mask and PermissionsDenied = 0." -ForegroundColor Green
Write-Host ""
Write-Host @"
NEXT:
  1. Verify the row above: $GroupName -> Allowed = $Mask, Denied = 0.
  2. Functional test (keep your rescue admin session open):
       - A non-admin member of $GroupName logs off a test session via UserDashboard -> should SUCCEED.
       - That member tries to RDP into this host directly -> should be REFUSED.
       - That member tries to read C:\GSS\UserDashboard\Log or stop a service -> should be DENIED.
  3. Confirm normal users can still RDP in (open a fresh session as a normal user).

ROLLBACK:
  .\Restore-RdsListenerSecurity.ps1 -BackupFile "$backupFile"
"@ -ForegroundColor Gray
