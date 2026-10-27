using System;
using System.DirectoryServices;
using System.Linq;

namespace UserDashboard.Services;

/// <summary>
/// Locates the RD Web Access password-change page for this deployment:
///   https://&lt;gateway-fqdn&gt;/RDWeb/Pages/en-US/password.aspx
///
/// Discovery is domain-agnostic and works for non-admin users:
/// RDS deployments register the RD Gateway / RD Web Access servers in the AD
/// Builtin group "RDS Remote Access Servers". Any authenticated user can read
/// that group's membership, so we resolve a member's dNSHostName (e.g.
/// ACI.gsserpgov.com) and build the URL from it. If the deployment's external
/// URL ever differs from the gateway's AD name, set
/// AppConfig.PasswordChangeUrlOverride and that wins.
/// </summary>
internal static class RdWebLocator
{
    private static string? _cached;
    private static bool _resolved;

    /// <summary>
    /// Full URL of the RD Web password page, or null if it can't be determined.
    /// Cached for the process lifetime.
    /// </summary>
    public static string? GetPasswordChangeUrl()
    {
        if (_resolved) return _cached;
        _resolved = true;

        // 1. Explicit override wins (for environments where the public URL
        //    doesn't match the gateway's AD computer name).
        if (!string.IsNullOrWhiteSpace(AppConfig.PasswordChangeUrlOverride))
        {
            _cached = AppConfig.PasswordChangeUrlOverride.Trim();
            return _cached;
        }

        // 2. AD Builtin group "RDS Remote Access Servers" -> member dNSHostName.
        string? host = QueryRemoteAccessServerFqdn();

        // 3. Fallback: the connection broker FQDN from the local registry
        //    (in all-in-one deployments the broker is also the gateway).
        if (string.IsNullOrWhiteSpace(host))
            host = GetBrokerFqdnFromRegistry();

        if (!string.IsNullOrWhiteSpace(host))
            _cached = $"https://{host!.Trim().ToLowerInvariant()}/RDWeb/Pages/en-US/password.aspx";

        return _cached;
    }

    /// <summary>
    /// Reads the first resolvable member of the Builtin "RDS Remote Access
    /// Servers" group and returns its dNSHostName. Readable by any
    /// authenticated user; returns null if the group is missing or empty.
    /// </summary>
    private static string? QueryRemoteAccessServerFqdn()
    {
        try
        {
            using var rootDse = new DirectoryEntry("LDAP://RootDSE");
            string? nc = rootDse.Properties["defaultNamingContext"]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(nc)) return null;

            using var grp = new DirectoryEntry($"LDAP://CN=RDS Remote Access Servers,CN=Builtin,{nc}");
            var members = grp.Properties["member"];
            if (members == null || members.Count == 0) return null;

            foreach (object? m in members)
            {
                string dn = m?.ToString() ?? "";
                if (dn.Length == 0) continue;
                try
                {
                    using var member = new DirectoryEntry("LDAP://" + dn.Replace("/", "\\/"));
                    string? fqdn = member.Properties["dNSHostName"]?.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(fqdn)) return fqdn;
                }
                catch { /* try the next member */ }
            }
        }
        catch { /* group not present / AD unreadable */ }
        return null;
    }

    private static string? GetBrokerFqdnFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server\ClusterSettings");
            var v = key?.GetValue("SessionDirectoryLocation") as string;
            return string.IsNullOrWhiteSpace(v) ? null : v!.Trim();
        }
        catch { return null; }
    }
}
