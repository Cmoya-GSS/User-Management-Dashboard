<#
.SYNOPSIS
    Restores the RDP-Tcp listener's security descriptor from a backup created by
    Grant-RdsQueryLogoff.ps1. Use this to cleanly undo the delegation, or to
    recover if a permission change had an unintended effect.

.PARAMETER BackupFile
    Path to the .bin backup produced by the grant script (the binary security
    descriptor of the listener as it was before the change).

.PARAMETER TerminalName
    The listener to restore. Default: RDP-Tcp.

.NOTES
    * RUN AS ADMINISTRATOR on the session host.
    * Keep a second administrative session open while doing this, in case the
      listener needs a moment to apply the restored descriptor.
    * If the backup file contains the marker "NO_EXPLICIT_SECURITY_VALUE", the
      listener originally had no explicit descriptor (it used inherited defaults).
      In that case this script removes the explicit Security value so the
      listener reverts to those defaults.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BackupFile,
    [string] $TerminalName = "RDP-Tcp"
)

$ErrorActionPreference = "Stop"

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$p  = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as Administrator."
}

if (-not (Test-Path $BackupFile)) {
    throw "Backup file not found: $BackupFile"
}

$regPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\$TerminalName"
if (-not (Test-Path $regPath)) {
    throw "Listener registry key not found: $regPath"
}

# Detect the "no explicit descriptor" marker.
$firstBytes = $null
try { $firstBytes = Get-Content -Path $BackupFile -TotalCount 1 -ErrorAction SilentlyContinue } catch {}

if ($firstBytes -eq "NO_EXPLICIT_SECURITY_VALUE") {
    Write-Host "Backup indicates no explicit security value originally. Removing the explicit Security value to revert to inherited defaults..." -ForegroundColor Yellow
    Remove-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue
    Write-Host "Done. Listener reverted to default (Administrators + SYSTEM full access)." -ForegroundColor Green
}
else {
    $bytes = [System.IO.File]::ReadAllBytes($BackupFile)
    Set-ItemProperty -Path $regPath -Name "Security" -Value $bytes -Type Binary
    Write-Host "Restored listener security descriptor from $BackupFile." -ForegroundColor Green
}

Write-Host ""
Write-Host "Current SDDL after restore:" -ForegroundColor Yellow
$sec = (Get-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue).Security
if ($null -eq $sec) {
    Write-Host "(no explicit security value - inherited defaults)"
} else {
    try {
        $helper = [wmiclass]"Win32_SecurityDescriptorHelper"
        Write-Host ($helper.BinarySDToSDDL($sec).SDDL)
    } catch {
        Write-Host "(could not convert to SDDL: $($_.Exception.Message))"
    }
}
