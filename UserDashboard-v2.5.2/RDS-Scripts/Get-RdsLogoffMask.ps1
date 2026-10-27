<#
.SYNOPSIS
    Determines the EXACT RDP-Tcp access-mask bit values for Query and Logoff on
    THIS server, so a precise security descriptor can be built. Measures the
    values empirically (lets Windows compute them), prints an analysis, then
    cleans up so no lasting change is made.

.DESCRIPTION
    Safe and reversible. The script:
      1. Backs up the current listener security descriptor.
      2. Temporarily adds the group at the minimal (Guest) preset and reads the
         baseline mask.
      3. Allows Query, then Logoff, reading the mask after each so we can isolate
         exactly which bit each permission corresponds to on this build.
      4. Removes the temporary grant, restoring the listener to its prior state.
      5. Prints the determined bit values and the recommended target mask.

    Nothing permanent is changed. Run this, paste the output back, and the precise
    "apply" script will use the measured mask.

.PARAMETER GroupName
    The group to use for measurement. Default: GSS-CLOUD\UserDashboard.

.PARAMETER TerminalName
    Listener name. Default: RDP-Tcp.

.NOTES
    RUN AS ADMINISTRATOR. Keep a second admin session open as a precaution
    (though this script restores the prior state automatically).
#>

[CmdletBinding()]
param(
    [string] $GroupName    = "GSS-CLOUD\UserDashboard",
    [string] $TerminalName = "RDP-Tcp",
    [string] $BackupDir    = "C:\GSS\RDS-Backup"
)

$ErrorActionPreference = "Stop"
$ns      = "root\CIMV2\TerminalServices"
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\$TerminalName"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run as Administrator."
    }
}

function Get-Acct {
    param([string]$Group, [string]$Term)
    Get-WmiObject -Namespace $ns -Class Win32_TSAccount `
        -Filter "TerminalName='$Term'" -Authentication PacketPrivacy |
        Where-Object { $_.AccountName -eq $Group } | Select-Object -First 1
}

function Show-Mask {
    param([string]$Label, [uint32]$Mask)
    $bits = @()
    for ($i = 0; $i -lt 24; $i++) {
        $b = [uint32]([math]::Pow(2, $i))
        if ($Mask -band $b) { $bits += ("0x{0:X}" -f $b) }
    }
    Write-Host ("  {0,-28} = {1,-8} (0x{2:X})  bits: {3}" -f $Label, $Mask, $Mask, ($bits -join " "))
}

Assert-Admin

Write-Host "=== Determine exact Query/Logoff mask bits on $TerminalName ===" -ForegroundColor Cyan
Write-Host ""

# --- Back up current state -------------------------------------------------
if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$secBefore = (Get-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue).Security
$hadExplicit = $null -ne $secBefore
if ($hadExplicit) {
    $bk = Join-Path $BackupDir "$TerminalName-Security-$stamp-DETERMINE.bin"
    [System.IO.File]::WriteAllBytes($bk, $secBefore)
    Write-Host "Backed up existing descriptor to $bk" -ForegroundColor Green
} else {
    Write-Host "No explicit descriptor present (inherited defaults). Will revert to that after measuring." -ForegroundColor Green
}
Write-Host ""

try {
    # --- Add group at Guest (minimal) preset, measure baseline ------------
    $perm = Get-WmiObject -Namespace $ns -Class Win32_TSPermissionsSetting `
        -Filter "TerminalName='$TerminalName'" -Authentication PacketPrivacy
    [void]$perm.AddAccount($GroupName, 0)

    $acct = Get-Acct -Group $GroupName -Term $TerminalName
    if ($null -eq $acct) { throw "Could not locate the group account after AddAccount." }
    $baseline = [uint32]$acct.PermissionsAllowed

    # --- Allow Query, measure ---------------------------------------------
    [void]$acct.ModifyPermissions(0, $true)
    $afterQuery = [uint32](Get-Acct -Group $GroupName -Term $TerminalName).PermissionsAllowed

    # --- Allow Logoff, measure --------------------------------------------
    [void]$acct.ModifyPermissions(2, $true)
    $afterLogoff = [uint32](Get-Acct -Group $GroupName -Term $TerminalName).PermissionsAllowed

    # --- Analysis ----------------------------------------------------------
    $queryBit  = $afterQuery  -band (-bnot $baseline)      # bit added by allowing Query
    $logoffBit = $afterLogoff -band (-bnot $afterQuery)    # bit added by allowing Logoff
    if ($queryBit  -eq 0) { $queryBit  = 0x1 }             # known reference: INTERACTIVE = 1 = Query

    $target = ([uint32]$queryBit) -bor ([uint32]$logoffBit)

    Write-Host "Measured masks:" -ForegroundColor Yellow
    Show-Mask "Guest baseline"            $baseline
    Show-Mask "after allow Query"         $afterQuery
    Show-Mask "after allow Query+Logoff"  $afterLogoff
    Write-Host ""
    Write-Host "Derived bits:" -ForegroundColor Yellow
    Show-Mask "Query bit"   $queryBit
    Show-Mask "Logoff bit"  $logoffBit
    Write-Host ""
    Write-Host ("RECOMMENDED TARGET MASK (Query | Logoff) = {0} (0x{0:X})" -f $target) -ForegroundColor Green
    Write-Host ""
    Write-Host "Reference (read earlier from this listener): NT AUTHORITY\INTERACTIVE = 1 = pure Query." -ForegroundColor Gray
}
finally {
    # --- Clean up: remove the temporary grant, restore prior state --------
    Write-Host ""
    Write-Host "Cleaning up (removing temporary grant)..." -ForegroundColor Cyan
    $a = Get-Acct -Group $GroupName -Term $TerminalName
    if ($a) { [void]$a.Delete() }

    if (-not $hadExplicit) {
        # Listener had no explicit descriptor originally; AddAccount created one.
        # Remove it to return to inherited defaults.
        Remove-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue
        Write-Host "Reverted listener to inherited defaults (no explicit descriptor)." -ForegroundColor Green
    } else {
        # Restore the original explicit descriptor we backed up.
        [System.IO.File]::ReadAllBytes($bk) | Out-Null
        Set-ItemProperty -Path $regPath -Name "Security" -Value ([System.IO.File]::ReadAllBytes($bk)) -Type Binary
        Write-Host "Restored original explicit descriptor from backup." -ForegroundColor Green
    }

    # Verify cleanup
    $check = Get-Acct -Group $GroupName -Term $TerminalName
    if ($null -eq $check) {
        Write-Host "Verified: no '$GroupName' entry remains on the listener." -ForegroundColor Green
    } else {
        Write-Warning "A '$GroupName' entry still exists - run the restore script with your DETERMINE backup if needed."
    }
}

Write-Host ""
Write-Host "=== DONE - paste the output above (especially the TARGET MASK line) ===" -ForegroundColor Cyan
