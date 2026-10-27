using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.DirectoryServices.ActiveDirectory;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using UserDashboard.Models;

namespace UserDashboard.Services;

/// <summary>
/// Wraps every Active Directory operation the dashboard needs.
/// Replaces the Get-ADUser / Set-ADUser / New-ADUser / Add-ADGroupMember /
/// Set-ADAccountPassword / etc. calls scattered through UserDashboard.ps1.
///
/// All public methods throw on failure - the UI layer is responsible for
/// catching and presenting errors to the user.
/// </summary>
internal sealed class AdService : IDisposable
{
    private readonly PrincipalContext _ctx;

    /// <summary>UPN suffix, e.g. "@corp.example.com".</summary>
    public string UpnSuffix { get; }

    /// <summary>Pre-Windows 2000 / NetBIOS domain name, e.g. "CORP".</summary>
    public string NetBiosDomain { get; }

    /// <summary>Distinguished name of the default Users container.</summary>
    public string UsersContainerDn { get; }

    /// <summary>DNS name of the current domain, e.g. "corp.example.com".</summary>
    public string DnsRoot { get; }

    public AdService()
    {
        var domain = Domain.GetCurrentDomain();
        DnsRoot = domain.Name;

        _ctx = new PrincipalContext(ContextType.Domain, DnsRoot);

        // Pull the domain DN and NetBIOS name from RootDSE / the Partitions container.
        using var rootDse = new DirectoryEntry($"LDAP://{DnsRoot}/RootDSE");
        string defaultNc = (string)rootDse.Properties["defaultNamingContext"].Value!;
        UsersContainerDn = "CN=Users," + defaultNc;

        NetBiosDomain  = ResolveNetBiosName(rootDse, defaultNc) ?? DnsRoot.Split('.')[0].ToUpperInvariant();
        UpnSuffix      = "@" + DnsRoot;
    }

    private static string? ResolveNetBiosName(DirectoryEntry rootDse, string defaultNc)
    {
        try
        {
            string configNc = (string)rootDse.Properties["configurationNamingContext"].Value!;
            using var partitions = new DirectoryEntry($"LDAP://CN=Partitions,{configNc}");
            using var searcher = new DirectorySearcher(partitions)
            {
                Filter = $"(&(objectClass=crossRef)(nCName={defaultNc}))",
                SearchScope = SearchScope.OneLevel
            };
            searcher.PropertiesToLoad.Add("nETBIOSName");
            var result = searcher.FindOne();
            if (result?.Properties["nETBIOSName"].Count > 0)
                return result.Properties["nETBIOSName"][0]?.ToString();
        }
        catch { /* fall through to caller's fallback */ }
        return null;
    }

    public void Dispose() => _ctx.Dispose();

    // ====================================================================
    //  Loading
    // ====================================================================

    /// <summary>
    /// Load every user in the domain (subtree of defaultNamingContext).
    /// Mirrors Load-Users in the PowerShell file - including the same
    /// excluded-user filtering and the krbtgt regex strip.
    /// </summary>
    public List<AdUserInfo> LoadAllUsers()
    {
        var results = new List<AdUserInfo>();

        using var root = new DirectoryEntry($"LDAP://{DnsRoot}");
        using var searcher = new DirectorySearcher(root)
        {
            Filter = "(&(objectCategory=person)(objectClass=user))",
            SearchScope = SearchScope.Subtree,
            PageSize = 1000
        };
        foreach (var p in new[]
        {
            "displayName", "givenName", "sn", "initials", "middleName",
            "sAMAccountName", "userPrincipalName", "memberOf",
            "lastLogonTimestamp", "userAccountControl", "lockoutTime",
            "distinguishedName"
        })
        {
            searcher.PropertiesToLoad.Add(p);
        }

        foreach (SearchResult r in searcher.FindAll())
        {
            var info = MapResult(r);
            if (string.IsNullOrWhiteSpace(info.SamAccountName)) continue;
            if (Constants.HiddenSamAccountNames.Contains(info.SamAccountName)) continue;
            if (Constants.ExcludedUsers.Contains(info.SamAccountName))         continue;
            if (info.SamAccountName.Equals("krbtgt", StringComparison.OrdinalIgnoreCase)) continue;

            results.Add(info);
        }

        return results;
    }

    /// <summary>
    /// Replace each user's LastLogonDate (from the imprecise, ~14-day-stale
    /// lastLogonTimestamp) with the accurate per-DC lastLogon value.
    ///
    /// lastLogon is NON-replicated: each DC only knows the logons it
    /// authenticated, so the true last logon is the MAX across all DCs. We
    /// query every DC in parallel (short connect timeout, unreachable DCs
    /// skipped) and keep the newest value per user. Any user for whom no DC
    /// returned a live value keeps the lastLogonTimestamp fallback already set.
    ///
    /// Domain-agnostic: DomainController list comes from the current domain.
    /// Safe to call off the UI thread; never throws (best-effort enrichment).
    /// </summary>
    public void EnrichLastLogonAccurate(List<AdUserInfo> users)
    {
        if (users == null || users.Count == 0) return;

        // Index users by SAM for fast merge.
        var bySam = new Dictionary<string, AdUserInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in users)
            if (!string.IsNullOrWhiteSpace(u.SamAccountName)) bySam[u.SamAccountName] = u;

        // Enumerate DCs in the current domain.
        List<string> dcHosts = new();
        try
        {
            var domain = Domain.GetCurrentDomain();
            foreach (DomainController dc in domain.DomainControllers)
            {
                try { if (!string.IsNullOrWhiteSpace(dc.Name)) dcHosts.Add(dc.Name); }
                catch { }
            }
        }
        catch { return; }   // can't enumerate DCs - keep the fallback values
        if (dcHosts.Count == 0) return;

        // Per-DC newest lastLogon, merged under a lock.
        var newest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        object gate = new();

        try
        {
            Parallel.ForEach(
                dcHosts,
                new ParallelOptions { MaxDegreeOfParallelism = 8 },
                dc =>
                {
                    // Skip DCs that aren't answering LDAP quickly (dead/remote).
                    if (!IsTcpReachable(dc, 389, 1500)) return;

                    var local = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        using var root = new DirectoryEntry($"LDAP://{dc}");
                        using var searcher = new DirectorySearcher(root)
                        {
                            Filter      = "(&(objectCategory=person)(objectClass=user))",
                            SearchScope = SearchScope.Subtree,
                            PageSize    = 1000
                        };
                        searcher.PropertiesToLoad.Add("sAMAccountName");
                        searcher.PropertiesToLoad.Add("lastLogon");

                        using var results = searcher.FindAll();
                        foreach (SearchResult r in results)
                        {
                            string sam = r.Properties["sAMAccountName"].Count > 0
                                ? r.Properties["sAMAccountName"][0]?.ToString() ?? "" : "";
                            if (sam.Length == 0 || !bySam.ContainsKey(sam)) continue;

                            if (r.Properties["lastLogon"].Count > 0 &&
                                r.Properties["lastLogon"][0] is long ll && ll > 0)
                            {
                                if (!local.TryGetValue(sam, out long cur) || ll > cur)
                                    local[sam] = ll;
                            }
                        }
                    }
                    catch { return; }   // this DC failed - other DCs still contribute

                    lock (gate)
                    {
                        foreach (var kv in local)
                            if (!newest.TryGetValue(kv.Key, out long cur) || kv.Value > cur)
                                newest[kv.Key] = kv.Value;
                    }
                });
        }
        catch { /* fall through with whatever we gathered */ }

        // Merge: overwrite LastLogonDate where we found a real per-DC value.
        foreach (var kv in newest)
        {
            if (!bySam.TryGetValue(kv.Key, out var u)) continue;
            try { u.LastLogonDate = DateTime.FromFileTimeUtc(kv.Value).ToLocalTime(); }
            catch { /* keep fallback */ }
        }
    }

    /// <summary>Fast TCP reachability gate (used to skip dead DCs). Never throws.</summary>
    private static bool IsTcpReachable(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
            try { client.EndConnect(ar); return client.Connected; }
            catch { return false; }
        }
        catch { return false; }
    }

    /// <summary>Re-fetch a single user after an edit. Equivalent to Sync-UserState.</summary>
    public AdUserInfo GetUser(string samAccountName)
    {
        using var root = new DirectoryEntry($"LDAP://{DnsRoot}");
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={EscapeFilter(samAccountName)}))",
            SearchScope = SearchScope.Subtree
        };
        var r = searcher.FindOne()
            ?? throw new InvalidOperationException($"User '{samAccountName}' not found.");
        return MapResult(r);
    }

    private static AdUserInfo MapResult(SearchResult r)
    {
        string? Get(string p) =>
            r.Properties[p].Count > 0 ? r.Properties[p][0]?.ToString() : null;

        var info = new AdUserInfo
        {
            SamAccountName    = Get("sAMAccountName") ?? string.Empty,
            UserPrincipalName = Get("userPrincipalName"),
            DisplayName       = Get("displayName"),
            GivenName         = Get("givenName"),
            Surname           = Get("sn"),
            Initials          = Get("initials"),
            MiddleName        = Get("middleName"),
            DistinguishedName = Get("distinguishedName"),
        };

        // userAccountControl: bit 0x2 = ACCOUNTDISABLE
        if (r.Properties["userAccountControl"].Count > 0 &&
            r.Properties["userAccountControl"][0] is int uac)
        {
            info.Enabled = (uac & 0x2) == 0;
        }

        // lockoutTime: 0 (or absent) means not locked
        if (r.Properties["lockoutTime"].Count > 0 &&
            r.Properties["lockoutTime"][0] is long lockout && lockout > 0)
        {
            info.LockedOut = true;
        }

        // lastLogonTimestamp is a Windows FILETIME (replicated; ~14-day resolution)
        if (r.Properties["lastLogonTimestamp"].Count > 0 &&
            r.Properties["lastLogonTimestamp"][0] is long ft && ft > 0)
        {
            try { info.LastLogonDate = DateTime.FromFileTimeUtc(ft).ToLocalTime(); }
            catch { /* leave as null */ }
        }

        foreach (var dn in r.Properties["memberOf"])
        {
            if (dn is string s) info.MemberOf.Add(s);
        }

        return info;
    }

    // ====================================================================
    //  Editing user attributes
    // ====================================================================

    /// <summary>
    /// Set or clear a single text-valued LDAP attribute (givenName, sn, initials, ...).
    /// Mirrors Set-ADTextAttributeValue in the PowerShell file.
    /// </summary>
    public void SetUserAttribute(string samAccountName, string ldapName, string? value)
    {
        using var user = FindUserDirectoryEntry(samAccountName);
        try { user.RefreshCache(new[] { ldapName }); } catch { /* still try the property bag */ }
        string trimmed = (value ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            if (user.Properties[ldapName].Count == 0) return;
            user.Properties[ldapName].Clear();
        }
        else
        {
            user.Properties[ldapName].Value = trimmed;
        }
        user.CommitChanges();
    }

    /// <summary>Set DisplayName and rename the AD object's CN to match.</summary>
    public void SetDisplayName(string samAccountName, string displayName)
    {
        using var user = FindUserDirectoryEntry(samAccountName);
        string trimmed = (displayName ?? string.Empty).Trim();
        user.Properties["displayName"].Value = trimmed;
        user.CommitChanges();

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            // Rename the CN component so the AD object name matches the display name.
            user.Rename("CN=" + trimmed);
            user.CommitChanges();
        }
    }

    /// <summary>Rename the SAM account / UPN local part of an existing user.</summary>
    public void RenameUser(string oldSam, string newSam)
    {
        if (UserExists(newSam))
            throw new InvalidOperationException($"Username '{newSam}' already exists.");

        using var user = FindUserDirectoryEntry(oldSam);
        user.Properties["sAMAccountName"].Value   = newSam;
        user.Properties["userPrincipalName"].Value = newSam + UpnSuffix;
        user.CommitChanges();
    }

    public bool UserExists(string samAccountName)
    {
        try
        {
            using var p = UserPrincipal.FindByIdentity(_ctx, IdentityType.SamAccountName, samAccountName);
            return p != null;
        }
        catch { return false; }
    }

    // ====================================================================
    //  Account state (enable / disable / unlock / password)
    // ====================================================================

    public void EnableAccount(string samAccountName)  => ToggleEnabled(samAccountName, true);
    public void DisableAccount(string samAccountName) => ToggleEnabled(samAccountName, false);

    private void ToggleEnabled(string sam, bool enabled)
    {
        using var p = UserPrincipal.FindByIdentity(_ctx, IdentityType.SamAccountName, sam)
            ?? throw new InvalidOperationException($"User '{sam}' not found.");
        p.Enabled = enabled;
        p.Save();
    }

    public void ResetPassword(string samAccountName, string newPassword,
                              bool mustChangeAtNextLogon = false)
    {
        using var p = UserPrincipal.FindByIdentity(_ctx, IdentityType.SamAccountName, samAccountName)
            ?? throw new InvalidOperationException($"User '{samAccountName}' not found.");
        p.SetPassword(newPassword);

        // Best-effort unlock (matches Unlock-ADAccount call in the PS dialog)
        try { if (p.IsAccountLockedOut()) p.UnlockAccount(); } catch { }

        if (mustChangeAtNextLogon)
        {
            // Temp-password mode: the user must set their own password at next
            // logon (e.g. via the RD Web password page). "Never expires" must be
            // OFF or AD ignores the expiry flag.
            p.PasswordNeverExpires = false;
            p.Save();
            p.ExpirePasswordNow();
        }
        else
        {
            // Policy: permanent password set by the dashboard, flagged
            // "Password Never Expires". SetPassword itself clears any prior
            // "must change at next logon" state by updating pwdLastSet.
            p.PasswordNeverExpires = true;
            p.Save();
        }
    }

    /// <summary>Clear an account lockout (does not change the password).</summary>
    public void UnlockAccount(string samAccountName)
    {
        using var p = UserPrincipal.FindByIdentity(_ctx, IdentityType.SamAccountName, samAccountName)
            ?? throw new InvalidOperationException($"User '{samAccountName}' not found.");
        p.UnlockAccount();
        p.Save();
    }

    // ====================================================================
    //  Group membership
    // ====================================================================

    public List<string> GetAllSecurityGroupNamesUnderUsersContainer()
    {
        var names = new List<string>();

        using var usersContainer = new DirectoryEntry($"LDAP://{UsersContainerDn}");
        using var searcher = new DirectorySearcher(usersContainer)
        {
            Filter = "(objectClass=group)",
            SearchScope = SearchScope.OneLevel
        };
        searcher.PropertiesToLoad.Add("name");
        searcher.PropertiesToLoad.Add("groupType");

        foreach (SearchResult r in searcher.FindAll())
        {
            // groupType bit 0x80000000 = Security group (vs Distribution).
            // The attribute can come back as either int or long depending on the
            // COM marshaler, so coerce through long to avoid OverflowException.
            long groupType = 0;
            if (r.Properties["groupType"].Count > 0)
            {
                var raw = r.Properties["groupType"][0];
                if      (raw is int  i) groupType = i;
                else if (raw is long l) groupType = l;
                else long.TryParse(raw?.ToString(), out groupType);
            }
            bool isSecurity = (groupType & 0x80000000L) != 0;
            if (!isSecurity) continue;

            string? name = r.Properties["name"].Count > 0
                ? r.Properties["name"][0]?.ToString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (Constants.ExcludedGroups.Contains(name)) continue;

            names.Add(name);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    public void AddUserToGroup(string samAccountName, string groupName)
    {
        using var grp = GroupPrincipal.FindByIdentity(_ctx, groupName)
            ?? throw new InvalidOperationException($"Group '{groupName}' not found.");
        using var usr = UserPrincipal.FindByIdentity(_ctx, IdentityType.SamAccountName, samAccountName)
            ?? throw new InvalidOperationException($"User '{samAccountName}' not found.");

        if (!grp.Members.Contains(usr))
        {
            grp.Members.Add(usr);
            grp.Save();
        }
    }

    public void RemoveUserFromGroup(string samAccountName, string groupName)
    {
        using var grp = GroupPrincipal.FindByIdentity(_ctx, groupName)
            ?? throw new InvalidOperationException($"Group '{groupName}' not found.");
        using var usr = UserPrincipal.FindByIdentity(_ctx, IdentityType.SamAccountName, samAccountName)
            ?? throw new InvalidOperationException($"User '{samAccountName}' not found.");

        if (grp.Members.Contains(usr))
        {
            grp.Members.Remove(usr);
            grp.Save();
        }
    }

    public bool GroupExists(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName)) return false;
        try
        {
            using var g = GroupPrincipal.FindByIdentity(_ctx, IdentityType.Name, groupName);
            return g != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Create a global security group in the default Users container.
    /// Idempotent: if a group with this name already exists, this is a no-op.
    /// </summary>
    public void CreateSecurityGroup(string groupName, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new ArgumentException("Group name is required.", nameof(groupName));

        string name = groupName.Trim();
        if (GroupExists(name)) return;

        using var container = new DirectoryEntry($"LDAP://{UsersContainerDn}");
        using var grp = container.Children.Add("CN=" + name, "group");
        grp.Properties["sAMAccountName"].Value = name;
        // 0x80000002 = ADS_GROUP_TYPE_SECURITY_ENABLED | ADS_GROUP_TYPE_GLOBAL_GROUP
        grp.Properties["groupType"].Value = unchecked((int)0x80000002);
        if (!string.IsNullOrWhiteSpace(description))
            grp.Properties["description"].Value = description.Trim();
        grp.CommitChanges();
    }

    /// <summary>
    /// Read a single string-valued LDAP attribute. Returns null if missing.
    /// </summary>
    public string? GetUserStringAttribute(string samAccountName, string ldapName)
    {
        using var user = FindUserDirectoryEntry(samAccountName);
        try { user.RefreshCache(new[] { ldapName }); } catch { /* still try the property bag */ }
        var prop = user.Properties[ldapName];
        if (prop == null || prop.Count == 0) return null;
        string? value = prop[0]?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Read the user's default Global Shop company code. Tries the configured
    /// LDAP attribute, then the base-schema <c>comment</c> field, so domains
    /// without Exchange (no extensionAttribute*) still work.
    /// </summary>
    public string? GetCompanyCodePrimary(string samAccountName)
    {
        foreach (string ldap in CompanyCodePrimaryCandidates())
        {
            try
            {
                string? parsed = CompanyCodeHelper.ParsePrimaryValue(
                    GetUserStringAttribute(samAccountName, ldap), ldap);
                if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
            }
            catch (Exception ex) when (IsMissingLdapAttribute(ex))
            {
                // Attribute is not in this forest's schema - try the next one.
            }
            catch
            {
                // Unreadable is the same as unset for this candidate.
            }
        }
        return null;
    }

    /// <summary>
    /// Store (or clear) the user's default Global Shop company code. Falls back
    /// to <c>comment</c> when the configured attribute is not in the schema.
    /// </summary>
    public void SetCompanyCodePrimary(string samAccountName, string? code)
    {
        string? trimmed = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        Exception? lastMissing = null;

        foreach (string ldap in CompanyCodePrimaryCandidates())
        {
            try
            {
                if (CompanyCodeHelper.UsesEmbeddedMarker(ldap))
                {
                    string? existing = null;
                    try { existing = GetUserStringAttribute(samAccountName, ldap); }
                    catch (Exception ex) when (IsMissingLdapAttribute(ex)) { throw; }
                    catch { existing = null; }
                    SetUserAttribute(samAccountName, ldap,
                        CompanyCodeHelper.ApplyPrimaryMarker(existing, trimmed));
                }
                else
                {
                    SetUserAttribute(samAccountName, ldap, trimmed);
                }
                return;
            }
            catch (Exception ex) when (IsMissingLdapAttribute(ex))
            {
                lastMissing = ex;
            }
        }

        throw new InvalidOperationException(
            "Could not store the default company code. This domain does not have " +
            "the configured LDAP attribute, and the fallback comment field also failed.",
            lastMissing);
    }

    private static IEnumerable<string> CompanyCodePrimaryCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string configured = CompanyCodeHelper.PrimaryAttribute;
        if (!string.IsNullOrWhiteSpace(configured) && seen.Add(configured))
            yield return configured;
        if (seen.Add("comment"))
            yield return "comment";
    }

    /// <summary>
    /// True when ADSI/LDAP rejected the attribute as not in the schema
    /// (0x8007200A) or not present on the object (0x8000500D).
    /// </summary>
    private static bool IsMissingLdapAttribute(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is DirectoryServicesCOMException com)
            {
                uint hr = unchecked((uint)com.ErrorCode);
                if (hr == 0x8007200A || hr == 0x8000500D) return true;
            }
            if (e.Message.IndexOf("attribute or value does not exist",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (e.Message.IndexOf("property is not found",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    // ====================================================================
    //  Creating users
    // ====================================================================

    public sealed class CreateUserRequest
    {
        public string SamAccountName    { get; set; } = string.Empty;
        public string UserPrincipalName { get; set; } = string.Empty; // full UPN incl. suffix
        public string DisplayName       { get; set; } = string.Empty;
        public string Password          { get; set; } = string.Empty;
        public string? GivenName        { get; set; }
        public string? Surname          { get; set; }
        public string? Initials         { get; set; }
        public string  Path             { get; set; } = string.Empty; // OU/container DN
        public bool PasswordNeverExpires { get; set; } = false;

        /// <summary>
        /// FedRAMP / NIST IA-5: the initial password is temporary. The user
        /// must set a unique password only they know (RD Web password page)
        /// before the account can be used normally.
        /// </summary>
        public bool MustChangePasswordAtNextLogon { get; set; } = true;
    }

    public void CreateUser(CreateUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SamAccountName))
            throw new ArgumentException("SAM account name is required.", nameof(req));
        if (UserExists(req.SamAccountName))
            throw new InvalidOperationException($"Username '{req.SamAccountName}' already exists.");

        // Bind to the target container (fall back to default Users container).
        string containerDn = string.IsNullOrWhiteSpace(req.Path) ? UsersContainerDn : req.Path;
        using var container = new DirectoryEntry($"LDAP://{containerDn}");
        using var newUser = container.Children.Add("CN=" + req.DisplayName, "user");

        newUser.Properties["sAMAccountName"].Value     = req.SamAccountName;
        newUser.Properties["userPrincipalName"].Value  = req.UserPrincipalName;
        newUser.Properties["displayName"].Value        = req.DisplayName;

        if (!string.IsNullOrWhiteSpace(req.GivenName))
            newUser.Properties["givenName"].Value = req.GivenName;
        if (!string.IsNullOrWhiteSpace(req.Surname))
            newUser.Properties["sn"].Value = req.Surname;
        if (!string.IsNullOrWhiteSpace(req.Initials))
            newUser.Properties["initials"].Value = req.Initials;

        newUser.CommitChanges();

        // Set password
        newUser.Invoke("SetPassword", new object[] { req.Password });
        newUser.CommitChanges();

        // Enable account: clear ACCOUNTDISABLE (0x2). Never-expire is incompatible
        // with "must change at next logon" (AD ignores pwdLastSet=0 when 0x10000 is set).
        int uac = 0x200; // NORMAL_ACCOUNT
        if (req.PasswordNeverExpires && !req.MustChangePasswordAtNextLogon)
            uac |= 0x10000;
        newUser.Properties["userAccountControl"].Value = uac;
        newUser.CommitChanges();

        if (req.MustChangePasswordAtNextLogon)
        {
            using var p = UserPrincipal.FindByIdentity(_ctx, IdentityType.SamAccountName, req.SamAccountName);
            if (p != null)
            {
                p.PasswordNeverExpires = false;
                p.Save();
                p.ExpirePasswordNow();
            }
        }
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    private DirectoryEntry FindUserDirectoryEntry(string samAccountName)
    {
        using var root = new DirectoryEntry($"LDAP://{DnsRoot}");
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={EscapeFilter(samAccountName)}))",
            SearchScope = SearchScope.Subtree
        };
        searcher.PropertiesToLoad.Add("distinguishedName");
        var r = searcher.FindOne()
            ?? throw new InvalidOperationException($"User '{samAccountName}' not found.");

        // SearchResult.GetDirectoryEntry() returns a writable, bindable entry.
        return r.GetDirectoryEntry();
    }

    /// <summary>Escape a value to be safely embedded in an LDAP search filter.</summary>
    public static string EscapeFilter(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*':  sb.Append("\\2a"); break;
                case '(':  sb.Append("\\28"); break;
                case ')':  sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default:   sb.Append(c);      break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Normalize a DN (CN=Foo,OU=...) or bare CN to its lowercased CN label.</summary>
    public static string NormalizeGroupName(string dnOrCn)
    {
        if (string.IsNullOrWhiteSpace(dnOrCn)) return string.Empty;
        string first = dnOrCn.Split(',')[0];
        if (first.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            first = first.Substring(3);
        return first.Trim().ToLowerInvariant();
    }

    /// <summary>Extract the bare CN from a memberOf DN.</summary>
    public static string ExtractCn(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName)) return string.Empty;
        string first = distinguishedName.Split(',')[0];
        if (first.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            first = first.Substring(3);
        return first.Trim();
    }
}
