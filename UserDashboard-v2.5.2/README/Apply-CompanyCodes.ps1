#Requires -Version 5.1
<#
.SYNOPSIS
    Writes or removes GSSLOGIN.DIR from the logged-on user's company-code
    AD group membership.

.DESCRIPTION
    Deploy as a user logon script (GPO User Configuration > Policies >
    Windows Settings > Scripts > Logon). The GPO VBS launches this file from
    C:\GSS\PowerShell\CompanyCodes on each RDS host (same pattern as the
    original Company_Code_Selection VBS). Replaces the old per-combination
    GPO + C:\GSS\PowerShell\<code> copy.

    Policy (must match User Dashboard):
      * One or more CC-* groups   -> write GSSLOGIN.DIR listing those codes.
        SELECTEDCOMPANYCODE comes from the AD comment field (GSSCC:CODE)
        when that code is still assigned; otherwise the first assigned code
        is used.
      * No CC-* group membership:
          - If $CoexistenceWithLegacyGpo is $true (default): do not touch
            GSSLOGIN.DIR. Whatever this customer's existing GPO already
            copied (any group name, any combination) is left in place.
          - If $false: unrestricted. Delete GSSLOGIN.DIR so Global Shop
            shows every company code. Use this only after the old GPOs are
            disabled.

    Membership is read from AD (not the logon token) so a group change in
    User Dashboard takes effect on the next full logoff/logon even if a
    Kerberos PAC is briefly stale.

    Keep $GroupPrefix in sync with UserDashboard.config.json
    (CompanyCodeGroupPrefix) or the compiled default CC-.

    A diagnostic log is appended to %TEMP%\Apply-CompanyCodes.log.

    This script never throws out to the logon engine: any failure is swallowed
    so a directory hiccup cannot block the desktop.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# --- Keep these in sync with User Dashboard --------------------------------
$GroupPrefix      = 'CC-'
$PrimaryAttribute = 'comment'
$PrimaryMarker    = 'GSSCC:'
$ListSeparator    = '*!*'
$DestFileName     = 'GSSLOGIN.DIR'

# Existing environments still have their own company-code GPOs (names and
# combinations differ per customer) that copy a static GSSLOGIN.DIR. While
# those GPOs are linked, a user with no CC-* groups is a *legacy* user, not
# an unrestricted one. Leave their file alone.
# Set this to $false after every user is on CC-* groups and the old GPOs are
# disabled, so unassigned users see every company code.
$CoexistenceWithLegacyGpo = $true

function Write-CcLog {
    param([string]$Message)
    try {
        $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Message
        Add-Content -LiteralPath (Join-Path $env:TEMP 'Apply-CompanyCodes.log') -Value $line -ErrorAction SilentlyContinue
    } catch { }
}

function Get-GssLoginPaths {
    $paths = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        [void]$paths.Add((Join-Path $env:USERPROFILE $DestFileName))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERNAME)) {
        $legacy = Join-Path ('C:\Users\' + $env:USERNAME) $DestFileName
        if (-not $paths.Contains($legacy)) { [void]$paths.Add($legacy) }
    }
    return $paths
}

function Get-UserCompanyCodeState {
    $codes = New-Object System.Collections.Generic.List[string]
    $primary = $null

    $sam = $env:USERNAME
    if ([string]::IsNullOrWhiteSpace($sam)) {
        return [pscustomobject]@{ Codes = @(); Primary = $null }
    }

    $searcher = New-Object System.DirectoryServices.DirectorySearcher
    try {
        $searcher.Filter = "(&(objectCategory=person)(objectClass=user)(sAMAccountName=$sam))"
        $searcher.SearchScope = 'Subtree'
        [void]$searcher.PropertiesToLoad.Add('memberOf')
        [void]$searcher.PropertiesToLoad.Add($PrimaryAttribute)
        $result = $searcher.FindOne()
    } finally {
        $searcher.Dispose()
    }

    if (-not $result) {
        return [pscustomobject]@{ Codes = @(); Primary = $null }
    }

    foreach ($dn in @($result.Properties['memberOf'])) {
        if (-not $dn) { continue }
        $cn = ([string]$dn -split ',', 2)[0]
        if ($cn.StartsWith('CN=', [System.StringComparison]::OrdinalIgnoreCase)) {
            $cn = $cn.Substring(3)
        }
        if ($cn.Length -le $GroupPrefix.Length) { continue }
        if (-not $cn.StartsWith($GroupPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $code = $cn.Substring($GroupPrefix.Length).Trim().ToUpperInvariant()
        if ($code.Length -gt 0 -and -not $codes.Contains($code)) {
            [void]$codes.Add($code)
        }
    }

    if ($result.Properties[$PrimaryAttribute].Count -gt 0) {
        $raw = [string]$result.Properties[$PrimaryAttribute][0]
        if ($raw -match ([regex]::Escape($PrimaryMarker) + '([A-Z0-9-]+)')) {
            $primary = $Matches[1].ToUpperInvariant()
        } elseif ($PrimaryAttribute -notmatch '^(comment|info)$' -and -not [string]::IsNullOrWhiteSpace($raw)) {
            $primary = $raw.Trim().ToUpperInvariant()
        }
    }

    return [pscustomobject]@{
        Codes   = @($codes | Sort-Object)
        Primary = $primary
    }
}

function Remove-GssLoginFile {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        Write-CcLog "Removed $Path"
    }
}

function Write-GssLoginFile {
    param(
        [string]$Path,
        [string]$Content
    )
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { return }
    $ascii = [System.Text.Encoding]::ASCII
    [System.IO.File]::WriteAllText($Path, $Content, $ascii)
    Write-CcLog "Wrote $Path"
}

try {
    $paths = @(Get-GssLoginPaths)
    Write-CcLog ("User={0} Profile={1}" -f $env:USERNAME, $env:USERPROFILE)

    $state = Get-UserCompanyCodeState
    $codes = @($state.Codes)
    Write-CcLog ("AD codes=[{0}] storedPrimary={1}" -f ($codes -join ','), $state.Primary)

    if ($codes.Count -eq 0) {
        if ($CoexistenceWithLegacyGpo) {
            Write-CcLog 'No CC-* groups; coexistence on - leaving GSSLOGIN.DIR for legacy GPOs.'
            exit 0
        }
        foreach ($p in $paths) { Remove-GssLoginFile -Path $p }
        Write-CcLog 'Unrestricted (no CC-* groups).'
        exit 0
    }

    $selected = $null
    if ($state.Primary) {
        foreach ($code in $codes) {
            if ($code -eq $state.Primary) { $selected = $code; break }
        }
    }
    if ([string]::IsNullOrWhiteSpace($selected)) { $selected = $codes[0] }

    $list = [string]::Join($ListSeparator, $codes)
    $content = "SELECTEDCOMPANYCODE::$selected`r`nCOMPANYCODELIST::$list`r`n"
    Write-CcLog ("SELECTED={0} LIST={1}" -f $selected, $list)

    foreach ($p in $paths) { Write-GssLoginFile -Path $p -Content $content }
} catch {
    Write-CcLog ("ERROR: " + $_.Exception.Message)
    exit 0
}
