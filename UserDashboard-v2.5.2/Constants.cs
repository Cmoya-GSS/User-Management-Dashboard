using System.Collections.Generic;
using System.Drawing;

namespace UserDashboard;

/// <summary>
/// Mirrors the top-of-file globals from UserDashboard.ps1:
/// brand colors, excluded users/groups, hidden SAM accounts, audit log dir.
/// </summary>
internal static class Constants
{
    // ---- Brand colors -----------------------------------------------------
    public static readonly Color GssGreen  = Color.FromArgb(  0, 146,  75); // logo green
    public static readonly Color GssOrange = Color.FromArgb(243, 112,  33); // CTA orange

    // Disabled-account row tinting
    public static readonly Color DisabledAcctBack    = Color.FromArgb(255, 230, 230);
    public static readonly Color DisabledAcctSelBack = Color.FromArgb(255, 200, 200);

    // Read-only column tinting
    public static readonly Color ReadonlyColBack = Color.FromArgb(248, 249, 250);

    // Alternating row tint
    public static readonly Color AlternatingRowBack = Color.FromArgb(236, 245, 240);

    // ---- Audit log --------------------------------------------------------
    // Primary: C:\GSS\UserDashboard\Log (matches the GSS deployment convention).
    // This path is pre-created with appropriate ACLs as part of the install.
    // If for any reason that path is not writable (folder missing, ACL change),
    // AuditLogger falls back to %LOCALAPPDATA%\GSS\UserDashboard\Log so the
    // dashboard never runs without an audit trail.
    public const string AuditDir = @"C:\GSS\UserDashboard\Log";

    /// <summary>Per-user fallback if the primary AuditDir is not writable.</summary>
    public static readonly string AuditDirFallback = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "GSS", "UserDashboard", "Log");

    // ---- RDS session hosts (logoff feature) -------------------------------
    // OPTIONAL supplement to automatic discovery. The dashboard auto-detects
    // session hosts from Active Directory (computers with a TERMSRV SPN that are
    // not domain controllers), confirmed by a live session query, and always
    // includes the local machine - so this list normally stays EMPTY and the
    // tool works unchanged in any domain, picking up new hosts automatically.
    // Only add entries here to force-include a host that AD discovery can't see
    // (e.g. a session host in a different domain/forest, or not yet in AD).
    public static readonly string[] RdsSessionHosts = new string[]
    {
        // "EXTRAHOST.otherdomain.com",
    };

    // ---- End-user password page (RD Web) -----------------------------------
    // OPTIONAL override for the RD Web password-change URL shown to operators
    // (Info menu and the Reset Password confirmation). Leave empty ("") to
    // auto-detect: the dashboard reads the AD Builtin group "RDS Remote Access
    // Servers" and builds https://<gateway-fqdn>/RDWeb/Pages/en-US/password.aspx
    // from the gateway's DNS name, so it works unchanged in any domain.
    // Only set this if the public URL differs from the gateway's AD name.
    public const string PasswordChangeUrlOverride = "";

    // ---- Password rules ---------------------------------------------------
    public const int MinPasswordLength = 11;

    /// <summary>
    /// AD group that grants UserDashboard app access plus delegated rights to
    /// manage users and log off RDS sessions. Adding members requires explicit
    /// operator confirmation in the group-management dialogs.
    /// </summary>
    public const string UserDashboardAccessGroup = "UserDashboard";

    /// <summary>
    /// Security groups that grant Global Shop company-code visibility are
    /// named {prefix}{code}, e.g. CC-100. Users in none of these groups are
    /// unrestricted (they see every company code).
    /// </summary>
    public const string CompanyCodeGroupPrefix = "CC-";

    /// <summary>
    /// LDAP attribute storing the user's default / selected company code
    /// (GSSLOGIN.DIR SELECTEDCOMPANYCODE). Must exist in the base AD schema
    /// (not Exchange-only attributes like extensionAttribute1). The value is
    /// stored as <c>GSSCC:CODE</c> so an existing comment is preserved.
    /// </summary>
    public const string CompanyCodePrimaryAttribute = "comment";

    /// <summary>Prefix written into the primary attribute, e.g. GSSCC:PLA.</summary>
    public const string CompanyCodePrimaryMarker = "GSSCC:";

    // ---- Hidden / excluded principals -------------------------------------

    /// <summary>
    /// Accounts the dashboard must NEVER log off, regardless of who the
    /// operator is or which screen the logoff comes from (per-user action,
    /// bulk action, or the Active Sessions viewer). Enforced centrally in
    /// RdsService.LogoffSession as the final backstop. Site config can add
    /// more via "ProtectedLogoffAccounts"; compiled entries cannot be removed.
    /// </summary>
    public static readonly HashSet<string> ProtectedLogoffAccounts =
        new(System.StringComparer.OrdinalIgnoreCase) { "Administrator", "GLOBALSHOP" };

    /// <summary>SAM names that should never even appear in the grid.</summary>
    public static readonly HashSet<string> HiddenSamAccountNames =
        new(System.StringComparer.OrdinalIgnoreCase) { "ssm-user" };

    /// <summary>Built-in / service users to filter out of the dashboard.</summary>
    public static readonly HashSet<string> ExcludedUsers =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            "Administrator", "Guest", "NINJA", "Globalshop", "GSSAdmin", "krbtgt"
        };

    /// <summary>Built-in / privileged groups to suppress in the group filter and on rows.</summary>
    public static readonly HashSet<string> ExcludedGroups =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            "Account Operators",
            "Administrators",
            "Allowed RODC Password Replication Group",
            "Backup Operators",
            "Cert Publishers",
            "Cloneable Domain Controllers",
            "Denied RODC Password Replication Group",
            "DnsAdmins",
            "DnsUpdateProxy",
            "Domain Admins",
            "Domain Computers",
            "Domain Controllers",
            "Domain Guests",
            "Domain Users",
            "Enterprise Admins",
            "Enterprise Key Admins",
            "Enterprise Read-only Domain Controllers",
            "Group Policy Creator Owners",
            "Key Admins",
            "OfficeAllowed",
            "OfficeDenied",
            "Pre-Windows 2000 Compatible Access",
            "Print Operators",
            "Protected Users",
            "RAS and IAS Servers",
            "Read-only Domain Controllers",
            "Replicator",
            "Schema Admins",
            "Server Operators"
        };

    // ---- Grid columns -----------------------------------------------------
    public const string ColFirstName       = "First Name";
    public const string ColMiddleInitials  = "Middle Initials";
    public const string ColLastName        = "Last Name";
    public const string ColDisplayName     = "Display Name";
    public const string ColUsername        = "Username";
    public const string ColLastLogin       = "Last Login";
    public const string ColSecurityGroups  = "Security Groups";

    public static readonly string[] AllColumns =
    {
        ColFirstName, ColMiddleInitials, ColLastName, ColDisplayName,
        ColUsername, ColLastLogin, ColSecurityGroups
    };

    public static readonly string[] ReadOnlyColumns =
    {
        ColUsername, ColLastLogin, ColSecurityGroups
    };
}
