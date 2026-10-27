using System;
using System.Collections.Generic;

namespace UserDashboard.Models;

/// <summary>
/// Snapshot of one Active Directory user. Replaces the dynamic
/// Get-ADUser objects that were cached in $script:allUsers in the
/// PowerShell version.
/// </summary>
public sealed class AdUserInfo
{
    public string SamAccountName    { get; set; } = string.Empty;
    public string? UserPrincipalName { get; set; }
    public string? DisplayName       { get; set; }
    public string? GivenName         { get; set; }
    public string? Surname           { get; set; }
    public string? Initials          { get; set; }
    public string? MiddleName        { get; set; }
    public string? DistinguishedName { get; set; }

    /// <summary>True = enabled in AD (the common case).</summary>
    public bool Enabled { get; set; } = true;
    public bool LockedOut { get; set; }

    public DateTime? LastLogonDate { get; set; }

    /// <summary>List of distinguished names of groups the user is a member of.</summary>
    public List<string> MemberOf { get; set; } = new();
}
