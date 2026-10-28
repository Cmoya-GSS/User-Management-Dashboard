using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UserDashboard.Models;
using UserDashboard.Services;

namespace UserDashboard;

/// <summary>
/// Global Shop company-code access is modeled as one AD security group per
/// code, named {prefix}{code} (default prefix "CC-"). Users with no such
/// memberships are unrestricted and see every company code at logon.
///
/// Existing customer environments often already have their own groups and
/// GPOs (names and combinations vary). Those are left alone. This helper
/// only recognizes the CC-* prefix; it does not parse or manage legacy
/// combination groups.
/// </summary>
internal static class CompanyCodeHelper
{
    private static readonly Regex CodePattern =
        new(@"^[A-Z0-9][A-Z0-9-]*$", RegexOptions.CultureInvariant);

    public static string Prefix => AppConfig.CompanyCodeGroupPrefix;

    public static string PrimaryAttribute => AppConfig.CompanyCodePrimaryAttribute;

    public static string PrimaryMarker => Constants.CompanyCodePrimaryMarker;

    /// <summary>
    /// comment/info hold free text, so the company code is stored as GSSCC:CODE
    /// rather than replacing the whole attribute.
    /// </summary>
    public static bool UsesEmbeddedMarker(string ldapName) =>
        ldapName.Equals("comment", StringComparison.OrdinalIgnoreCase) ||
        ldapName.Equals("info", StringComparison.OrdinalIgnoreCase);

    public static string? ParsePrimaryValue(string? raw, string ldapName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (UsesEmbeddedMarker(ldapName))
        {
            var m = Regex.Match(raw, @"GSSCC:([A-Z0-9-]+)",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        return raw.Trim().ToUpperInvariant();
    }

    public static string ApplyPrimaryMarker(string? existing, string? code)
    {
        string rest = Regex.Replace(existing ?? string.Empty,
                                    @"GSSCC:[A-Z0-9-]+\s*",
                                    string.Empty,
                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                           .Trim();
        if (string.IsNullOrWhiteSpace(code)) return rest;
        string token = PrimaryMarker + code.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(rest) ? token : token + Environment.NewLine + rest;
    }

    public static bool IsPrefixedCompanyCodeGroup(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName)) return false;
        string prefix = Prefix;
        return groupName!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && groupName.Length > prefix.Length;
    }

    /// <summary>
    /// Groups this program owns (CC-*). Hidden from generic Manage Security
    /// Groups. Customer legacy groups are not included.
    /// </summary>
    public static bool IsCompanyCodeGroup(string? groupName) =>
        IsPrefixedCompanyCodeGroup(groupName);

    public static string GroupNameForCode(string code) => Prefix + code;

    /// <summary>Code from a CC-* group name, or null.</summary>
    public static string? CodeFromGroupName(string? groupName)
    {
        if (!IsPrefixedCompanyCodeGroup(groupName)) return null;
        return groupName!.Substring(Prefix.Length).Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Normalize operator input into a company code. Strips a leading prefix
    /// if the operator typed the full group name.
    /// </summary>
    public static bool TryNormalizeCode(string? raw, out string code, out string error)
    {
        code  = (raw ?? string.Empty).Trim().ToUpperInvariant();
        error = string.Empty;

        string prefix = Prefix.Trim().ToUpperInvariant();
        if (prefix.Length > 0 && code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            code = code.Substring(prefix.Length).Trim().ToUpperInvariant();

        if (code.Length == 0)
        {
            error = "Enter a company code.";
            return false;
        }

        int maxCode = Math.Max(1, 64 - Prefix.Length);
        if (code.Length > maxCode)
        {
            error = $"Company code is too long (max {maxCode} characters).";
            return false;
        }

        if (!CodePattern.IsMatch(code))
        {
            error = "Use letters, numbers, and hyphens only (e.g. 100 or ACME).";
            return false;
        }

        return true;
    }

    public static List<string> CodesFromUser(AdUserInfo user)
    {
        if (user?.MemberOf == null) return new List<string>();

        return user.MemberOf
            .Select(AdService.ExtractCn)
            .Select(CodeFromGroupName)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
