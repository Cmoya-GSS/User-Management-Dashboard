<#
.SYNOPSIS
    One-stop UserDashboard deployment (targets UserDashboard v2.4.1). Creates the UserDashboard AD group,
    delegates OU control, publishes the RemoteApp, stamps NTFS permissions on
    every session host, AND grants the group the precise Query + Logoff rights
    on each host's RDP-Tcp listener (the rights that let non-admin members log
    off other sessions through the app).

    This merges the former RDS-Scripts helpers
    (Get-RdsLogoffMask.ps1 / Set-RdsQueryLogoffPrecise.ps1 /
    Restore-RdsListenerSecurity.ps1) into this single script.

.DESCRIPTION
    PREREQUISITE: every intended RDS session host must already be domain-joined
    AND added to the RemoteApps collection BEFORE this script is run. Hosts
    joined or added later will not receive the app, folder ACLs, event-log
    source, or listener rights until you re-run setup (or run it on that host
    with -LocalHostOnly). The script warns about this at startup and, unless
    -Unattended, asks you to confirm.

    NORMAL RUN (no mode switch): first runs a stage-aware PREFLIGHT that verifies
    the environment and STOPS before any change if a blocking check fails (see
    -SkipPreflight). Then executes these stages in order, against the detected
    domain (gss-cloud.com or gsserpgov.com) and the discovered session hosts:

        STEP 0  Stage the build from the versioned script folder into the
                canonical install path C:\GSS\UserDashboard              (local)
        STEP 1  Create the UserDashboard security group + add members      (ADSI)
        STEP 2  Delegate user/group management on the Users OU             (ADSI)
        STEP 3  Publish / update the UserDashboard RemoteApp        (RemoteDesktop)
        STEP 4  Distribute the app folder to every session host        (per host)
        STEP 4b Place a Public Desktop shortcut to the installed EXE   (per host)
        STEP 5  Grant the group NTFS access to the app + Log folders   (per host)
        STEP 5b Register the 'GSS-UserDashboard' Windows Event Log source (per host)
        STEP 6  Grant the group Query + Logoff on the RDP-Tcp listener (per host)

    Steps 1-5 are additive/safe and apply directly. STEP 6 edits a listener
    security descriptor, which is the one operation that can lock people out of
    RDP if it goes wrong, so it always runs a clean dry-run pass first (nothing
    is written). If that pass finds hosts that need the change, an interactive
    session is asked to confirm with (Y/N) before anything is written; pass
    -ApplyListener to skip that prompt (e.g. for scripted/-Unattended runs).

    OUT-OF-BAND MODES (each runs alone, then the script exits):

        -MeasureMask        Read-only diagnostic. On each session host it reads
                            the listener's existing reference principals
                            (INTERACTIVE, Remote Desktop Users, Administrators),
                            confirms the standard Query/Reset/Logoff bit layout,
                            and reports the target mask. Changes nothing.

        -RestoreListener    Recovery. Restores a listener security descriptor
                            from a .bin backup produced by STEP 6 (or reverts to
                            inherited defaults if the backup is the marker file).

    FILE OUTPUT: every backup this script creates is written next to the script
    itself, under <script folder>\RDS-Backup (no more C:\GSS\RDS-Backup). Backups
    are named per host, e.g. RDP-Tcp-Security-RDSH01-20260626_142233-PREAPPLY.bin.

.PARAMETER MeasureMask
    Run the read-only listener-inspection diagnostic on the session hosts, then exit.

.PARAMETER RestoreListener
    Restore a listener descriptor from -BackupFile, then exit.

.PARAMETER BackupFile
    (Restore mode) Path to the .bin backup to restore.

.PARAMETER RestoreHosts
    (Restore mode) Hosts to restore on. Defaults to the local machine.

.PARAMETER ApplyListener
    Skip STEP 6's interactive Y/N confirmation and write the listener ACE
    immediately once the dry-run pass finds hosts that need it. Without it,
    STEP 6 still runs the dry run and, if changes are needed, asks to confirm
    before writing (unless -Unattended is also set, in which case it reports
    the pending changes and does not write without this switch).

.PARAMETER ListenerMask
    Access mask granted in STEP 6. Default 0x25 = Query (0x1) | Reset (0x4) |
    Logoff (0x20). Reset (0x4) is what allows ending ANOTHER user's session -
    Query+Logoff alone (0x21) lets you see sessions but not log off other users.

.PARAMETER TerminalName
    Listener name. Default RDP-Tcp.

.PARAMETER SkipMaskVerification
    Bypass the Step 6 mask-safety gate. By default, before writing the listener
    on any host whose Windows build is not in $VerifiedMaskByBuild, the script
    reads that host's reference principals (read-only) to confirm the standard
    Query/Reset/Logoff layout, and refuses to write
    if they do not equal $ListenerMask. This switch disables that gate and writes
    the mask unconditionally - not recommended.

.PARAMETER DeploySource
    (Step 4) Folder to distribute to the session hosts. Defaults to $AppFolder
    (C:\GSS\UserDashboard on the machine running the script). Point this at a
    build output or staging share if your good copy lives elsewhere.

.PARAMETER DeployVia
    (Step 4) How to copy the folder: WinRM (default) copies over a PowerShell
    session - no SMB or double-hop required, works wherever remoting works. SMB
    uses robocopy to \\<host>\C$\GSS\UserDashboard and needs TCP 445 plus the
    admin C$ share. The Log and RDS-Backup subfolders are never copied.

.PARAMETER LocalHostOnly
    Run the per-host stages (Steps 5, 6, -MeasureMask) against ONLY the machine
    the script is running on - no broker discovery, no WinRM/remoting, no
    RemoteDesktop module needed. Use this to configure session hosts one at a
    time by running the script directly on each host. When doing per-host runs,
    also pass -SkipGroup -SkipDelegation -SkipRemoteApp so the domain-level
    stages (which only need to run once, from the broker) are not repeated.
    Step 4 (folder distribution) is skipped automatically under -LocalHostOnly.

    You usually do NOT need to pass this: the script auto-detects topology at
    startup. If the collection's only session host is this machine (an all-in-one
    box with broker + session host + gateway + web access together), it enables
    local-only targeting on its own. Multi-host deployments fan out to every
    session host. Pass -LocalHostOnly only to force local behavior explicitly.

.PARAMETER AllHosts
    Force multi-host mode: target EVERY session host in the collection (or every
    name in -TerminalServers) and never auto-collapse to -LocalHostOnly. Use this
    if a healthy multi-host collection was mistakenly treated as all-in-one, or to
    guarantee the run fans out to the whole collection from the broker. Mutually
    exclusive with -LocalHostOnly. If discovery cannot resolve the hosts, combine
    with -TerminalServers Host1,Host2 to name them explicitly.

.PARAMETER TerminalServers
    Explicit list of session-host names/FQDNs (e.g. -TerminalServers Library,Library2).
    When provided, this list is used instead of live discovery via Get-RDSessionHost -
    use it whenever discovery is unreliable, or to be certain exactly which hosts a
    run touches. Ignored under -LocalHostOnly (which always targets only the local
    machine). Required alongside -AllHosts if discovery fails.

.PARAMETER SkipGroup / SkipDelegation / SkipStaging / SkipRemoteApp / SkipDeploy / SkipFolderAcl / SkipEventLogSource / SkipListener
    Skip the corresponding stage during a normal run (handy for re-runs and
    targeted troubleshooting). -SkipDeploy also skips Step 4b (Public Desktop
    shortcut).

.PARAMETER Unattended
    Skip interactive confirmations (session-host prerequisite, domain confirm,
    and the "add more users" prompt). The session-host warning is still printed.

.PARAMETER SkipPreflight
    Skip the preflight environment checks. By default a normal run first verifies
    (stage-aware) admin rights, AD reachability, the RemoteDesktop module, that
    this host is the broker owning the collection, session-host discovery, WinRM
    reachability to the hosts it will touch, and backup-folder writability - and
    STOPS (exit 1) on any blocking failure before changing anything. This switch
    overrides that gate (not recommended).

.NOTES
    * SESSION HOSTS FIRST: join each session host to the domain and add it to
      the RDS collection before running this script. Discovery, deploy, ACLs,
      and listener grants only touch hosts that are already in the collection
      (or named with -TerminalServers / -LocalHostOnly).
    * RUN AS ADMINISTRATOR, from an account that can create groups / set ACLs in
      the target OU and is a local admin on every session host.
    * Steps 1 & 2 use native ADSI, so no RSAT / AD module is required (runs on RDS1).
    * Steps 3-6 reach the session hosts; WinRM / PS Remoting must be enabled there
      (Enable-PSRemoting -Force or domain GPO). Step 3 (publish) needs the
      RemoteDesktop module and runs only on the broker that owns the collection
      (verified at run time, not hardcoded). Step 4 (deploy) via SMB additionally
      needs TCP 445 + the admin C$ share on each host.
    * MASK SAFETY: the session-management mask is 0x25 = Query (0x1) | Reset (0x4)
      | Logoff (0x20). Query lets you see sessions, Logoff logs off, and Reset is
      the right that permits ending ANOTHER user's session (Query+Logoff alone is
      not enough to log off other users). These bits are constant across Windows
      Server versions. Common builds are pre-verified in $VerifiedMaskByBuild; an
      unlisted build is checked before any write (see -SkipMaskVerification).
      Still: test STEP 6 with -ApplyListener on a NON-CRITICAL host first, and keep
      a second admin session open as a rescue.
#>

[CmdletBinding()]
param(
    # --- Out-of-band modes (mutually exclusive with a normal run) ---
    [switch]   $MeasureMask,
    [switch]   $RestoreListener,

    # --- Restore inputs ---
    [string]   $BackupFile,
    [string[]] $RestoreHosts,

    # --- Listener safety / tuning ---
    [switch]   $ApplyListener,
    [uint32]   $ListenerMask  = 0x25,
    [string]   $TerminalName  = "RDP-Tcp",
    # MASK SAFETY: by default, before writing the listener on any host whose
    # Windows build is not in the verified-mask table below, the script reads the
    # host's reference principals (read-only) to confirm the standard bit layout and
    # refuses to write if it can't. -SkipMaskVerification bypasses this gate.
    [switch]   $SkipMaskVerification,

    # --- App-folder distribution (Step 4) ---
    # The script can push the app folder from THIS machine out to every session
    # host so you never hand-copy it. Source defaults to $AppFolder below.
    [string]   $DeploySource,                       # override the source folder
    [ValidateSet("WinRM","SMB")]
    [string]   $DeployVia = "WinRM",                 # WinRM = Copy over PS session
                                                     # SMB   = robocopy to \\host\C$
    # --- Target scope for per-host stages (Steps 4, 5, 6, -MeasureMask) ---
    # When set, operate ONLY on the machine the script is running on: no broker
    # discovery, no WinRM/remoting, no RemoteDesktop module required. Use this to
    # configure one session host at a time by running the script directly on it.
    # (Deploy is skipped under -LocalHostOnly: the source folder is already here.)
    [switch]   $LocalHostOnly,

    # Force multi-host behavior: target EVERY discovered session host (or every
    # name in -TerminalServers) and never auto-collapse to -LocalHostOnly. Use
    # this when a healthy multi-host collection was mistakenly treated as
    # all-in-one, or to be explicit that the run must fan out. -AllHosts wins
    # over topology auto-detection; it is mutually exclusive with -LocalHostOnly.
    [switch]   $AllHosts,

    # Explicit session-host list (hostnames or FQDNs). Overrides auto-discovery
    # when provided - use this whenever Get-RDSessionHost can't be relied on, or
    # to be explicit about exactly which hosts a run should touch. Combine with
    # -AllHosts to force fan-out to this exact list, or with -LocalHostOnly to
    # target only this machine regardless of what's listed here.
    [string[]] $TerminalServers = @(),

    # --- Normal-run stage control ---
    [switch]   $SkipGroup,
    [switch]   $SkipDelegation,
    [switch]   $SkipRemoteApp,
    [switch]   $SkipDeploy,
    [switch]   $SkipStaging,
    [switch]   $SkipFolderAcl,
    [switch]   $SkipEventLogSource,
    [switch]   $SkipListener,

    # --- Unattended / safety ---
    [switch]   $Unattended,
    # Skip the preflight environment checks. Preflight runs by default and stops
    # the run (exit 1) on a blocking failure; this switch overrides that gate.
    [switch]   $SkipPreflight
)

$ErrorActionPreference = "Stop"

# ------------------------------------------------------------------------------
# SCRIPT-LOCAL PATHS - all generated files land next to THIS script.
# ($PSScriptRoot is populated when run as a saved .ps1; fall back gracefully.)
# ------------------------------------------------------------------------------
$ScriptRoot = if ($PSScriptRoot)        { $PSScriptRoot }
              elseif ($PSCommandPath)   { Split-Path -Parent $PSCommandPath }
              else                      { (Get-Location).Path }
$BackupDir  = Join-Path $ScriptRoot "RDS-Backup"

# ------------------------------------------------------------------------------
# App version this setup script targets. Used for the runtime banner and for a
# best-effort verification at deploy time that the EXE being pushed matches.
# Bump this when you cut a new dashboard build.
# ------------------------------------------------------------------------------
$AppVersion = "2.4.1"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must be run as Administrator."
    }
}
Assert-Admin

Write-Host "`n=====================================================================" -ForegroundColor Cyan
Write-Host " UserDashboard Setup  -  target app version v$AppVersion"            -ForegroundColor Cyan
Write-Host "=====================================================================" -ForegroundColor Cyan

# ==============================================================================
# DOMAIN DETECTION - no AD module required (uses RootDSE / .NET)
# ==============================================================================
Write-Host "`n[PRE-FLIGHT] Detecting domain..." -ForegroundColor Cyan

try {
    $rootDSE  = [ADSI]"LDAP://RootDSE"
    $DomainDN = $rootDSE.defaultNamingContext
    if ([string]::IsNullOrWhiteSpace($DomainDN)) {
        throw "Could not read defaultNamingContext from RootDSE."
    }
    $DomainFQDN    = ([System.DirectoryServices.ActiveDirectory.Domain]::GetCurrentDomain()).Name
    $DomainNetBIOS = $env:USERDOMAIN

    Write-Host "  FQDN     : $DomainFQDN"    -ForegroundColor Gray
    Write-Host "  NetBIOS  : $DomainNetBIOS" -ForegroundColor Gray
    Write-Host "  DN       : $DomainDN"      -ForegroundColor Gray
    Write-Host "  Domain detected successfully." -ForegroundColor Green
} catch {
    Write-Error "  Could not detect the AD domain. Ensure this machine is domain-joined.`n  Error: $_"
    exit 1
}

# Recognize known GSS environments (commercial vs FedRAMP/Gov).
$KnownDomains = @{
    "gss-cloud.com"  = "GSS Commercial"
    "gsserpgov.com"  = "GSS FedRAMP / GovCloud"
}
if ($KnownDomains.ContainsKey($DomainFQDN)) {
    Write-Host "  Recognized environment: $($KnownDomains[$DomainFQDN]) ($DomainFQDN)" -ForegroundColor Green
} else {
    Write-Host "  WARNING: '$DomainFQDN' is not one of the known GSS domains" -ForegroundColor Yellow
    Write-Host "           (expected: $($KnownDomains.Keys -join ', '))." -ForegroundColor Yellow
    Write-Host "           Proceed only if you are sure this is the correct domain." -ForegroundColor Yellow
}

# ==============================================================================
# CONFIGURATION  (stable GSS settings; domain values filled in automatically)
# ==============================================================================
$GroupName       = "UserDashboard"
$GroupSamName    = "UserDashboard"

# DOMAIN\Group form used for the listener WMI operations (Step 5 / -MeasureMask).
$GroupAccount    = "$DomainNetBIOS\$GroupSamName"

# Auto-resolve where "Users" lives (CN=Users container vs OU=Users).
$UsersCandidates = @("OU=Users,$DomainDN", "CN=Users,$DomainDN")
$OUPath = $null
foreach ($candidate in $UsersCandidates) {
    if ([System.DirectoryServices.DirectoryEntry]::Exists("LDAP://$candidate")) {
        $OUPath = $candidate
        break
    }
}
if (-not $OUPath) {
    Write-Error "  Could not find a 'Users' OU or container under $DomainDN. Set `$OUPath manually."
    exit 1
}
Write-Host "  Target container resolved: $OUPath" -ForegroundColor Gray

$GroupDN               = "CN=$GroupName,$OUPath"
$GROUP_GLOBAL_SECURITY = -2147483646          # 0x80000000 | 0x00000002

$DefaultMembers           = @("Administrator", "Globalshop")
$PromptForAdditionalUsers = (-not $Unattended)

# RemoteApp settings
# The broker host is NOT hardcoded - Step 3 verifies at run time that the local
# machine is the RD Connection Broker owning $CollectionName (read live from the
# deployment) and skips publishing if it isn't. Only the collection NAME is set here.
$CollectionName  = "RemoteApps"
$AppDisplayName  = "UserDashboard"
$AppAlias        = "UserDashboard"
$AppFilePath     = "C:\GSS\UserDashboard\bin\Release\net48\UserDashboard.exe"
$AppIconPath     = "C:\GSS\UserDashboard\UserDashboard.ico"
$RemoteAppGroups = @("UserDashboard")

# Folder permissions (Step 5)
$AppFolder         = "C:\GSS\UserDashboard"
$FolderAccessLevel = "ReadAndExecute"     # change to "Modify" if the app writes here
$LogFolder         = "C:\GSS\UserDashboard\Log"
$LogAccessLevel    = "Modify"

# App-folder distribution (Step 4)
# Source = the good copy on the machine running this script (the broker). The
# stage pushes this folder to C:\GSS\UserDashboard on every session host.
$DeploySourcePath = if ($DeploySource) { $DeploySource } else { $AppFolder }
# Top-level subfolders NOT pushed (per-host runtime/output dirs, not app content).
$DeployExcludeDirs = @("Log", "RDS-Backup")

# Public Desktop shortcut (Step 4b)
# Windows Public Desktop (visible to every interactive user) is:
#   C:\Users\Public\Desktop
# which is \\<host>\C$\Users\Public\Desktop over the admin share.
# After the app folder is on each host, Step 4b drops a .lnk that points at
# $AppFilePath so the app still runs from C:\GSS\UserDashboard\... (config,
# deps, and working directory stay correct).
$PublicDesktopDir      = "C:\Users\Public\Desktop"
$PublicDesktopShortcut = "UserDashboard.lnk"

# --- Session-host discovery (shared by Steps 4, 5, 6 and -MeasureMask) ---
$AutoDiscoverSessionHosts = $true
$ConnectionBroker         = ""            # "" = this server
# $TerminalServers is now a real parameter (see param() block above) - pass
# -TerminalServers Host1,Host2 on the command line instead of editing this file.
$script:DiscoveredHosts   = $null          # populated by topology auto-detection

# --- Listener mask safety (Step 6) ---
# The session-management WinStation access mask is 0x25 = WINSTATION_QUERY (0x1)
# | WINSTATION_RESET (0x4) | WINSTATION_LOGOFF (0x20). Reset (0x4) is required to
# end ANOTHER user's session; Query+Logoff (0x21) only lets you SEE sessions and
# log off your own. These bits are constant across Windows Server versions
# (confirmed live: INTERACTIVE = 0x1; Remote Desktop Users = 0x121 = Query|Logoff|
# Disconnect but CANNOT log off other users; Administrators additionally hold Reset).
# This table lists builds where 0x25 is known-good, keyed by OS build number.
# A target host on a build NOT listed is auto-measured before any write.
#   Server 2016 = 14393   Server 2019 = 17763
#   Server 2022 = 20348   Server 2025 = 26100
$VerifiedMaskByBuild = @{
    14393 = 0x25    # Windows Server 2016 - Query (0x1) | Reset (0x4) | Logoff (0x20)
    17763 = 0x25    # Windows Server 2019
    20348 = 0x25    # Windows Server 2022
    26100 = 0x25    # Windows Server 2025
}

# ==============================================================================
# ADSI HELPERS
# ==============================================================================
function Find-AdsiObject {
    param([string]$SamAccountName, [string]$ObjectClass)
    $searcher = New-Object System.DirectoryServices.DirectorySearcher
    $searcher.SearchRoot = [ADSI]"LDAP://$DomainDN"
    $searcher.Filter     = "(&(objectClass=$ObjectClass)(sAMAccountName=$SamAccountName))"
    return $searcher.FindOne()
}

function ConvertTo-LdapEscaped {
    param([string]$Value)
    $Value = $Value -replace '\\','\5c'
    $Value = $Value -replace '\*','\2a'
    $Value = $Value -replace '\(','\28'
    $Value = $Value -replace '\)','\29'
    return $Value
}

function Get-AllAdsiUsers {
    $searcher = New-Object System.DirectoryServices.DirectorySearcher
    $searcher.SearchRoot = [ADSI]"LDAP://$DomainDN"
    $searcher.Filter     = "(&(objectCategory=person)(objectClass=user))"
    $searcher.PageSize   = 1000
    foreach ($p in "displayName","sAMAccountName","cn") { $null = $searcher.PropertiesToLoad.Add($p) }
    return $searcher.FindAll()
}

function Find-AdsiUserByName {
    param([string]$Term)
    $safe = ConvertTo-LdapEscaped -Value $Term
    $searcher = New-Object System.DirectoryServices.DirectorySearcher
    $searcher.SearchRoot = [ADSI]"LDAP://$DomainDN"
    $searcher.Filter = "(&(objectCategory=person)(objectClass=user)(|(displayName=*$safe*)(cn=*$safe*)(name=*$safe*)(sAMAccountName=$safe)))"
    $searcher.PageSize = 500
    foreach ($p in "displayName","sAMAccountName","cn") { $null = $searcher.PropertiesToLoad.Add($p) }
    return @($searcher.FindAll())
}

function Get-ResultDisplayName {
    param($Result)
    if     ($Result.Properties["displayname"]) { return $Result.Properties["displayname"][0] }
    elseif ($Result.Properties["cn"])          { return $Result.Properties["cn"][0] }
    else                                        { return $Result.Properties["samaccountname"][0] }
}

function Resolve-GroupSidString {
    # Domain-wide group SID, identical on every member server.
    $grp = Find-AdsiObject -SamAccountName $GroupSamName -ObjectClass "group"
    if (-not $grp) { throw "Group '$GroupName' could not be located for SID lookup." }
    $sidBytes = $grp.Properties["objectsid"][0]
    return (New-Object System.Security.Principal.SecurityIdentifier($sidBytes, 0)).Value
}

# ==============================================================================
# SESSION-HOST DISCOVERY  (shared)
# ==============================================================================
function Get-TargetSessionHosts {
    # -LocalHostOnly: skip all discovery/remoting and operate on THIS host only.
    if ($LocalHostOnly) {
        Write-Host "  -LocalHostOnly: targeting this host only ($env:COMPUTERNAME)." -ForegroundColor Gray
        return @($env:COMPUTERNAME)
    }

    # Reuse the set already found by topology auto-detection, if present.
    if ($script:DiscoveredHosts -and $script:DiscoveredHosts.Count -gt 0) {
        return $script:DiscoveredHosts
    }

    $targetServers = @()

    if ($AutoDiscoverSessionHosts -and (Get-Module -ListAvailable -Name RemoteDesktop)) {
        try {
            Import-Module RemoteDesktop -ErrorAction Stop
            $rdParams = @{ CollectionName = $CollectionName; ErrorAction = "Stop" }
            if ($ConnectionBroker) { $rdParams["ConnectionBroker"] = $ConnectionBroker }
            $targetServers = @(Get-RDSessionHost @rdParams | Select-Object -ExpandProperty SessionHost)
            if ($targetServers.Count -gt 0) {
                Write-Host "  Discovered $($targetServers.Count) session host(s) from collection '$CollectionName':" -ForegroundColor Gray
                $targetServers | ForEach-Object { Write-Host "    - $_" -ForegroundColor DarkGray }
            }
        } catch {
            Write-Warning "  Could not auto-discover session hosts: $_"
        }
    }

    if (-not $targetServers -or $targetServers.Count -eq 0) {
        if ($TerminalServers.Count -gt 0) {
            Write-Host "  Using manual `$TerminalServers list." -ForegroundColor Gray
            $targetServers = $TerminalServers
        } else {
            throw "No session hosts discovered and no manual `$TerminalServers provided. Nothing to do."
        }
    }
    return $targetServers
}

function Test-IsLocalHost {
    param([string]$Server)
    return ($Server -eq $env:COMPUTERNAME) -or
           ($Server -eq "localhost")        -or
           ($Server -eq ".")                -or
           ($Server -eq "$env:COMPUTERNAME.$DomainFQDN")
}

function Invoke-OnHost {
    # Run a scriptblock locally or via Invoke-Command, returning its result.
    param([string]$Server, [ScriptBlock]$ScriptBlock, [object[]]$ArgumentList)
    if (Test-IsLocalHost -Server $Server) {
        return (& $ScriptBlock @ArgumentList)
    } else {
        return (Invoke-Command -ComputerName $Server -ScriptBlock $ScriptBlock `
                    -ArgumentList $ArgumentList -ErrorAction Stop)
    }
}

function Send-FileToHost {
    # Copy a single local file to $RemotePath on $Server, via WinRM or SMB.
    param([string]$Server, [string]$LocalPath, [string]$RemotePath, [string]$Via = "WinRM")
    if ($Via -eq "SMB") {
        $dest    = "\\$Server\C$\" + ($RemotePath -replace '^[A-Za-z]:\\','')
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item -LiteralPath $LocalPath -Destination $dest -Force -ErrorAction Stop
    } else {
        $sess = New-PSSession -ComputerName $Server -ErrorAction Stop
        try {
            $rdir = Split-Path -Parent $RemotePath
            Invoke-Command -Session $sess -ScriptBlock {
                param($d) if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
            } -ArgumentList $rdir
            Copy-Item -LiteralPath $LocalPath -Destination $RemotePath -ToSession $sess -Force -ErrorAction Stop
        } finally {
            Remove-PSSession $sess
        }
    }
}

function New-UserDashboardShortcut {
    # Build a .lnk that launches the installed EXE from its canonical path.
    # Target/WorkingDirectory use the same C:\GSS\... paths on every host, so
    # one locally built shortcut can be copied to the whole session-host pool.
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$TargetPath,
        [string]$WorkingDirectory = (Split-Path -Parent $TargetPath),
        [string]$IconLocation = $TargetPath,
        [string]$Description = "User Management Dashboard"
    )
    $dir = Split-Path -Parent $ShortcutPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $wsh = New-Object -ComObject WScript.Shell
    $lnk = $wsh.CreateShortcut($ShortcutPath)
    $lnk.TargetPath       = $TargetPath
    $lnk.WorkingDirectory = $WorkingDirectory
    $lnk.IconLocation     = $IconLocation
    $lnk.Description      = $Description
    $lnk.Save()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($lnk) | Out-Null
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($wsh) | Out-Null
}

function Test-LocalIsCollectionBroker {
    # Verifies, with nothing hardcoded, that THIS host is the RD Connection Broker
    # that owns $Collection. The broker is read live from the deployment (no host
    # name baked in). Returns an object the caller inspects before publishing.
    param([string]$Collection)
    $info = [ordered]@{
        IsBroker = $false; CollectionExists = $false
        BrokerServers = @(); Available = @(); Detail = $null
    }
    try {
        Import-Module RemoteDesktop -ErrorAction Stop
        $localShort = $env:COMPUTERNAME
        $localFqdn  = "$env:COMPUTERNAME.$DomainFQDN"

        # Get-RDServer with no -ConnectionBroker queries THIS host as the broker;
        # if this host is not a broker it throws (caught below -> IsBroker stays false).
        $servers = Get-RDServer -ErrorAction Stop
        $info.BrokerServers = @($servers | Where-Object { $_.Roles -contains 'RDS-CONNECTION-BROKER' } |
                                 Select-Object -ExpandProperty Server)
        foreach ($b in $info.BrokerServers) {
            if ($b -ieq $localFqdn -or ($b -split '\.')[0] -ieq $localShort) { $info.IsBroker = $true; break }
        }

        # Does the named collection exist on this broker? Capture the list either way.
        $info.Available = @(Get-RDSessionCollection -ErrorAction Stop | Select-Object -ExpandProperty CollectionName)
        $info.CollectionExists = ($info.Available -contains $Collection)
    } catch {
        $info.Detail = "$_"
    }
    return [pscustomobject]$info
}

function Get-DeploymentTopology {
    # Read-only probe used to decide targeting automatically (no -LocalHostOnly
    # needed). AllInOne is true when the collection's ONLY session host is THIS
    # machine - i.e. broker + session host collapsed on one box. On a multi-host
    # deployment it returns the full session-host set so the caller can reuse it.
    $t = [ordered]@{ Detected = $false; AllInOne = $false; SessionHosts = @(); Detail = $null }
    if (-not (Get-Module -ListAvailable -Name RemoteDesktop)) {
        $t.Detail = "RemoteDesktop module not present"
        return [pscustomobject]$t
    }
    try {
        Import-Module RemoteDesktop -ErrorAction Stop
        $rdParams = @{ CollectionName = $CollectionName; ErrorAction = "Stop" }
        if ($ConnectionBroker) { $rdParams["ConnectionBroker"] = $ConnectionBroker }
        $hostSet = @(Get-RDSessionHost @rdParams | Select-Object -ExpandProperty SessionHost)
        $t.SessionHosts = $hostSet
        $t.Detected     = $true
        if ($hostSet.Count -eq 1 -and (Test-IsLocalHost -Server $hostSet[0])) { $t.AllInOne = $true }
    } catch {
        $t.Detail = "$_"
    }
    return [pscustomobject]$t
}

function Show-Mask {
    param([string]$Label, [uint32]$Mask)
    $bits = @()
    for ($i = 0; $i -lt 24; $i++) {
        $b = [uint32]([math]::Pow(2, $i))
        if ($Mask -band $b) { $bits += ("0x{0:X}" -f $b) }
    }
    Write-Host ("    {0,-26} = {1,-8} (0x{2:X})  bits: {3}" -f $Label, $Mask, $Mask, ($bits -join " "))
}

# ==============================================================================
# DOMAIN-CONTROLLER GUARD
# This script must NEVER modify the RDP-Tcp listener (or folders) on a domain
# controller (e.g. the Database/DC). Two layers protect against it:
#   1. Orchestrator filter: every target host is checked against the live list
#      of domain controllers and excluded before any work is dispatched.
#   2. Host-side guard: each per-host scriptblock re-checks its own DomainRole
#      and refuses to act if it is running on a DC - this also covers
#      -LocalHostOnly runs and any case where DC enumeration was unavailable.
# ==============================================================================
function Get-DomainControllerSet {
    # Returns a hashtable of lowercased DC names (both FQDN and short name).
    $set = @{}
    try {
        $dcs = ([System.DirectoryServices.ActiveDirectory.Domain]::GetCurrentDomain()).DomainControllers
        foreach ($dc in $dcs) {
            $fqdn  = "$($dc.Name)".ToLower()          # e.g. gssdb.gss-cloud.com
            $short = ($fqdn -split '\.')[0]
            $set[$fqdn]  = $true
            $set[$short] = $true
        }
        if ($set.Count -gt 0) {
            Write-Host "  Domain controllers (will be excluded from per-host stages): $((($set.Keys | Where-Object { $_ -like '*.*' }) -join ', '))" -ForegroundColor DarkGray
        }
    } catch {
        Write-Warning "  Could not enumerate domain controllers via .NET: $_"
        Write-Warning "  Relying on per-host DomainRole checks only."
    }
    return $set
}

function Test-IsDomainController {
    # $true if the named server is a known DC. Matches FQDN or short name.
    param([string]$Server, [hashtable]$DcSet)
    if (-not $DcSet -or $DcSet.Count -eq 0) { return $false }
    $name  = "$Server".ToLower()
    $short = ($name -split '\.')[0]
    return ($DcSet.ContainsKey($name) -or $DcSet.ContainsKey($short))
}

# Built once, up front; reused by every per-host stage.
$DcSet = Get-DomainControllerSet

# ==============================================================================
# REMOTE SCRIPTBLOCKS  (run on each session host; return objects, not Write-Host)
# ==============================================================================

# --- Lightweight OS identity (used by the Step 6 mask-safety gate) ---
$osInfoScript = {
    $os = $null
    try   { $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop }
    catch { try { $os = Get-WmiObject -Class Win32_OperatingSystem -ErrorAction Stop } catch {} }
    if ($null -eq $os) {
        return [pscustomobject]@{ Host = $env:COMPUTERNAME; Caption = "(unknown)"; Build = 0; Version = "(unknown)" }
    }
    $build = 0; [void][int]::TryParse("$($os.BuildNumber)", [ref]$build)
    [pscustomobject]@{ Host = $env:COMPUTERNAME; Caption = "$($os.Caption)".Trim(); Build = $build; Version = "$($os.Version)" }
}

# --- Read the live listener ACEs to confirm the Query/Reset/Logoff mask ---
# READ-ONLY diagnostic. It inspects the reference principals that ALREADY exist on
# the listener (INTERACTIVE, Remote Desktop Users, Administrators) and reports the
# standard WinStation bits. It does NOT add, modify, or remove any ACE - so there
# is nothing to back up and nothing to clean up. The bits themselves (Query 0x1,
# Reset 0x4, Logoff 0x20) are architectural constants; this confirms the host uses
# that standard layout and surfaces the masks that make the requirement obvious:
# INTERACTIVE has Query; Remote Desktop Users has Query+Logoff but NOT Reset (which
# is why RDU cannot end other users' sessions); Administrators additionally hold Reset.
$measureScript = {
    param($GroupAccount, $TerminalName)
    $ErrorActionPreference = "Stop"
    $ns = "root\CIMV2\TerminalServices"

    # WinStation access-right constants (stable across Windows versions).
    $QUERY = [uint32]0x1; $RESET = [uint32]0x4; $LOGOFF = [uint32]0x20

    $result = [ordered]@{
        Host = $env:COMPUTERNAME; Terminal = $TerminalName
        InteractiveMask = $null; RduMask = $null; AdminsMask = $null; SystemMask = $null
        QueryBit = $QUERY; ResetBit = $RESET; LogoffBit = $LOGOFF
        StandardLayout = $false; TargetMask = [uint32]0
        References = @(); Error = $null; CleanedUp = $true
    }

    try {
        $accts = @(Get-WmiObject -Namespace $ns -Class Win32_TSAccount `
                     -Filter "TerminalName='$TerminalName'" -Authentication PacketPrivacy)
        if ($accts.Count -eq 0) { throw "No Win32_TSAccount entries found for listener '$TerminalName'." }

        foreach ($a in $accts) {
            $name = "$($a.AccountName)"
            $mask = [uint32]$a.PermissionsAllowed
            $result.References += [pscustomobject]@{ Account = $name; Mask = $mask }
            switch -Regex ($name) {
                'INTERACTIVE$'          { $result.InteractiveMask = $mask }
                'Remote Desktop Users$' { $result.RduMask         = $mask }
                'Administrators$'       { $result.AdminsMask      = $mask }
                'SYSTEM$'               { $result.SystemMask      = $mask }
            }
        }

        # Confirm the host uses the standard bit layout before trusting 0x25:
        #  - INTERACTIVE carries Query (0x1)
        #  - Administrators carry both Reset (0x4) and Logoff (0x20)
        $interactiveHasQuery   = ($null -ne $result.InteractiveMask) -and
                                 (($result.InteractiveMask -band $QUERY) -eq $QUERY)
        $adminsHaveResetLogoff = ($null -ne $result.AdminsMask) -and
                                 ((($result.AdminsMask -band $RESET)  -eq $RESET) -and
                                  (($result.AdminsMask -band $LOGOFF) -eq $LOGOFF))
        $result.StandardLayout = ($interactiveHasQuery -and $adminsHaveResetLogoff)

        # The mask we grant: Query (see) + Reset (end another user's) + Logoff.
        $result.TargetMask = ($QUERY -bor $RESET -bor $LOGOFF)

        if (-not $result.StandardLayout) {
            $result.Error = "Listener does not show the expected reference bits " +
                            "(INTERACTIVE=Query, Administrators=Reset+Logoff); 0x25 not confirmed here."
        }
    } catch {
        $result.Error = "$_"
    }
    return [pscustomobject]$result
}

# --- Grant exactly $Mask to the group on the listener (preview unless -DoApply) ---
$listenerScript = {
    param($SidString, $GroupAccount, $TerminalName, $Mask, $DoApply)
    $ErrorActionPreference = "Stop"
    $ns      = "root\CIMV2\TerminalServices"
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\$TerminalName"

    # HOST-SIDE DC GUARD: never touch a domain controller's listener.
    # DomainRole 4 = backup DC, 5 = primary DC.
    $role = $null
    try   { $role = (Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop).DomainRole }
    catch { try { $role = (Get-WmiObject -Class Win32_ComputerSystem -ErrorAction Stop).DomainRole } catch {} }
    if ($null -ne $role -and [int]$role -ge 4) {
        return [pscustomobject]@{
            Host = $env:COMPUTERNAME; Terminal = $TerminalName; HadExplicit = $false
            SddlBefore = $null; SddlAfter = $null; Applied = $false; PreBytes = $null; Verify = $null
            Error = "SKIPPED: $env:COMPUTERNAME is a domain controller (DomainRole=$role). Listener NOT modified."
        }
    }

    $sid = New-Object System.Security.Principal.SecurityIdentifier($SidString)

    $secOrig     = (Get-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue).Security
    $hadExplicit = $null -ne $secOrig

    $result = [ordered]@{
        Host = $env:COMPUTERNAME; Terminal = $TerminalName; HadExplicit = $hadExplicit
        SddlBefore = $null; SddlAfter = $null; Applied = $false; AlreadyCorrect = $false
        PreBytes = $secOrig; Verify = $null; Error = $null
    }

    # IDEMPOTENCY: if the listener already grants this group EXACTLY $Mask via a
    # single clean allow ACE (no inheritance), there is nothing to do. This lets an
    # onboarding re-run touch (and back up) only the hosts that still need the grant.
    if ($hadExplicit) {
        try {
            $rsdChk    = New-Object System.Security.AccessControl.RawSecurityDescriptor(([byte[]]$secOrig), 0)
            $groupAces = @($rsdChk.DiscretionaryAcl | Where-Object { $_.SecurityIdentifier -eq $sid })
            if ($groupAces.Count -eq 1 -and
                $groupAces[0].AceQualifier -eq [System.Security.AccessControl.AceQualifier]::AccessAllowed -and
                ([uint32]$groupAces[0].AccessMask) -eq [uint32]$Mask -and
                $groupAces[0].AceFlags -eq [System.Security.AccessControl.AceFlags]::None) {
                $result.AlreadyCorrect = $true
                $sddl = $rsdChk.GetSddlForm([System.Security.AccessControl.AccessControlSections]::All)
                $result.SddlBefore = $sddl
                $result.SddlAfter  = $sddl
                return [pscustomobject]$result
            }
        } catch { }   # fall through to the normal path on any parse hiccup
    }

    try {
        $sec             = $secOrig
        $previewRollback = $false

        # No explicit descriptor: let WMI materialize the correct defaults first.
        if ($null -eq $sec) {
            $perm = Get-WmiObject -Namespace $ns -Class Win32_TSPermissionsSetting `
                        -Filter "TerminalName='$TerminalName'" -Authentication PacketPrivacy
            [void]$perm.AddAccount($GroupAccount, 0)
            $sec = (Get-ItemProperty -Path $regPath -Name "Security").Security
            if (-not $DoApply) { $previewRollback = $true }
        }

        $rsd = New-Object System.Security.AccessControl.RawSecurityDescriptor(([byte[]]$sec), 0)
        $result.SddlBefore = $rsd.GetSddlForm([System.Security.AccessControl.AccessControlSections]::All)

        # Strip any existing ACE for our SID (reverse index so removals don't shift).
        for ($i = $rsd.DiscretionaryAcl.Count - 1; $i -ge 0; $i--) {
            if ($rsd.DiscretionaryAcl[$i].SecurityIdentifier -eq $sid) {
                $rsd.DiscretionaryAcl.RemoveAce($i)
            }
        }
        # Insert one clean allow ACE carrying exactly $Mask, no inheritance.
        $rsd.DiscretionaryAcl.InsertAce(
            $rsd.DiscretionaryAcl.Count,
            (New-Object System.Security.AccessControl.CommonAce(
                [System.Security.AccessControl.AceFlags]::None,
                [System.Security.AccessControl.AceQualifier]::AccessAllowed,
                [int]$Mask, $sid, $false, $null)))
        $result.SddlAfter = $rsd.GetSddlForm([System.Security.AccessControl.AccessControlSections]::All)

        if ($DoApply) {
            $bytes = New-Object byte[] ($rsd.BinaryLength)
            $rsd.GetBinaryForm($bytes, 0)
            Set-ItemProperty -Path $regPath -Name "Security" -Value $bytes -Type Binary
            $result.Applied = $true

            $v = Get-WmiObject -Namespace $ns -Class Win32_TSAccount `
                    -Filter "TerminalName='$TerminalName'" -Authentication PacketPrivacy |
                    Where-Object { $_.AccountName -eq $GroupAccount } | Select-Object -First 1
            if ($v) { $result.Verify = "Allowed=$($v.PermissionsAllowed) Denied=$($v.PermissionsDenied)" }
        }
        elseif ($previewRollback) {
            # Undo the temporary materialize done purely for the preview.
            $a = Get-WmiObject -Namespace $ns -Class Win32_TSAccount `
                    -Filter "TerminalName='$TerminalName'" -Authentication PacketPrivacy |
                    Where-Object { $_.AccountName -eq $GroupAccount } | Select-Object -First 1
            if ($a) { [void]$a.Delete() }
            Remove-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue
        }
    } catch {
        $result.Error = "$_"
    }
    return [pscustomobject]$result
}

# --- Restore a listener descriptor from saved bytes (or revert to defaults) ----
$restoreScript = {
    param($TerminalName, $IsMarker, $Bytes)
    $ErrorActionPreference = "Stop"
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\$TerminalName"
    if (-not (Test-Path $regPath)) { throw "Listener registry key not found: $regPath" }

    if ($IsMarker) {
        Remove-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue
        $msg = "Reverted to inherited defaults (removed explicit Security value)."
    } else {
        Set-ItemProperty -Path $regPath -Name "Security" -Value $Bytes -Type Binary
        $msg = "Restored explicit descriptor."
    }

    $sddl = $null
    $sec  = (Get-ItemProperty -Path $regPath -Name "Security" -ErrorAction SilentlyContinue).Security
    if ($null -eq $sec) {
        $sddl = "(no explicit security value - inherited defaults)"
    } else {
        try {
            $rsd  = New-Object System.Security.AccessControl.RawSecurityDescriptor(([byte[]]$sec), 0)
            $sddl = $rsd.GetSddlForm([System.Security.AccessControl.AccessControlSections]::All)
        } catch { $sddl = "(could not convert to SDDL: $($_.Exception.Message))" }
    }
    return [pscustomobject]@{ Host = $env:COMPUTERNAME; Message = $msg; Sddl = $sddl }
}

# ==============================================================================
# Session-host prerequisite: hosts must already be domain-joined and in the
# collection. Discovery / deploy / ACLs / listener grants cannot see a host
# that has not been added yet.
# ==============================================================================
function Write-SessionHostPrerequisiteWarning {
    param([switch]$Prompt)

    Write-Host ""
    Write-Host "[PREREQUISITE] Session hosts must already be domain-joined and added" -ForegroundColor Yellow
    Write-Host "  to the RDS collection ('$CollectionName') BEFORE this script is run." -ForegroundColor Yellow
    Write-Host "  Join each host to the domain, add it to the collection, then run setup." -ForegroundColor Yellow
    Write-Host "  Hosts added after this run will not receive the app, folder ACLs," -ForegroundColor Yellow
    Write-Host "  event-log source, or listener rights until you re-run (or run this" -ForegroundColor Yellow
    Write-Host "  script on that host with -LocalHostOnly)." -ForegroundColor Yellow

    if ($script:DiscoveredHosts -and $script:DiscoveredHosts.Count -gt 0) {
        Write-Host "  This run will target:" -ForegroundColor Yellow
        $script:DiscoveredHosts | ForEach-Object { Write-Host "    - $_" -ForegroundColor DarkGray }
    } elseif ($LocalHostOnly) {
        Write-Host "  This run is scoped to this host only ($env:COMPUTERNAME)." -ForegroundColor Yellow
    } else {
        Write-Host "  No session hosts have been discovered yet. Add them to the" -ForegroundColor Yellow
        Write-Host "  collection first, or pass -TerminalServers Host1,Host2." -ForegroundColor Yellow
    }

    if ($Prompt -and -not $Unattended) {
        Write-Host ""
        $ready = Read-Host "  Have all intended session hosts been domain-joined and added to the collection? (Y/N)"
        if ($ready -notmatch '^[Yy]$') {
            Write-Host "  Aborted. Join/add the session hosts, then re-run setup." -ForegroundColor Yellow
            exit 0
        }
    }
}

# ==============================================================================
# PREFLIGHT  (normal run): actively verify the environment BEFORE any changes.
# Each enforced check is stage-aware - only what this run will actually use is
# required. A blocking ("FATAL") failure stops the run (exit 1) unless the
# operator passes -SkipPreflight. Caches the broker check + host list for reuse.
# ==============================================================================
function Invoke-Preflight {
    Write-Host "`n[PREFLIGHT] Verifying environment before making changes..." -ForegroundColor Cyan

    $fatal = New-Object System.Collections.Generic.List[string]
    $warn  = New-Object System.Collections.Generic.List[string]
    $pass  = New-Object System.Collections.Generic.List[string]

    # Which stages will actually run?
    $willPublish    = -not $SkipRemoteApp
    $willDeploy     = (-not $SkipDeploy)    -and (-not $LocalHostOnly)
    $willAcl        = -not $SkipFolderAcl
    $willEventLog   = -not $SkipEventLogSource
    $willListener   = -not $SkipListener
    $needsHosts     = $willDeploy -or $willAcl -or $willEventLog -or $willListener
    $needsDiscovery = $needsHosts -and (-not $LocalHostOnly)
    $needsRD        = $willPublish -or $needsDiscovery

    # 1) Administrator
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) { $pass.Add("Running as Administrator") } else { $fatal.Add("Not running as Administrator.") }

    # 2) Domain bind (already detected up top; confirm the values are present)
    if ($DomainFQDN -and $DomainDN) { $pass.Add("Domain reachable: $DomainFQDN") }
    else { $fatal.Add("Active Directory domain not detected.") }

    # 3) Target container resolvable
    if ($OUPath) { $pass.Add("Target container resolved: $OUPath") }
    else { $fatal.Add("Could not resolve the Users OU/container.") }

    # 4) RemoteDesktop module (publish + discovery)
    if ($needsRD) {
        if (Get-Module -ListAvailable -Name RemoteDesktop) { $pass.Add("RemoteDesktop module available") }
        else { $fatal.Add("RemoteDesktop module not found (needed for publish/discovery). Run on the broker, or use -LocalHostOnly with -SkipRemoteApp.") }
    }

    # 5) Broker + collection (only if publishing) - the check you asked to enforce
    if ($willPublish) {
        try {
            $script:PreflightBroker = Test-LocalIsCollectionBroker -Collection $CollectionName
            if ($script:PreflightBroker.IsBroker -and $script:PreflightBroker.CollectionExists) {
                $pass.Add("This host is the RD Connection Broker for collection '$CollectionName'")
            } else {
                $m = "Not verified as broker for '$CollectionName' (IsBroker=$($script:PreflightBroker.IsBroker), CollectionExists=$($script:PreflightBroker.CollectionExists))."
                if ($script:PreflightBroker.BrokerServers) { $m += " Deployment broker(s): $($script:PreflightBroker.BrokerServers -join ', ')." }
                if ($script:PreflightBroker.IsBroker -and $script:PreflightBroker.Available) { $m += " Collections here: $($script:PreflightBroker.Available -join ', ')." }
                $fatal.Add($m)
            }
        } catch {
            $fatal.Add("Broker/collection verification call failed: $_")
        }
    }

    # 6) Session-host discovery
    $hosts = @()
    if ($needsDiscovery) {
        try { $hosts = @(Get-TargetSessionHosts) } catch { $fatal.Add("Session-host discovery failed: $_") }
        if ($hosts.Count -gt 0) { $pass.Add("Discovered $($hosts.Count) session host(s)") }
        else { $fatal.Add("No session hosts discovered (and no manual `$TerminalServers).") }
    } elseif ($LocalHostOnly -and $needsHosts) {
        $hosts = @($env:COMPUTERNAME)
        $pass.Add("LocalHostOnly: targeting $env:COMPUTERNAME")
    }
    $script:PreflightHosts = $hosts

    # 7) WinRM reachability to the hosts the run will touch (skip self + DCs)
    if ($needsHosts -and $hosts.Count -gt 0) {
        $reachable = 0
        $unreach   = New-Object System.Collections.Generic.List[string]
        foreach ($h in $hosts) {
            if (Test-IsLocalHost -Server $h)                       { $reachable++; continue }
            if (Test-IsDomainController -Server $h -DcSet $DcSet)   { continue }   # excluded anyway
            $up = $false
            try { $null = Test-WSMan -ComputerName $h -ErrorAction Stop; $up = $true } catch {
                try { $null = Invoke-Command -ComputerName $h -ScriptBlock { 1 } -ErrorAction Stop; $up = $true } catch {}
            }
            if ($up) { $reachable++ } else { $unreach.Add($h) }
        }
        if ($unreach.Count -gt 0) { $warn.Add("WinRM not reachable on: $($unreach -join ', ') - those hosts will be skipped.") }
        if ($reachable -eq 0) { $fatal.Add("No reachable session hosts over WinRM - per-host stages cannot run.") }
        else { $pass.Add("$reachable session host(s) reachable over WinRM") }
    }

    # 8) Deploy source present (warning - Step 4 degrades gracefully on its own)
    if ($willDeploy) {
        $srcExe = $null
        if (Test-Path -LiteralPath $DeploySourcePath) {
            $srcExe = Get-ChildItem -Path $DeploySourcePath -Filter "UserDashboard.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        }
        if ($srcExe) { $pass.Add("Deploy source contains UserDashboard.exe") }
        else { $warn.Add("Deploy source '$DeploySourcePath' has no UserDashboard.exe; Step 4 will be skipped.") }
    }

    # 9) Backup folder writable (Step 6 may write if the operator answers Y at
    #    the prompt, even without -ApplyListener - so check this whenever the
    #    listener stage will run at all, not only when -ApplyListener is passed.
    if ($willListener) {
        try {
            if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force -ErrorAction Stop | Out-Null }
            $probe = Join-Path $BackupDir ".preflight_write_test"
            Set-Content -Path $probe -Value "ok" -ErrorAction Stop
            Remove-Item -Path $probe -Force -ErrorAction SilentlyContinue
            $pass.Add("Backup folder writable: $BackupDir")
        } catch {
            $fatal.Add("Backup folder not writable ($BackupDir): $_  Listener apply must write rollback backups.")
        }
    }

    # ---- Report ----
    foreach ($p in $pass)  { Write-Host "  [ OK ]  $p"   -ForegroundColor Green }
    foreach ($w in $warn)  { Write-Host "  [WARN]  $w"   -ForegroundColor Yellow }
    foreach ($x in $fatal) { Write-Host "  [FAIL]  $x"   -ForegroundColor Red }

    return [pscustomobject]@{ Fatal = @($fatal); Warn = @($warn); Pass = @($pass) }
}

# ==============================================================================
# MODE: -RestoreListener  (recovery, then exit)
# ==============================================================================
if ($RestoreListener) {
    Write-Host "`n[MODE] Restore listener security descriptor" -ForegroundColor Cyan

    if (-not $BackupFile) { Write-Error "  -RestoreListener requires -BackupFile <path>."; exit 1 }
    if (-not (Test-Path -LiteralPath $BackupFile)) { Write-Error "  Backup file not found: $BackupFile"; exit 1 }

    # A marker backup means "originally no explicit descriptor".
    $isMarker = $false
    $first    = $null
    try { $first = Get-Content -Path $BackupFile -TotalCount 1 -ErrorAction SilentlyContinue } catch {}
    if ($first -eq "NO_EXPLICIT_SECURITY_VALUE") { $isMarker = $true }

    $bytes = $null
    if (-not $isMarker) { $bytes = [System.IO.File]::ReadAllBytes($BackupFile) }

    if (-not $RestoreHosts -or $RestoreHosts.Count -eq 0) { $RestoreHosts = @($env:COMPUTERNAME) }

    foreach ($h in $RestoreHosts) {
        if (Test-IsDomainController -Server $h -DcSet $DcSet) {
            Write-Warning "  [$h] is a domain controller. Restore is a recovery action, so proceeding - but confirm this is intended."
        }
        try {
            $r = Invoke-OnHost -Server $h -ScriptBlock $restoreScript `
                    -ArgumentList @($TerminalName, $isMarker, $bytes)
            Write-Host "  [$($r.Host)] $($r.Message)" -ForegroundColor Green
            Write-Host "    SDDL now: $($r.Sddl)" -ForegroundColor DarkGray
        } catch {
            Write-Warning "  [$h] Restore failed: $_"
        }
    }
    Write-Host "`n[DONE] Restore complete.`n" -ForegroundColor Green
    exit 0
}

# ==============================================================================
# TOPOLOGY AUTO-DETECTION
#   Removes the need to remember -LocalHostOnly. If the collection's only session
#   host is THIS machine (all-in-one: broker + session host on one box), the
#   per-host stages are scoped to this host automatically. Multi-host deployments
#   fan out to every discovered host as usual. An explicit -LocalHostOnly always
#   wins and skips detection.
# ==============================================================================
if ($PSBoundParameters.ContainsKey('LocalHostOnly') -and $AllHosts) {
    Write-Error "  -LocalHostOnly and -AllHosts are mutually exclusive. Choose one."
    exit 1
}
if ($PSBoundParameters.ContainsKey('LocalHostOnly')) {
    Write-Host "`n[TOPOLOGY] -LocalHostOnly set explicitly - targeting this host only." -ForegroundColor Gray
}
elseif ($AllHosts) {
    # Explicit multi-host: never auto-collapse to local. Resolve the full set now
    # from -TerminalServers (if given) or live discovery, and reuse it everywhere.
    Write-Host "`n[TOPOLOGY] -AllHosts set - forcing multi-host mode (no auto -LocalHostOnly)." -ForegroundColor Cyan
    $LocalHostOnly = $false
    if ($TerminalServers -and $TerminalServers.Count -gt 0) {
        $script:DiscoveredHosts = $TerminalServers
        Write-Host "           Using explicit -TerminalServers list:" -ForegroundColor Cyan
    } else {
        $topo = Get-DeploymentTopology
        if ($topo.Detected -and $topo.SessionHosts.Count -gt 0) {
            $script:DiscoveredHosts = $topo.SessionHosts
            Write-Host "           Discovered $($topo.SessionHosts.Count) session host(s) in '$CollectionName':" -ForegroundColor Cyan
        } else {
            Write-Error "  -AllHosts was set but no hosts could be resolved ($($topo.Detail)). Pass -TerminalServers Host1,Host2."
            exit 1
        }
    }
    $script:DiscoveredHosts | ForEach-Object { Write-Host "             - $_" -ForegroundColor DarkGray }
}
elseif ($AutoDiscoverSessionHosts) {
    $topo = Get-DeploymentTopology
    if ($topo.Detected) {
        $script:DiscoveredHosts = $topo.SessionHosts
        if ($topo.AllInOne) {
            $LocalHostOnly = $true
            Write-Host "`n[TOPOLOGY] All-in-one detected: this machine is the only session host in '$CollectionName'." -ForegroundColor Cyan
            Write-Host "           Scoping per-host stages to this host automatically (auto -LocalHostOnly)." -ForegroundColor Cyan
        } else {
            Write-Host "`n[TOPOLOGY] Multi-host deployment: $($topo.SessionHosts.Count) session host(s) in '$CollectionName':" -ForegroundColor Cyan
            $topo.SessionHosts | ForEach-Object { Write-Host "             - $_" -ForegroundColor DarkGray }
            Write-Host "           Per-host stages will target all of them from here." -ForegroundColor Cyan
        }
    } else {
        # Probe failed. Do NOT silently collapse to local-only - that is exactly
        # how a real multi-host collection ends up configured on only one box.
        Write-Host "`n[TOPOLOGY] Could not probe the deployment ($($topo.Detail))." -ForegroundColor DarkYellow
        if ($TerminalServers -and $TerminalServers.Count -gt 0) {
            $script:DiscoveredHosts = $TerminalServers
            Write-Host "           Falling back to explicit -TerminalServers list:" -ForegroundColor DarkYellow
            $script:DiscoveredHosts | ForEach-Object { Write-Host "             - $_" -ForegroundColor DarkGray }
        } else {
            Write-Warning "           Session-host discovery failed and no -TerminalServers were given."
            Write-Warning "           To configure THIS host only, re-run with -LocalHostOnly."
            Write-Warning "           To configure the whole collection, re-run with -AllHosts (optionally -TerminalServers Host1,Host2)."
            exit 1
        }
    }
}

# ==============================================================================
# MODE: -MeasureMask  (read-only diagnostic, then exit)
# ==============================================================================
if ($MeasureMask) {
    Write-Host "`n[MODE] Inspect listener reference principals (read-only)" -ForegroundColor Cyan

    $hosts = Get-TargetSessionHosts
    foreach ($h in $hosts) {
        Write-Host "`n  --- $h ---" -ForegroundColor Cyan
        if (Test-IsDomainController -Server $h -DcSet $DcSet) {
            Write-Warning "    SKIPPED - domain controller; not inspected."
            continue
        }
        try {
            $m = Invoke-OnHost -Server $h -ScriptBlock $measureScript `
                    -ArgumentList @($GroupAccount, $TerminalName)

            # Show the reference principals that define the bit layout on this host.
            Show-Mask "INTERACTIVE (Query)"          ([uint32]$m.InteractiveMask)
            Show-Mask "Remote Desktop Users"         ([uint32]$m.RduMask)
            Show-Mask "Administrators"               ([uint32]$m.AdminsMask)
            Show-Mask "-> Query bit"                 ([uint32]$m.QueryBit)
            Show-Mask "-> Reset bit"                 ([uint32]$m.ResetBit)
            Show-Mask "-> Logoff bit"                ([uint32]$m.LogoffBit)

            if ($m.Error) {
                Write-Warning "    $($m.Error)"
            }
            if ($m.StandardLayout) {
                Write-Host "    Standard WinStation layout confirmed (INTERACTIVE=Query, Administrators=Reset+Logoff)." -ForegroundColor Green
                Write-Host ("    RECOMMENDED TARGET MASK = {0} (0x{0:X})  = Query|Reset|Logoff" -f $m.TargetMask) -ForegroundColor Green
            } else {
                Write-Warning "    Non-standard layout - do NOT trust 0x25 blindly on this host; investigate before applying."
            }
        } catch {
            Write-Warning "  [$h] Inspection failed: $_"
        }
    }
    Write-Host "`n[DONE] Inspection complete (read-only; nothing was changed). The session-management mask is 0x25 (Query 0x1 | Reset 0x4 | Logoff 0x20).`n" -ForegroundColor Cyan
    exit 0
}

# ==============================================================================
# NORMAL RUN - prerequisite warning, preflight, confirm, then Steps 1-6
# ==============================================================================
Write-SessionHostPrerequisiteWarning -Prompt

if ($SkipPreflight) {
    Write-Warning "`n[PREFLIGHT] Skipped (-SkipPreflight). Environment was not verified."
} else {
    $pf = Invoke-Preflight
    if ($pf.Fatal.Count -gt 0) {
        Write-Host ""
        Write-Error "  PREFLIGHT FAILED: $($pf.Fatal.Count) blocking issue(s) above. Not proceeding."
        Write-Host  "  Resolve the [FAIL] item(s), or re-run with -SkipPreflight to override (not recommended)." -ForegroundColor Yellow
        exit 1
    }
    if ($pf.Warn.Count -gt 0) {
        Write-Host "  Preflight passed with $($pf.Warn.Count) warning(s) - review the [WARN] item(s) above." -ForegroundColor Yellow
    } else {
        Write-Host "  Preflight passed." -ForegroundColor Green
    }
}

if (-not $Unattended) {
    Write-Host ""
    $confirm = Read-Host "  Proceed with UserDashboard setup on '$DomainFQDN'? (Y/N)"
    if ($confirm -notmatch "^[Yy]$") { Write-Host "  Aborted by user." -ForegroundColor Yellow; exit 0 }
}


# ------------------------------------------------------------------------------
# STEP 0 - STAGE the build into the canonical install path
#
#   Pattern: the DEPLOYED app always lives at $AppFolder (C:\GSS\UserDashboard),
#   which is the fixed path the RemoteApp is published against and never changes.
#   Each release is unpacked/built into a VERSIONED staging folder (e.g.
#   C:\GSS\UserDashboard-v2.4.1) so you keep a visible on-disk history of builds.
#
#   This step detects when the script is being run from such a staging folder,
#   verifies the built EXE's version matches $AppVersion, and copies the build
#   into $AppFolder BEFORE publishing/deploying - so the canonical path always
#   holds the version this script targets. The running version stays visible via
#   the app's title bar / About / audit log and the EXE FileVersion stamp.
#
#   Skipped automatically when the build is already AT the canonical path (i.e.
#   you ran the script from C:\GSS\UserDashboard itself), or with -SkipStaging.
# ------------------------------------------------------------------------------
if ($SkipStaging) {
    Write-Host "`n[STEP 0] Staging skipped (-SkipStaging)." -ForegroundColor DarkYellow
} else {
    Write-Host "`n[STEP 0] Staging build into canonical path '$AppFolder'..." -ForegroundColor Cyan
    try {
        # Find the built EXE within this script's own folder tree. $ScriptRoot is
        # typically <staging>\README, so search from its parent to catch
        # <staging>\bin\Release\net48\UserDashboard.exe.
        $stagingRoot = Split-Path -Parent $ScriptRoot
        if (-not $stagingRoot) { $stagingRoot = $ScriptRoot }

        $stagedExe = Get-ChildItem -Path $stagingRoot -Filter "UserDashboard.exe" -Recurse -ErrorAction SilentlyContinue |
                     Where-Object { $_.FullName -match '\\bin\\Release\\' } |
                     Select-Object -First 1

        if (-not $stagedExe) {
            Write-Host "  No built EXE found under '$stagingRoot' (bin\Release). Assuming the app is" -ForegroundColor Gray
            Write-Host "  already at '$AppFolder'; nothing to stage." -ForegroundColor Gray
        }
        else {
            $stagedBinDir = Split-Path -Parent $stagedExe.FullName          # ...\bin\Release\net48
            $canonicalBin = Join-Path $AppFolder "bin\Release\net48"

            # If the found build IS already the canonical path, do nothing.
            if ($stagedBinDir.TrimEnd('\') -ieq $canonicalBin.TrimEnd('\')) {
                Write-Host "  Build is already at the canonical path; no staging needed." -ForegroundColor Gray
            }
            else {
                $stagedVer = $stagedExe.VersionInfo.FileVersion
                if ($stagedVer -like "$AppVersion*") {
                    Write-Host "  Staged build version: $stagedVer (matches target v$AppVersion)" -ForegroundColor Green
                } else {
                    Write-Warning "  Staged build is version $stagedVer, but this script targets v$AppVersion."
                    Write-Warning "  Continuing, but confirm you unpacked the intended release."
                }

                # Kill any running instance so the copy isn't blocked by a locked EXE.
                $running = Get-Process -Name "UserDashboard" -ErrorAction SilentlyContinue
                if ($running) {
                    Write-Host "  Stopping $($running.Count) running UserDashboard instance(s) on this host..." -ForegroundColor Yellow
                    $running | Stop-Process -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Milliseconds 500
                }

                New-Item -ItemType Directory -Path $AppFolder -Force | Out-Null
                Write-Host "  Copying build payload:" -ForegroundColor Gray
                Write-Host "    from: $stagedBinDir" -ForegroundColor DarkGray
                Write-Host "    to:   $canonicalBin" -ForegroundColor DarkGray

                # Copy the bin\Release\net48 payload (EXE + all dependency DLLs).
                New-Item -ItemType Directory -Path $canonicalBin -Force | Out-Null
                Copy-Item -Path (Join-Path $stagedBinDir '*') -Destination $canonicalBin -Recurse -Force

                # Also bring the icon to the canonical root if present in staging.
                $stagedIcon = Get-ChildItem -Path $stagingRoot -Filter "UserDashboard.ico" -Recurse -ErrorAction SilentlyContinue |
                              Select-Object -First 1
                if ($stagedIcon) {
                    Copy-Item -Path $stagedIcon.FullName -Destination (Join-Path $AppFolder "UserDashboard.ico") -Force
                }

                # Verify the canonical EXE now reports the expected version.
                $canonicalExe = Join-Path $canonicalBin "UserDashboard.exe"
                if (Test-Path -LiteralPath $canonicalExe) {
                    $nowVer = (Get-Item $canonicalExe).VersionInfo.FileVersion
                    Write-Host "  Canonical install now at v$nowVer : $canonicalExe" -ForegroundColor Green
                } else {
                    Write-Warning "  Copy completed but '$canonicalExe' was not found afterward - check permissions."
                }
            }
        }
    }
    catch {
        Write-Warning "  Staging step failed: $_"
        Write-Warning "  Continuing; Step 3 will use whatever is already at '$AppFolder'."
    }
}

# ------------------------------------------------------------------------------
# STEP 1 - Create the UserDashboard Security Group (ADSI) and add members
# ------------------------------------------------------------------------------
if ($SkipGroup) {
    Write-Host "`n[STEP 1] Skipped (-SkipGroup)." -ForegroundColor DarkYellow
} else {
    Write-Host "`n[STEP 1] Creating AD group '$GroupName' (via ADSI)..." -ForegroundColor Cyan
    try {
        $existing = Find-AdsiObject -SamAccountName $GroupSamName -ObjectClass "group"
        if ($existing) {
            Write-Host "  Group '$GroupName' already exists - skipping creation." -ForegroundColor Yellow
        } else {
            $ou       = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$OUPath")
            $newGroup = $ou.Create("group", "CN=$GroupName")
            $newGroup.Properties["sAMAccountName"].Value = $GroupSamName
            $newGroup.Properties["groupType"].Value      = $GROUP_GLOBAL_SECURITY
            $newGroup.Properties["description"].Value     = "UserDashboard application access and AD delegation group"
            $newGroup.CommitChanges()
            Write-Host "  Group '$GroupName' created successfully." -ForegroundColor Green
        }

        $groupEntry = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$GroupDN")

        foreach ($member in $DefaultMembers) {
            try {
                $userResult = Find-AdsiObject -SamAccountName $member -ObjectClass "user"
                if (-not $userResult) { Write-Warning "  User '$member' not found in AD - skipping."; continue }
                $userPath = $userResult.Path
                if ($groupEntry.IsMember($userPath)) {
                    Write-Host "  User '$member' is already a member - skipping." -ForegroundColor Yellow
                } else {
                    $groupEntry.Add($userPath)
                    Write-Host "  User '$member' added to '$GroupName' successfully." -ForegroundColor Green
                }
            } catch { Write-Warning "  Could not add user '$member': $_" }
        }

        if ($PromptForAdditionalUsers) {
            Write-Host ""
            $addMore = Read-Host "  Add additional users to '$GroupName'? (Y/N)"
            if ($addMore -match "^[Yy]") {
                Write-Host "`n  Current users in the domain:" -ForegroundColor Cyan
                $allUsers = Get-AllAdsiUsers | ForEach-Object {
                    [PSCustomObject]@{
                        DisplayName = (Get-ResultDisplayName -Result $_)
                        Sam         = $_.Properties["samaccountname"][0]
                    }
                } | Sort-Object DisplayName
                Write-Host ("    {0,-34} {1}" -f "DISPLAY NAME", "USERNAME") -ForegroundColor DarkGray
                foreach ($u in $allUsers) { Write-Host ("    {0,-34} {1}" -f $u.DisplayName, $u.Sam) -ForegroundColor DarkGray }

                while ($true) {
                    Write-Host ""
                    $entry = Read-Host "  Enter a display name or username to add (blank to finish)"
                    if ([string]::IsNullOrWhiteSpace($entry)) { break }
                    $found = Find-AdsiUserByName -Term $entry
                    if ($found.Count -eq 0) { Write-Host "    No user found matching '$entry'." -ForegroundColor Yellow; continue }

                    if ($found.Count -gt 1) {
                        Write-Host "    Multiple matches:" -ForegroundColor Yellow
                        for ($i = 0; $i -lt $found.Count; $i++) {
                            $dn  = Get-ResultDisplayName -Result $found[$i]
                            $sam = $found[$i].Properties["samaccountname"][0]
                            Write-Host ("      [{0}] {1,-32} {2}" -f ($i + 1), $dn, $sam)
                        }
                        $pick = Read-Host "    Choose a number (blank to cancel)"
                        if ([string]::IsNullOrWhiteSpace($pick)) { continue }
                        $idx = ($pick -as [int]) - 1
                        if ($null -eq ($pick -as [int]) -or $idx -lt 0 -or $idx -ge $found.Count) {
                            Write-Host "    Invalid selection." -ForegroundColor Yellow; continue
                        }
                        $chosen = $found[$idx]
                    } else { $chosen = $found[0] }

                    $chosenPath = $chosen.Path
                    $chosenName = Get-ResultDisplayName -Result $chosen
                    try {
                        if ($groupEntry.IsMember($chosenPath)) {
                            Write-Host "    '$chosenName' is already a member - skipping." -ForegroundColor Yellow
                        } else {
                            $groupEntry.Add($chosenPath)
                            Write-Host "    '$chosenName' added to '$GroupName'." -ForegroundColor Green
                        }
                    } catch { Write-Warning "    Could not add '$chosenName': $_" }
                }
            }
        }
    } catch { Write-Error "  Failed to create group: $_"; exit 1 }
}

# ------------------------------------------------------------------------------
# STEP 2 - Delegate Control on the Users OU to the UserDashboard group (ADSI)
# ------------------------------------------------------------------------------
if ($SkipDelegation) {
    Write-Host "`n[STEP 2] Skipped (-SkipDelegation)." -ForegroundColor DarkYellow
} else {
    Write-Host "`n[STEP 2] Delegating OU control to '$GroupName' (via ADSI)..." -ForegroundColor Cyan
    try {
        $grpForSid = Find-AdsiObject -SamAccountName $GroupSamName -ObjectClass "group"
        if (-not $grpForSid) { throw "Group '$GroupName' could not be located for SID lookup." }
        $sidBytes  = $grpForSid.Properties["objectsid"][0]
        $groupSID  = New-Object System.Security.Principal.SecurityIdentifier($sidBytes, 0)

        $ouEntry = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$OUPath")
        $null = $ouEntry.NativeObject
        try {
            $ouEntry.Options.SecurityMasks = [System.DirectoryServices.SecurityMasks]::Dacl
        } catch {
            Write-Host "  (Note: SecurityMasks not settable here; using default masks.)" -ForegroundColor DarkGray
        }
        $ouAcl = $ouEntry.ObjectSecurity

        function New-ADAccessRule {
            param(
                [System.Security.Principal.SecurityIdentifier] $Identity,
                [System.DirectoryServices.ActiveDirectoryRights] $Rights,
                [System.Security.AccessControl.AccessControlType] $ControlType,
                [System.DirectoryServices.ActiveDirectorySecurityInheritance] $Inheritance,
                [Guid] $ObjectType    = [Guid]::Empty,
                [Guid] $InheritedType = [Guid]::Empty
            )
            return New-Object System.DirectoryServices.ActiveDirectoryAccessRule(
                $Identity, $Rights, $ControlType, $ObjectType, $Inheritance, $InheritedType)
        }

        $guidMap = @{
            "Reset-Password"            = [Guid]"00299570-246d-11d0-a768-00aa006e0529"
            "User-Account-Restrictions" = [Guid]"4c164200-20c0-11d0-a768-00aa006e0529"
            "General-Information"       = [Guid]"59ba2f42-79a2-11d0-9020-00c04fc2d3cf"
            "user"                      = [Guid]"bf967aba-0de6-11d0-a285-00aa003049e2"
            "group"                     = [Guid]"bf967a9c-0de6-11d0-a285-00aa003049e2"
            "member"                    = [Guid]"bf9679c0-0de6-11d0-a285-00aa003049e2"
        }

        $inherited   = [System.DirectoryServices.ActiveDirectorySecurityInheritance]::Descendents
        $selfAndDesc = [System.DirectoryServices.ActiveDirectorySecurityInheritance]::All
        $allow       = [System.Security.AccessControl.AccessControlType]::Allow
        $readProp    = [System.DirectoryServices.ActiveDirectoryRights]::ReadProperty
        $writeProp   = [System.DirectoryServices.ActiveDirectoryRights]::WriteProperty
        $extRight    = [System.DirectoryServices.ActiveDirectoryRights]::ExtendedRight
        $createChild = [System.DirectoryServices.ActiveDirectoryRights]::CreateChild
        $deleteChild = [System.DirectoryServices.ActiveDirectoryRights]::DeleteChild
        $fullCtrl    = [System.DirectoryServices.ActiveDirectoryRights]::GenericAll

        $rules = @(
            (New-ADAccessRule $groupSID ($createChild -bor $deleteChild) $allow $selfAndDesc $guidMap["user"] ([Guid]::Empty)),
            (New-ADAccessRule $groupSID $fullCtrl $allow $inherited ([Guid]::Empty) $guidMap["user"]),
            (New-ADAccessRule $groupSID $extRight $allow $inherited $guidMap["Reset-Password"] $guidMap["user"]),
            (New-ADAccessRule $groupSID $readProp $allow $inherited $guidMap["General-Information"] $guidMap["user"]),
            (New-ADAccessRule $groupSID $readProp $allow $inherited $guidMap["User-Account-Restrictions"] $guidMap["user"]),
            (New-ADAccessRule $groupSID ($createChild -bor $deleteChild) $allow $selfAndDesc $guidMap["group"] ([Guid]::Empty)),
            (New-ADAccessRule $groupSID $fullCtrl $allow $inherited ([Guid]::Empty) $guidMap["group"]),
            (New-ADAccessRule $groupSID $writeProp $allow $inherited $guidMap["member"] $guidMap["group"])
        )
        # IDEMPOTENT: remove any delegation ACEs this group already has on the OU,
        # then add the canonical set. Re-running therefore refreshes the exact same
        # delegation instead of stacking duplicate ACEs. Only this group's ACEs are
        # touched; every other principal on the OU is left untouched.
        $existingCount = @($ouAcl.Access | Where-Object {
            try { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -eq $groupSID.Value }
            catch { $false }
        }).Count
        if ($existingCount -gt 0) {
            Write-Host "  '$GroupName' already has $existingCount delegation ACE(s) on the OU - refreshing." -ForegroundColor Yellow
            [void]$ouAcl.PurgeAccessRules($groupSID)
        }
        foreach ($rule in $rules) { $ouAcl.AddAccessRule($rule) }

        $ouEntry.ObjectSecurity = $ouAcl
        $ouEntry.CommitChanges()
        if ($existingCount -gt 0) {
            Write-Host "  Delegation refreshed for '$GroupName' on '$OUPath' (no duplicates)." -ForegroundColor Green
        } else {
            Write-Host "  Delegation rules applied successfully to '$OUPath'." -ForegroundColor Green
        }
    } catch { Write-Error "  Failed to apply delegation: $_"; exit 1 }
}

# ------------------------------------------------------------------------------
# STEP 3 - Publish / update the UserDashboard RemoteApp (RemoteDesktop module)
#          First verifies (nothing hardcoded) that THIS host is the RD Connection
#          Broker that owns the collection - the broker is read live from the
#          deployment and the collection must exist here, or the stage is skipped.
#          The exe + icon paths are then validated by the broker (against the
#          collection's session host(s)); if the icon can't be used (e.g. a PNG
#          saved as .ico) the app is published WITHOUT the icon rather than
#          aborting. Distribution to the rest of the collection follows in Step 4.
# ------------------------------------------------------------------------------
if ($SkipRemoteApp) {
    Write-Host "`n[STEP 3] Skipped (-SkipRemoteApp)." -ForegroundColor DarkYellow
} else {
    Write-Host "`n[STEP 3] Publishing '$AppDisplayName' RemoteApp..." -ForegroundColor Cyan
    if (-not (Get-Module -ListAvailable -Name RemoteDesktop)) {
        Write-Warning "  The RemoteDesktop module is not available on this machine."
        Write-Warning "  Run with -SkipRemoteApp here and run Step 3 on the RD Connection Broker, or import the module there."
    } else {
        try {
            Import-Module RemoteDesktop -ErrorAction Stop

            # Verify (nothing hardcoded) that THIS host is the broker owning the collection.
            # Reuse the preflight result if it already ran; otherwise check now.
            if ($script:PreflightBroker) { $brokerChk = $script:PreflightBroker }
            else { $brokerChk = Test-LocalIsCollectionBroker -Collection $CollectionName }
            if (-not ($brokerChk.IsBroker -and $brokerChk.CollectionExists)) {
                Write-Warning "  This host is NOT verified as the RD Connection Broker for collection '$CollectionName'."
                Write-Warning "    On broker        : $($brokerChk.IsBroker)"
                Write-Warning "    Collection found  : $($brokerChk.CollectionExists)"
                if ($brokerChk.BrokerServers) {
                    Write-Warning "    Deployment broker(s): $($brokerChk.BrokerServers -join ', ') - run Step 3 there."
                }
                if ($brokerChk.IsBroker -and $brokerChk.Available) {
                    Write-Warning "    Collections on this broker: $($brokerChk.Available -join ', '). Set `$CollectionName accordingly."
                }
                if ($brokerChk.Detail) { Write-Warning "    Detail: $($brokerChk.Detail)" }
                Write-Warning "  Skipping Step 3 (publish must run on the broker that owns the collection)."
            } else {
                Write-Host "  Verified: this host is the RD Connection Broker for collection '$CollectionName'." -ForegroundColor Green

            $resolvedExe = $AppFilePath
            if (-not (Test-Path -LiteralPath $resolvedExe)) {
                Write-Warning "  Configured exe not found: $resolvedExe"
                $foundExe = Get-ChildItem -Path $AppFolder -Filter "UserDashboard.exe" -Recurse -ErrorAction SilentlyContinue |
                            Where-Object { $_.FullName -match '\\bin\\' } | Select-Object -First 1
                if (-not $foundExe) {
                    $foundExe = Get-ChildItem -Path $AppFolder -Filter "UserDashboard.exe" -Recurse -ErrorAction SilentlyContinue |
                                Select-Object -First 1
                }
                if ($foundExe) {
                    $resolvedExe = $foundExe.FullName
                    Write-Host "  Located exe at: $resolvedExe" -ForegroundColor Gray
                } else {
                    throw "Could not find UserDashboard.exe under '$AppFolder'. Build the project, then set `$AppFilePath."
                }
            }

            $resolvedIcon = $AppIconPath
            if (-not (Test-Path -LiteralPath $resolvedIcon)) {
                $foundIcon = Get-ChildItem -Path $AppFolder -Filter "UserDashboard.ico" -Recurse -ErrorAction SilentlyContinue |
                             Select-Object -First 1
                if ($foundIcon) {
                    $resolvedIcon = $foundIcon.FullName
                    Write-Host "  Located icon at: $resolvedIcon" -ForegroundColor Gray
                } else {
                    $resolvedIcon = $null
                    Write-Warning "  Icon not found under '$AppFolder'; publishing without a custom icon."
                }
            }

            # Stage the current icon onto the session hosts BEFORE publishing.
            # Publish validates IconPath on the session host, not on the broker, so
            # the host needs this (valid, current) icon there or it falls back to a
            # placeholder. This pushes just the .ico; the full folder still goes out
            # in Step 4. Best-effort: a host that can't be staged just uses fallback.
            if ($resolvedIcon -and (-not $LocalHostOnly)) {
                Write-Host "  Staging current icon to session hosts (so publish sets the real icon)..." -ForegroundColor Gray
                foreach ($h in (Get-TargetSessionHosts)) {
                    if (Test-IsLocalHost -Server $h) { continue }
                    if (Test-IsDomainController -Server $h -DcSet $DcSet) { continue }
                    try {
                        Send-FileToHost -Server $h -LocalPath $resolvedIcon -RemotePath $AppIconPath -Via $DeployVia
                        Write-Host "    [$h] icon staged to $AppIconPath" -ForegroundColor DarkGray
                    } catch {
                        Write-Warning "    [$h] could not stage icon: $_"
                    }
                }
            }

            # Publish, walking a chain of icon sources. RemoteApp REQUIRES an icon:
            # if no -IconPath is given it extracts one from the exe, so "no icon"
            # still needs the exe to contain an icon. We therefore try, in order:
            #   1) the custom .ico (if present)
            #   2) the exe's own embedded icon (no -IconPath)
            #   3) a guaranteed system icon (mstsc.exe) as a last resort, so the
            #      publish completes with a placeholder instead of failing the run.
            $iconSources = @()
            if ($resolvedIcon) { $iconSources += ,@{ Desc = "custom icon ($resolvedIcon)"; Path = $resolvedIcon } }
            $iconSources += ,@{ Desc = "the executable's own embedded icon"; Path = $null }
            $iconSources += ,@{ Desc = "a placeholder system icon (mstsc.exe)"; Path = (Join-Path $env:SystemRoot 'System32\mstsc.exe') }

            $published   = $false
            $usedDesc    = $null
            $usedFallback = $false
            for ($i = 0; $i -lt $iconSources.Count; $i++) {
                $src = $iconSources[$i]
                try {
                    $existingApp = Get-RDRemoteApp -CollectionName $CollectionName -Alias $AppAlias -ErrorAction SilentlyContinue
                    if ($existingApp) {
                        Write-Host "  RemoteApp '$AppAlias' exists - updating (icon: $($src.Desc))..." -ForegroundColor Yellow
                        $p = @{ CollectionName = $CollectionName; Alias = $AppAlias; FilePath = $resolvedExe; UserGroups = $RemoteAppGroups; ErrorAction = "Stop" }
                        if ($src.Path) { $p["IconPath"] = $src.Path; $p["IconIndex"] = 0 }
                        Set-RDRemoteApp @p
                    } else {
                        $p = @{ CollectionName = $CollectionName; DisplayName = $AppDisplayName; Alias = $AppAlias; FilePath = $resolvedExe; UserGroups = $RemoteAppGroups; ErrorAction = "Stop" }
                        if ($src.Path) { $p["IconPath"] = $src.Path; $p["IconIndex"] = 0 }
                        New-RDRemoteApp @p
                    }
                    $published = $true
                    $usedDesc  = $src.Desc
                    $usedFallback = ($i -gt 0)
                    break
                } catch {
                    $pubErr = "$_"
                    if ($pubErr -match 'icon' -and $i -lt ($iconSources.Count - 1)) {
                        Write-Warning "  Icon source failed ($($src.Desc)): $pubErr"
                        Write-Warning "  Trying next icon source..."
                        continue
                    }
                    throw    # non-icon failure (e.g. exe path), or all icon sources exhausted
                }
            }

            if ($published) {
                Write-Host "  RemoteApp '$AppDisplayName' published with groups: $($RemoteAppGroups -join ', ') [icon: $usedDesc]" -ForegroundColor Green
                if ($usedFallback) {
                    Write-Warning "  NOTE: the custom icon could not be used; published with $usedDesc instead."
                    Write-Warning "  Fix it properly, then re-run Step 3 to set the real icon:"
                    Write-Warning "    - Best: set <ApplicationIcon>UserDashboard.ico</ApplicationIcon> in UserDashboard.csproj and rebuild,"
                    Write-Warning "      so the exe carries a valid icon (then no -IconPath is needed)."
                    Write-Warning "    - Or regenerate UserDashboard.ico as a real multi-resolution .ico (not a renamed PNG)."
                    Write-Warning "    - The fixed file must reach the session host(s) (Step 4 deploy) before the icon will take."
                }
            }
            }
        } catch { Write-Error "  Failed to publish RemoteApp: $_"; exit 1 }
    }
}

# ------------------------------------------------------------------------------
# STEP 4 - Distribute the app folder to every session host
#          Pushes $DeploySourcePath (on this machine) to C:\GSS\UserDashboard on
#          each OTHER session host so the binaries don't have to be hand-copied.
#          Runs after publish (Step 3): the app is published from this host's
#          copy, then that copy is pushed out to the rest of the collection so
#          every host can actually serve it. Runs before the NTFS/listener stages.
#          WinRM (default): copies over a PS session (no SMB/double-hop needed).
#          SMB: robocopy to \\host\C$\GSS\UserDashboard (needs 445 + admin share).
# ------------------------------------------------------------------------------
if ($SkipDeploy) {
    Write-Host "`n[STEP 4] Skipped (-SkipDeploy)." -ForegroundColor DarkYellow
} elseif ($LocalHostOnly) {
    Write-Host "`n[STEP 4] Skipped - -LocalHostOnly (source folder is already on this host)." -ForegroundColor DarkYellow
} else {
    Write-Host "`n[STEP 4] Distributing app folder to session hosts (via $DeployVia)..." -ForegroundColor Cyan
    try {
        # "If deployed, then copy": only push when the source actually holds the app.
        $srcExe = $null
        if (Test-Path -LiteralPath $DeploySourcePath) {
            $srcExe = Get-ChildItem -Path $DeploySourcePath -Filter "UserDashboard.exe" -Recurse -ErrorAction SilentlyContinue |
                      Select-Object -First 1
        }
        if (-not $srcExe) {
            Write-Warning "  Source '$DeploySourcePath' does not contain UserDashboard.exe on this machine."
            Write-Warning "  Nothing to distribute (build/deploy the app here first, or set -DeploySource). Skipping Step 4."
        } else {
            Write-Host "  Source: $DeploySourcePath" -ForegroundColor Gray

            # Verify the build being pushed matches the version this script targets.
            try {
                $srcVer = $srcExe.VersionInfo.FileVersion
                if ($srcVer) {
                    if ($srcVer -like "$AppVersion*") {
                        Write-Host "  Source UserDashboard.exe version: $srcVer (matches target v$AppVersion)" -ForegroundColor Green
                    } else {
                        Write-Warning "  Source UserDashboard.exe is version $srcVer, but this script targets v$AppVersion."
                        Write-Warning "  You may be deploying an older/newer build than intended. Continuing anyway."
                    }
                } else {
                    Write-Host "  Source UserDashboard.exe version: (not stamped)" -ForegroundColor DarkGray
                }
            } catch { Write-Host "  (Could not read source EXE version.)" -ForegroundColor DarkGray }

            Write-Host "  Excluding top-level: $($DeployExcludeDirs -join ', ')" -ForegroundColor DarkGray

            # Top-level items to copy (skip the excluded runtime/output dirs).
            $srcItems = Get-ChildItem -LiteralPath $DeploySourcePath -Force |
                        Where-Object { $DeployExcludeDirs -notcontains $_.Name }

            $targetServers = Get-TargetSessionHosts
            foreach ($server in $targetServers) {
                if (Test-IsDomainController -Server $server -DcSet $DcSet) {
                    Write-Warning "  [$server] SKIPPED - domain controller; not copying app folder."
                    continue
                }
                if (Test-IsLocalHost -Server $server) {
                    Write-Host "  [$server] is the source host - skipping copy to self." -ForegroundColor DarkGray
                    continue
                }
                try {
                    if ($DeployVia -eq "SMB") {
                        $dest = "\\$server\C$\" + ($AppFolder -replace '^[A-Za-z]:\\','')
                        if (-not (Test-Path -LiteralPath $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
                        $xd = $DeployExcludeDirs | ForEach-Object { Join-Path $DeploySourcePath $_ }
                        $rcArgs = @($DeploySourcePath, $dest, "/E", "/R:2", "/W:2", "/NFL", "/NDL", "/NJH", "/NJS", "/NP")
                        foreach ($d in $xd) { $rcArgs += @("/XD", $d) }
                        & robocopy.exe @rcArgs | Out-Null
                        # robocopy: exit codes 0-7 = success (8+ = failure)
                        if ($LASTEXITCODE -ge 8) { throw "robocopy reported failure (exit $LASTEXITCODE)." }
                        Write-Host "  [$server] App folder copied/updated via SMB (robocopy exit $LASTEXITCODE)." -ForegroundColor Green
                    } else {
                        $sess = New-PSSession -ComputerName $server -ErrorAction Stop
                        try {
                            Invoke-Command -Session $sess -ScriptBlock {
                                param($Dest) if (-not (Test-Path -LiteralPath $Dest)) { New-Item -ItemType Directory -Path $Dest -Force | Out-Null }
                            } -ArgumentList $AppFolder
                            foreach ($item in $srcItems) {
                                Copy-Item -Path $item.FullName -Destination $AppFolder -ToSession $sess -Recurse -Force
                            }
                        } finally {
                            Remove-PSSession $sess
                        }
                        Write-Host "  [$server] App folder copied via WinRM ($($srcItems.Count) top-level item(s))." -ForegroundColor Green
                    }
                } catch {
                    Write-Warning "  [$server] Copy failed: $_"
                    Write-Warning "  [$server] (If the app is running on this host, files may be locked - retry during a maintenance window.)"
                }
            }
        }
    } catch { Write-Error "  Failed during app-folder distribution: $_"; exit 1 }
}

# ------------------------------------------------------------------------------
# STEP 4b - Place a Public Desktop shortcut on each session host
#           Creates UserDashboard.lnk on C:\Users\Public\Desktop pointing at
#           $AppFilePath (C:\GSS\UserDashboard\bin\Release\net48\UserDashboard.exe).
#           A shortcut (not a copied EXE) keeps BaseDirectory / config / deps on
#           the real install path. The .lnk is built once locally (target path is
#           identical on every host) then copied via WinRM/SMB. Also removes a
#           leftover Public-Desktop UserDashboard.exe from older Step 4b runs.
#           Runs under -LocalHostOnly; skipped with -SkipDeploy.
# ------------------------------------------------------------------------------
if ($SkipDeploy) {
    Write-Host "`n[STEP 4b] Skipped (-SkipDeploy)." -ForegroundColor DarkYellow
} else {
    Write-Host "`n[STEP 4b] Placing Public Desktop shortcut on each session host (via $DeployVia)..." -ForegroundColor Cyan
    try {
        $shortcutTarget = $AppFilePath
        if (-not (Test-Path -LiteralPath $shortcutTarget)) {
            $found = $null
            if (Test-Path -LiteralPath $DeploySourcePath) {
                $found = Get-ChildItem -Path $DeploySourcePath -Filter "UserDashboard.exe" -Recurse -ErrorAction SilentlyContinue |
                         Select-Object -First 1
            }
            if ($found) {
                # Prefer the canonical install path in the shortcut even when we
                # only verified the EXE exists under the deploy source.
                Write-Host "  Note: '$AppFilePath' not present locally; shortcut will still target that path on each host (Step 4 installs it there)." -ForegroundColor DarkGray
            } else {
                Write-Warning "  Could not find UserDashboard.exe at '$AppFilePath' (or under '$DeploySourcePath')."
                Write-Warning "  Skipping Public Desktop shortcut."
                $shortcutTarget = $null
            }
        }

        if ($shortcutTarget) {
            $remoteShortcut = Join-Path $PublicDesktopDir $PublicDesktopShortcut
            $iconForLnk     = if (Test-Path -LiteralPath $AppIconPath) { $AppIconPath } else { $shortcutTarget }
            $workDir        = Split-Path -Parent $shortcutTarget

            $tempLnk = Join-Path $env:TEMP ("UserDashboard-PublicDesktop-{0}.lnk" -f [guid]::NewGuid().ToString("N"))
            try {
                New-UserDashboardShortcut -ShortcutPath $tempLnk -TargetPath $shortcutTarget `
                    -WorkingDirectory $workDir -IconLocation $iconForLnk `
                    -Description "User Management Dashboard"

                Write-Host "  Shortcut target: $shortcutTarget" -ForegroundColor Gray
                Write-Host "  Dest:            $remoteShortcut  (on each host)" -ForegroundColor Gray

                $legacyDesktopExe = Join-Path $PublicDesktopDir "UserDashboard.exe"
                $targetServers = Get-TargetSessionHosts
                foreach ($server in $targetServers) {
                    if (Test-IsDomainController -Server $server -DcSet $DcSet) {
                        Write-Warning "  [$server] SKIPPED - domain controller; not placing Public Desktop shortcut."
                        continue
                    }
                    try {
                        if (Test-IsLocalHost -Server $server) {
                            if (-not (Test-Path -LiteralPath $PublicDesktopDir)) {
                                New-Item -ItemType Directory -Path $PublicDesktopDir -Force | Out-Null
                            }
                            Copy-Item -LiteralPath $tempLnk -Destination $remoteShortcut -Force -ErrorAction Stop
                            if (Test-Path -LiteralPath $legacyDesktopExe) {
                                Remove-Item -LiteralPath $legacyDesktopExe -Force -ErrorAction SilentlyContinue
                                Write-Host "  [$server] Removed leftover Public Desktop UserDashboard.exe." -ForegroundColor DarkGray
                            }
                            Write-Host "  [$server] Public Desktop shortcut placed (local)." -ForegroundColor Green
                        } else {
                            Send-FileToHost -Server $server -LocalPath $tempLnk `
                                -RemotePath $remoteShortcut -Via $DeployVia
                            # Best-effort cleanup of a prior EXE-on-desktop deploy.
                            try {
                                $removedLegacy = Invoke-OnHost -Server $server -ScriptBlock {
                                    param($ExePath)
                                    if (Test-Path -LiteralPath $ExePath) {
                                        Remove-Item -LiteralPath $ExePath -Force -ErrorAction Stop
                                        return $true
                                    }
                                    return $false
                                } -ArgumentList @($legacyDesktopExe)
                                if ($removedLegacy) {
                                    Write-Host "  [$server] Removed leftover Public Desktop UserDashboard.exe." -ForegroundColor DarkGray
                                }
                            } catch {
                                # Non-fatal: shortcut is what matters.
                            }
                            Write-Host "  [$server] Public Desktop shortcut placed via $DeployVia." -ForegroundColor Green
                        }
                    } catch {
                        Write-Warning "  [$server] Public Desktop shortcut failed: $_"
                    }
                }
            } finally {
                Remove-Item -LiteralPath $tempLnk -Force -ErrorAction SilentlyContinue
            }
        }
    } catch { Write-Error "  Failed during Public Desktop shortcut distribution: $_"; exit 1 }
}

# ------------------------------------------------------------------------------
# STEP 5 - Grant the group NTFS access to the app + Log folders on every host
# ------------------------------------------------------------------------------
if ($SkipFolderAcl) {
    Write-Host "`n[STEP 5] Skipped (-SkipFolderAcl)." -ForegroundColor DarkYellow
} else {
    Write-Host "`n[STEP 5] Granting '$GroupName' access to app + Log folders across session hosts..." -ForegroundColor Cyan
    try {
        $groupSidString = Resolve-GroupSidString

        $applyAclScript = {
            param($SidString, $Targets)

            # HOST-SIDE DC GUARD: never create/modify these folders on a DC.
            $role = $null
            try   { $role = (Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop).DomainRole }
            catch { try { $role = (Get-WmiObject -Class Win32_ComputerSystem -ErrorAction Stop).DomainRole } catch {} }
            if ($null -ne $role -and [int]$role -ge 4) {
                return "SKIPPED: $env:COMPUTERNAME is a domain controller (DomainRole=$role). Folders NOT modified."
            }

            $sid         = New-Object System.Security.Principal.SecurityIdentifier($SidString)
            $inherit     = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
            $propagation = [System.Security.AccessControl.PropagationFlags]::None
            $accessAllow = [System.Security.AccessControl.AccessControlType]::Allow
            $applied = @()
            foreach ($t in $Targets) {
                $path = $t.Path
                if (-not (Test-Path -LiteralPath $path)) {
                    if ($t.Create) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
                    else { throw "Folder '$path' does not exist on $env:COMPUTERNAME." }
                }
                $rights = [System.Security.AccessControl.FileSystemRights]::($t.Access)
                $fsRule = New-Object System.Security.AccessControl.FileSystemAccessRule($sid, $rights, $inherit, $propagation, $accessAllow)
                $acl = Get-Acl -Path $path
                $acl.AddAccessRule($fsRule)
                Set-Acl -Path $path -AclObject $acl
                $applied += "$($t.Access) on $path"
            }
            return "granted " + ($applied -join "; ")
        }

        $targetServers = Get-TargetSessionHosts
        $aclTargets = @(
            @{ Path = $AppFolder; Access = $FolderAccessLevel; Create = $false },
            @{ Path = $LogFolder; Access = $LogAccessLevel;    Create = $true  }
        )

        foreach ($server in $targetServers) {
            if (Test-IsDomainController -Server $server -DcSet $DcSet) {
                Write-Warning "  [$server] SKIPPED - domain controller; folders not modified."
                continue
            }
            try {
                $result = Invoke-OnHost -Server $server -ScriptBlock $applyAclScript `
                            -ArgumentList @($groupSidString, $aclTargets)
                Write-Host "  [$server] $result" -ForegroundColor Green
            } catch { Write-Warning "  [$server] Failed: $_" }
        }
    } catch { Write-Error "  Failed to set folder permissions: $_"; exit 1 }
}

# ------------------------------------------------------------------------------
# STEP 5b - Register the Windows Event Log source used by the dashboard's
#           dual audit sink (v2.4.0+). Creating an event source requires admin
#           ONCE per host; after that, the (non-admin) dashboard writes every
#           audit entry to the Application log under this source in addition
#           to the CSV. If the source is missing the app skips the event log
#           silently, so this step is safe to defer - but do run it: the event
#           log is the tamper-resistant (AU-9-friendly) copy of the trail.
#           Idempotent: re-running is a no-op when the source already exists.
# ------------------------------------------------------------------------------
if ($SkipEventLogSource) {
    Write-Host "`n[STEP 5b] Skipped (-SkipEventLogSource)." -ForegroundColor DarkYellow
} else {
    Write-Host "`n[STEP 5b] Registering 'GSS-UserDashboard' event log source on session hosts..." -ForegroundColor Cyan

    $eventSourceScript = {
        # HOST-SIDE DC GUARD: the dashboard never runs on a DC; don't touch DCs.
        $role = $null
        try   { $role = (Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop).DomainRole }
        catch { try { $role = (Get-WmiObject -Class Win32_ComputerSystem -ErrorAction Stop).DomainRole } catch {} }
        if ($null -ne $role -and [int]$role -ge 4) {
            return "SKIPPED: $env:COMPUTERNAME is a domain controller (DomainRole=$role)."
        }

        $src = "GSS-UserDashboard"
        if ([System.Diagnostics.EventLog]::SourceExists($src)) {
            return "source '$src' already registered - no change."
        }
        New-EventLog -LogName Application -Source $src
        if ([System.Diagnostics.EventLog]::SourceExists($src)) {
            return "source '$src' REGISTERED in the Application log."
        }
        throw "source '$src' still missing after New-EventLog."
    }

    try {
        $targetServers = Get-TargetSessionHosts
        foreach ($server in $targetServers) {
            if (Test-IsDomainController -Server $server -DcSet $DcSet) {
                Write-Warning "  [$server] SKIPPED - domain controller."
                continue
            }
            try {
                $result = Invoke-OnHost -Server $server -ScriptBlock $eventSourceScript -ArgumentList @()
                Write-Host "  [$server] $result" -ForegroundColor Green
            } catch { Write-Warning "  [$server] Failed: $_" }
        }
    } catch { Write-Warning "  Event log source registration stage failed: $_" }
}

# ==============================================================================
# LISTENER PASS  (shared by preview and apply runs of STEP 6)
#   Runs the mask-verification + grant logic across every target host. When
#   $DoApply is $false, nothing is written anywhere - this is the "clean run" /
#   dry-run pass. When $true, it writes the ACE (and backs up the prior state)
#   on hosts that actually need the change. Returns a summary so the caller can
#   decide what to do next (report, prompt, or move on).
# ==============================================================================
function Invoke-ListenerPass {
    param(
        [bool]   $DoApply,
        [string] $GroupSidString,
        [string] $Stamp
    )

    $result = [ordered]@{
        Pending        = New-Object System.Collections.Generic.List[string]  # would change, not yet applied
        Applied        = New-Object System.Collections.Generic.List[string]
        AlreadyCorrect = New-Object System.Collections.Generic.List[string]
        MaskSkipped    = New-Object System.Collections.Generic.List[string]
        Errored        = New-Object System.Collections.Generic.List[string]
    }

    $targetServers = Get-TargetSessionHosts
    foreach ($server in $targetServers) {
        Write-Host "`n  --- $server ---" -ForegroundColor Cyan
        if (Test-IsDomainController -Server $server -DcSet $DcSet) {
            Write-Warning "    SKIPPED - domain controller; RDP-Tcp listener NOT modified."
            $result.Errored.Add($server)
            continue
        }

        # ----- Per-host mask verification gate --------------------------------
        if (-not $SkipMaskVerification) {
            $maskOk  = $false
            $os      = $null
            try { $os = Invoke-OnHost -Server $server -ScriptBlock $osInfoScript -ArgumentList @() } catch {}
            $osName  = if ($os) { "$($os.Caption) (build $($os.Build))" } else { "(OS unknown)" }
            $verMask = if ($os -and $VerifiedMaskByBuild.ContainsKey([int]$os.Build)) { [uint32]$VerifiedMaskByBuild[[int]$os.Build] } else { $null }

            if ($null -ne $verMask -and $verMask -eq [uint32]$ListenerMask) {
                Write-Host ("    OS: {0} - mask 0x{1:X} VERIFIED for this build." -f $osName, $ListenerMask) -ForegroundColor Green
                $maskOk = $true
            } else {
                # Unverified build (or an overridden mask): read the host's
                # reference principals (read-only) to confirm the layout here.
                Write-Host ("    OS: {0} - mask not pre-verified; inspecting listener on host..." -f $osName) -ForegroundColor Yellow
                $m = $null
                try { $m = Invoke-OnHost -Server $server -ScriptBlock $measureScript -ArgumentList @($GroupAccount, $TerminalName) } catch { }
                if ($null -eq $m -or $m.Error) {
                    $why = if ($m) { $m.Error } else { "inspection call failed" }
                    Write-Warning "    COULD NOT VERIFY MASK on $server ($why)."
                } elseif ([uint32]$m.TargetMask -eq [uint32]$ListenerMask) {
                    Write-Host ("    Standard layout confirmed; target mask 0x{0:X} matches requested 0x{1:X}. OK to proceed." -f [uint32]$m.TargetMask, [uint32]$ListenerMask) -ForegroundColor Green
                    $maskOk = $true
                } else {
                    Write-Host "    !!! MASK MISMATCH !!!" -ForegroundColor Red
                    Write-Host ("    Requested 0x{0:X} but the standard layout on {1} yields 0x{2:X}." -f [uint32]$ListenerMask, $server, [uint32]$m.TargetMask) -ForegroundColor Red
                    Write-Host ("    Re-run for this build with:  -ListenerMask 0x{0:X}   (after confirming with -MeasureMask)" -f [uint32]$m.TargetMask) -ForegroundColor Red
                }
            }

            if (-not $maskOk) {
                Write-Warning "    SKIPPED $server - listener NOT modified (mask unverified). Use -SkipMaskVerification to force."
                $result.MaskSkipped.Add($server)
                continue
            }
        }
        # ----------------------------------------------------------------------

        try {
            $r = Invoke-OnHost -Server $server -ScriptBlock $listenerScript `
                    -ArgumentList @($GroupSidString, $GroupAccount, $TerminalName, $ListenerMask, [bool]$DoApply)

            if ($r.Error) { Write-Warning "    $($r.Error)"; $result.Errored.Add($server); continue }

            if ($r.AlreadyCorrect) {
                Write-Host ("    Already granted exactly 0x{0:X} - no change, no backup." -f $ListenerMask) -ForegroundColor Green
                $result.AlreadyCorrect.Add($server)
                continue
            }

            Write-Host "    SDDL before: $($r.SddlBefore)" -ForegroundColor DarkGray
            Write-Host "    SDDL after : $($r.SddlAfter)"  -ForegroundColor Gray
            Write-Host ("    Planned ACE: (A;;0x{0:X};;;{1})" -f $ListenerMask, $GroupSidString) -ForegroundColor Green

            if ($DoApply) {
                # Back up the pre-change state next to the script.
                if ($r.HadExplicit -and $r.PreBytes) {
                    $bk = Join-Path $BackupDir "$TerminalName-Security-$($r.Host)-$Stamp-PREAPPLY.bin"
                    [System.IO.File]::WriteAllBytes($bk, [byte[]]$r.PreBytes)
                } else {
                    # No explicit descriptor originally -> marker so restore reverts to defaults.
                    $bk = Join-Path $BackupDir "$TerminalName-Security-$($r.Host)-$Stamp-PREAPPLY.bin"
                    Set-Content -Path $bk -Value "NO_EXPLICIT_SECURITY_VALUE" -Encoding ASCII
                }
                Write-Host "    Backup     : $bk" -ForegroundColor DarkGray
                if ($r.Applied) {
                    Write-Host "    APPLIED. Verify: $($r.Verify)" -ForegroundColor Green
                    $result.Applied.Add($server)
                } else {
                    Write-Warning "    Apply requested but host reported no change."
                    $result.Errored.Add($server)
                }
            } else {
                # Preview only: this host has a real, pending change.
                $result.Pending.Add($server)
            }
        } catch {
            Write-Warning "  [$server] Failed: $_"
            $result.Errored.Add($server)
        }
    }

    return [pscustomobject]$result
}

# ------------------------------------------------------------------------------
# STEP 6 - Grant the group Query + Logoff on the RDP-Tcp listener (per host)
#          Always runs a clean dry-run pass first (writes nothing). If that
#          pass finds hosts that need the grant, it either applies immediately
#          (-ApplyListener, or -Unattended -ApplyListener) or - in a normal
#          interactive session - asks for a Y/N confirmation before writing
#          anything. Backups land in <script folder>\RDS-Backup, named per host.
# ------------------------------------------------------------------------------
if ($SkipListener) {
    Write-Host "`n[STEP 6] Skipped (-SkipListener)." -ForegroundColor DarkYellow
} else {
    Write-Host "`n[STEP 6] Listener Query+Logoff grant (mask 0x$('{0:X}' -f $ListenerMask))" -ForegroundColor Cyan

    # ----- MASK SAFETY FLAG (read this) ---------------------------------------
    Write-Host "  ********************************************************************" -ForegroundColor Yellow
    Write-Host "  * MASK SAFETY: 0x$('{0:X}' -f $ListenerMask) is only VERIFIED on the builds listed in    *" -ForegroundColor Yellow
    Write-Host "  * `$VerifiedMaskByBuild (Windows Server 2022 by default). On any other  *" -ForegroundColor Yellow
    Write-Host "  * build, the script MEASURES the Query+Logoff bits on the host first  *" -ForegroundColor Yellow
    Write-Host "  * and REFUSES to write if they do not match - so an unverified build  *" -ForegroundColor Yellow
    Write-Host "  * cannot get the wrong rights. -SkipMaskVerification disables this.   *" -ForegroundColor Yellow
    Write-Host "  ********************************************************************" -ForegroundColor Yellow
    if ($SkipMaskVerification) {
        Write-Warning "  -SkipMaskVerification is set: the mask will be written WITHOUT per-host verification."
    }

    try {
        $groupSidString = Resolve-GroupSidString
        Write-Host "  Group SID: $groupSidString" -ForegroundColor Gray
        Write-Host "  Group account (WMI): $GroupAccount" -ForegroundColor Gray

        $stamp = Get-Date -Format "yyyyMMdd_HHmmss"

        # --- Pass 1: always a clean, read-only dry run - nothing is written. ---
        Write-Host "`n  --- Dry run: checking every target host (nothing will be written yet) ---" -ForegroundColor Cyan
        $preview = Invoke-ListenerPass -DoApply:$false -GroupSidString $groupSidString -Stamp $stamp
        $pending = @($preview.Pending)

        Write-Host ""
        Write-Host "  --- Dry-run summary ---" -ForegroundColor Cyan
        Write-Host ("    Already correct : {0}" -f $preview.AlreadyCorrect.Count) -ForegroundColor Green
        Write-Host ("    Would change    : {0}" -f $pending.Count) -ForegroundColor $(if ($pending.Count -gt 0) { "Yellow" } else { "Green" })
        Write-Host ("    Mask unverified : {0}" -f $preview.MaskSkipped.Count) -ForegroundColor $(if ($preview.MaskSkipped.Count -gt 0) { "Yellow" } else { "Green" })
        Write-Host ("    Errored         : {0}" -f $preview.Errored.Count) -ForegroundColor $(if ($preview.Errored.Count -gt 0) { "Red" } else { "Green" })

        if ($preview.MaskSkipped.Count -gt 0) {
            Write-Host ""
            Write-Warning "  MASK NOT VERIFIED on $($preview.MaskSkipped.Count) host(s); these will be left unchanged:"
            $preview.MaskSkipped | ForEach-Object { Write-Warning "    - $_" }
            Write-Warning "  Confirm the correct mask on those builds with:  .\Setup-UserDashboard.ps1 -MeasureMask"
            Write-Warning "  then either add the build to `$VerifiedMaskByBuild or re-run with the right -ListenerMask."
        }

        if ($pending.Count -eq 0) {
            Write-Host "`n  No listener changes are needed - nothing to apply." -ForegroundColor Green
        } else {
            Write-Host ("`n  {0} host(s) need the Query+Logoff grant applied: {1}" -f $pending.Count, ($pending -join ', ')) -ForegroundColor Yellow

            # --- Decide whether to actually write, and whether to ask first. ---
            $doApplyNow = $false
            if ($Unattended) {
                if ($ApplyListener) {
                    $doApplyNow = $true
                    Write-Host "  -Unattended with -ApplyListener: proceeding without a prompt." -ForegroundColor Gray
                } else {
                    Write-Host "  -Unattended without -ApplyListener: leaving this as a dry run." -ForegroundColor DarkYellow
                    Write-Host "  Re-run with -ApplyListener (still -Unattended) to apply without prompting." -ForegroundColor DarkYellow
                }
            } elseif ($ApplyListener) {
                $doApplyNow = $true
                Write-Host "  -ApplyListener was set: proceeding without an additional prompt." -ForegroundColor Gray
            } else {
                Write-Host ""
                $ans = Read-Host "  Apply these listener changes now? (Y/N)"
                if ($ans -match '^[Yy]') {
                    $doApplyNow = $true
                } else {
                    Write-Host "  Skipped - nothing was written. Re-run (and answer Y) or pass -ApplyListener to apply later." -ForegroundColor DarkYellow
                }
            }

            # --- Pass 2: apply, only if confirmed/authorized above. ---
            if ($doApplyNow) {
                if (-not (Test-Path $BackupDir)) {
                    New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
                }
                Write-Host "`n  --- Applying listener grant ---" -ForegroundColor Cyan
                $apply = Invoke-ListenerPass -DoApply:$true -GroupSidString $groupSidString -Stamp $stamp

                Write-Host ""
                Write-Host "  --- Apply summary ---" -ForegroundColor Cyan
                Write-Host ("    Applied         : {0}" -f $apply.Applied.Count) -ForegroundColor Green
                Write-Host ("    Already correct : {0}" -f $apply.AlreadyCorrect.Count) -ForegroundColor Green
                Write-Host ("    Mask unverified : {0}" -f $apply.MaskSkipped.Count) -ForegroundColor $(if ($apply.MaskSkipped.Count -gt 0) { "Yellow" } else { "Green" })
                Write-Host ("    Errored         : {0}" -f $apply.Errored.Count) -ForegroundColor $(if ($apply.Errored.Count -gt 0) { "Red" } else { "Green" })

                Write-Host @"

  POST-APPLY CHECKS (keep your rescue admin session open):
    1. Each host above should show Allowed = $ListenerMask, Denied = 0 for $GroupAccount.
    2. A non-admin member logs off a test session via UserDashboard -> should SUCCEED.
    3. That member tries to RDP into the host directly -> should be REFUSED.
    4. Normal users can still RDP in (open a fresh session as a normal user).

  ROLLBACK a host:
    .\Setup-UserDashboard.ps1 -RestoreListener ``
        -BackupFile "$BackupDir\$TerminalName-Security-<HOST>-<stamp>-PREAPPLY.bin" ``
        -RestoreHosts <HOST>
"@ -ForegroundColor Gray
            }
        }
    } catch { Write-Error "  Failed during listener grant: $_"; exit 1 }
}

# ------------------------------------------------------------------------------
Write-Host "`n[DONE] UserDashboard v$AppVersion setup completed.`n" -ForegroundColor Green
