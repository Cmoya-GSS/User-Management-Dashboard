using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace UserDashboard.Services;

/// <summary>
/// Thrown when an RDS operation fails because the caller lacks the rights to
/// query/log off a session on a host. Lets the UI show a clear "permissions
/// required" message instead of a raw Win32 error.
/// </summary>
internal sealed class RdsAccessDeniedException : Exception
{
    public RdsAccessDeniedException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>One RDS user session on a specific session host.</summary>
internal sealed class RdsSession
{
    public string UserName         { get; set; } = "";   // SAM (no domain)
    public string DomainName       { get; set; } = "";
    public string HostServer       { get; set; } = "";   // session host FQDN
    public int    UnifiedSessionId { get; set; }         // WTS session id on that host
    public string SessionState     { get; set; } = "";   // "Active" / "Disconnected" / ...
    public string CollectionName   { get; set; } = "";   // not used in WTS path
    public TimeSpan? IdleTime      { get; set; }

    public string QualifiedUser =>
        string.IsNullOrEmpty(DomainName) ? UserName : $"{DomainName}\\{UserName}";

    /// <summary>
    /// Ticks stored in the Idle grid cell so the column sorts as a duration.
    /// -1 means unknown / blank.
    /// </summary>
    public long IdleSortTicks => IdleTime?.Ticks ?? -1L;

    public static string FormatIdle(TimeSpan? idle)
    {
        if (!idle.HasValue) return "";
        var t = idle.Value;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes}m"
            : $"{t.Minutes}m";
    }

    public static string FormatIdleCell(object? value)
    {
        long ticks = IdleTicksFromCell(value);
        return ticks < 0 ? "" : FormatIdle(new TimeSpan(ticks));
    }

    /// <summary>
    /// Compare Idle Time cells as durations. Handles numeric ticks, TimeSpan,
    /// and display text ("6h 31m" vs "9m") so the grid cannot fall back to
    /// lexicographic sort.
    /// </summary>
    public static int CompareIdleCells(object? cell1, object? cell2, RdsSession? row1, RdsSession? row2)
    {
        long a = IdleTicksFromCell(cell1);
        long b = IdleTicksFromCell(cell2);
        if (a < 0 && row1?.IdleTime != null) a = row1.IdleTime.Value.Ticks;
        if (b < 0 && row2?.IdleTime != null) b = row2.IdleTime.Value.Ticks;
        return a.CompareTo(b);
    }

    public static long IdleTicksFromCell(object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return -1;
            case TimeSpan ts:
                return ts.Ticks;
            case long l:
                return l;
            case int i:
                return i < 0 ? -1 : TimeSpan.FromMinutes(i).Ticks;
            case double d:
                return d < 0 ? -1 : TimeSpan.FromMinutes(d).Ticks;
            case float f:
                return f < 0 ? -1 : TimeSpan.FromMinutes(f).Ticks;
            case decimal m:
                return m < 0 ? -1 : TimeSpan.FromMinutes((double)m).Ticks;
            default:
                return TimeSpan.FromMinutes(ParseIdleMinutes(Convert.ToString(value) ?? "")).Ticks;
        }
    }

    /// <summary>
    /// Parse "6h 31m", "9m", "2h 33m" into minutes. Bare numbers are treated as
    /// minutes unless they are large enough to be TimeSpan ticks. Empty -> -1.
    /// </summary>
    public static double ParseIdleMinutes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        string s = text.Trim();
        double minutes = 0;
        bool any = false;
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            if (i >= s.Length) break;
            int start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (!int.TryParse(s.Substring(start, i - start), out int n)) break;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            char unit = 'm';
            if (i < s.Length)
            {
                char u = char.ToLowerInvariant(s[i]);
                if (u is 'd' or 'h' or 'm' or 's')
                {
                    unit = u;
                    i++;
                }
            }
            any = true;
            switch (unit)
            {
                case 'd': minutes += n * 1440; break;
                case 'h': minutes += n * 60; break;
                case 'm': minutes += n; break;
                case 's': minutes += n / 60.0; break;
            }
        }
        if (any) return minutes;

        if (long.TryParse(s, out long raw))
        {
            // 1 minute in ticks is 600,000,000. A displayed minute count never
            // reaches that, so large values are raw ticks (unformatted cells).
            if (Math.Abs(raw) >= TimeSpan.TicksPerMinute)
                return TimeSpan.FromTicks(raw).TotalMinutes;
            return raw;
        }
        return -1;
    }

    /// <summary>Already friendly in the WTS path; pass through, title-cased.</summary>
    public string FriendlyState
    {
        get
        {
            string s = SessionState ?? "";
            if (s.StartsWith("STATE_", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6);
            if (s.Length == 0) return "Unknown";
            return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
        }
    }
}

internal sealed class LogoffResult
{
    public RdsSession Session { get; set; } = null!;
    public bool       Success { get; set; }
    public string?    Error   { get; set; }
}

/// <summary>
/// Queries and logs off RDS sessions by talking DIRECTLY to each session host's
/// Windows Terminal Services (WTS) API - NOT through the RD Connection Broker's
/// management cmdlets.
///
/// Why: the broker cmdlets (Get-RDUserSession / Invoke-RDUserLogoff) require
/// administrative rights on the broker. The native WTS API instead checks the
/// session host's RDP-Tcp listener permissions (WINSTATION_QUERY 0x1 +
/// WINSTATION_RESET 0x4 + WINSTATION_LOGOFF 0x20). Reset (0x4) is what allows
/// ending ANOTHER user's session; Query+Logoff alone only covers seeing sessions
/// and logging off your own. That listener permission can be delegated to a
/// non-administrative group (mask 0x25), so operators can log off sessions with
/// no local admin and no broker admin - which is the whole point.
///
/// Host discovery: tries Get-RDServer via the broker (a topology read); if that
/// is not permitted, falls back to a configured list (Constants.RdsSessionHosts)
/// plus the local machine.
/// </summary>
internal sealed class RdsService
{
    // ===================== WTS P/Invoke =====================
    private const int WTS_CURRENT_SERVER_HANDLE = 0;
    private const int ERROR_ACCESS_DENIED = 5;

    private enum WTS_CONNECTSTATE_CLASS
    {
        Active, Connected, ConnectQuery, Shadow, Disconnected,
        Idle, Listen, Reset, Down, Init
    }

    private enum WTS_INFO_CLASS
    {
        WTSUserName    = 5,
        WTSDomainName  = 7,
        WTSConnectState = 8,
        WTSSessionInfo = 24
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionID;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    // Subset of WTSINFOW - we only read the time fields for idle calculation.
    // Char arrays are sized per the Windows headers so the long fields land at
    // the correct offsets. Idle is treated as best-effort regardless.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTSINFO
    {
        public WTS_CONNECTSTATE_CLASS State;
        public int SessionId;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr WTSOpenServer(string pServerName);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern void WTSCloseServer(IntPtr hServer);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, int SessionId, bool bWait);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    // ===================== state =====================
    private string? _brokerFqdn;
    private bool _brokerResolved;
    private List<string>? _hostCache;
    private bool? _available;

    // ===================== availability =====================
    /// <summary>
    /// True if the WTS API can enumerate sessions on this machine. wtsapi32 is
    /// present on all Windows, so this is effectively "are we on a session host".
    /// </summary>
    public bool IsAvailable()
    {
        if (_available.HasValue) return _available.Value;
        try
        {
            IntPtr h = (IntPtr)WTS_CURRENT_SERVER_HANDLE;
            bool ok = WTSEnumerateSessions(h, 0, 1, out IntPtr p, out int _);
            if (p != IntPtr.Zero) WTSFreeMemory(p);
            _available = ok;
        }
        catch { _available = false; }
        return _available.Value;
    }

    // ===================== broker (for host discovery only) =====================
    /// <summary>
    /// Resolve the connection broker FQDN from the local registry (works from any
    /// deployment member). Used only to enumerate session hosts via Get-RDServer.
    /// </summary>
    public string? GetBrokerFqdn()
    {
        if (_brokerResolved) return _brokerFqdn;
        _brokerResolved = true;
        try
        {
            var probes = new (string Path, string Value)[]
            {
                (@"SYSTEM\CurrentControlSet\Control\Terminal Server\ClusterSettings", "SessionDirectoryLocation"),
                (@"SYSTEM\CurrentControlSet\Control\Terminal Server\ClusterSettings", "ConnectionBroker"),
            };
            foreach (var (path, value) in probes)
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                var v = key?.GetValue(value) as string;
                if (!string.IsNullOrWhiteSpace(v)) { _brokerFqdn = v!.Trim(); return _brokerFqdn; }
            }
        }
        catch { }
        _brokerFqdn = null;
        return null;
    }

    // ===================== host discovery =====================
    /// <summary>
    /// Determine the session hosts to query. Discovery is domain-agnostic and
    /// works for non-admin operators:
    ///   1. Query Active Directory for computers advertising a TERMSRV SPN,
    ///      excluding domain controllers. Any authenticated user can read this,
    ///      it works in any domain (resolved via RootDSE), and newly-joined
    ///      session hosts appear automatically.
    ///   2. WTS-probe each candidate and keep only those that actually return
    ///      session data - this drops any stray RDP-enabled member server that
    ///      isn't a session host, and skips hosts that are offline/unreachable.
    ///   3. Always include any explicitly-configured hosts (Constants.RdsSessionHosts)
    ///      and the local machine, so the feature still works if AD is unreadable.
    /// Cached for the session.
    /// </summary>
    public List<string> GetSessionHosts()
    {
        if (_hostCache != null) return _hostCache;

        var confirmed = new List<string>();   // hosts that passed the WTS probe
        var candidates = new List<string>();

        // 1. AD candidates: TERMSRV SPN, not a domain controller.
        try
        {
            foreach (var h in QueryAdSessionHostCandidates())
                AddUnique(candidates, h);
        }
        catch { /* AD not readable - rely on configured list + local below */ }

        // 1b. Configured hosts (optional) are candidates too.
        try
        {
            foreach (var h in AppConfig.RdsSessionHosts)
                if (!string.IsNullOrWhiteSpace(h)) AddUnique(candidates, h.Trim());
        }
        catch { }

        // 1c. Local machine is always a candidate.
        string localFqdn;
        try { localFqdn = System.Net.Dns.GetHostEntry("").HostName; }
        catch { localFqdn = Environment.MachineName; }
        if (!string.IsNullOrWhiteSpace(localFqdn)) AddUnique(candidates, localFqdn);

        // 2. Probe candidates for real session hosts. The AD candidate list can
        //    include stale/powered-off computer objects (any server that ever had
        //    a TERMSRV SPN), and a blocking WTSOpenServer against a dead host can
        //    hang 20-60s EACH - doing this serially was the cause of multi-minute
        //    loads. So: keep the local machine for free, then probe the remote
        //    candidates IN PARALLEL, each gated by a fast TCP reachability check
        //    (RDP port 3389, short timeout) before the expensive WTS call.
        var remote = new List<string>();
        foreach (var host in candidates)
        {
            bool isLocal = string.Equals(host, localFqdn, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            if (isLocal) AddUnique(confirmed, host);
            else         remote.Add(host);
        }

        var reachable = new System.Collections.Concurrent.ConcurrentBag<string>();
        try
        {
            Parallel.ForEach(
                remote,
                new ParallelOptions { MaxDegreeOfParallelism = 16 },
                host =>
                {
                    if (IsRdpReachable(host, 1500) && ProbeIsSessionHost(host))
                        reachable.Add(host);
                });
        }
        catch
        {
            // If parallelism itself faults, fall back to a bounded serial pass.
            foreach (var host in remote)
                if (IsRdpReachable(host, 1500) && ProbeIsSessionHost(host))
                    reachable.Add(host);
        }
        foreach (var h in reachable) AddUnique(confirmed, h);

        // Safety: if probing produced nothing (e.g. all remote probes blocked),
        // fall back to the raw candidate list so the feature still attempts to work.
        _hostCache = confirmed.Count > 0 ? confirmed : candidates;
        return _hostCache;
    }

    /// <summary>
    /// AD query: computers with a TERMSRV SPN that are NOT domain controllers.
    /// Domain-agnostic (RootDSE), readable by any authenticated user.
    /// </summary>
    private static List<string> QueryAdSessionHostCandidates()
    {
        var list = new List<string>();
        using (var rootDse = new System.DirectoryServices.DirectoryEntry("LDAP://RootDSE"))
        {
            string? namingContext = rootDse.Properties["defaultNamingContext"]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(namingContext)) return list;

            using var root = new System.DirectoryServices.DirectoryEntry("LDAP://" + namingContext);
            using var searcher = new System.DirectoryServices.DirectorySearcher(root)
            {
                // TERMSRV SPN AND not a domain controller (UAC bit 0x2000 = 8192).
                Filter   = "(&(objectCategory=computer)(servicePrincipalName=TERMSRV/*)" +
                           "(!(userAccountControl:1.2.840.113556.1.4.803:=8192)))",
                PageSize = 200
            };
            searcher.PropertiesToLoad.Add("dnshostname");

            using var results = searcher.FindAll();
            foreach (System.DirectoryServices.SearchResult r in results)
            {
                if (r.Properties.Contains("dnshostname") && r.Properties["dnshostname"].Count > 0)
                {
                    string dns = r.Properties["dnshostname"][0]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(dns)) list.Add(dns);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Per-host health check for the About/diagnostics display. For every
    /// discovered session host, reports whether this account can enumerate
    /// sessions there (i.e. whether the delegated listener permission works).
    /// Never throws.
    /// </summary>
    public List<string> GetHostDiagnostics()
    {
        var lines = new List<string>();
        foreach (var host in GetSessionHosts())
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = WTSOpenServer(host);
                if (h == IntPtr.Zero) { lines.Add($"{host}: UNREACHABLE"); continue; }

                if (WTSEnumerateSessions(h, 0, 1, out IntPtr p, out int count))
                {
                    if (p != IntPtr.Zero) WTSFreeMemory(p);
                    lines.Add($"{host}: OK ({count} sessions visible)");
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    lines.Add(err == ERROR_ACCESS_DENIED
                        ? $"{host}: ACCESS DENIED (listener Query permission missing?)"
                        : $"{host}: query failed (Win32 error {err})");
                }
            }
            catch (Exception ex) { lines.Add($"{host}: error - {ex.Message}"); }
            finally { if (h != IntPtr.Zero) WTSCloseServer(h); }
        }
        return lines;
    }

    /// <summary>
    /// Fast reachability gate: attempt a TCP connect to the RDP port with a
    /// short timeout. A powered-off / decommissioned host fails in ~timeout ms
    /// instead of blocking WTSOpenServer for 20-60s. Never throws.
    /// </summary>
    private static bool IsRdpReachable(string host, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var ar = client.BeginConnect(host, 3389, null, null);
            bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs);
            if (!ok) return false;
            try { client.EndConnect(ar); return client.Connected; }
            catch { return false; }
        }
        catch { return false; }
    }

    /// <summary>
    /// Lightweight check: can we open the host's WTS and enumerate sessions?
    /// Returns true for a reachable session host. Never throws.
    /// </summary>
    private static bool ProbeIsSessionHost(string host)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = WTSOpenServer(host);
            if (h == IntPtr.Zero) return false;
            if (WTSEnumerateSessions(h, 0, 1, out IntPtr p, out int _))
            {
                if (p != IntPtr.Zero) WTSFreeMemory(p);
                return true;
            }
            return false;
        }
        catch { return false; }
        finally { if (h != IntPtr.Zero) WTSCloseServer(h); }
    }

    private static void AddUnique(List<string> list, string item)
    {
        if (!list.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase)))
            list.Add(item);
    }

    // ===================== enumerate =====================
    public List<RdsSession> GetSessionsForUsers(IEnumerable<string> samAccountNames)
    {
        var wanted = new HashSet<string>(samAccountNames, StringComparer.OrdinalIgnoreCase);
        return GetAllSessions().Where(s => wanted.Contains(s.UserName)).ToList();
    }

    /// <summary>
    /// Enumerate user sessions across all known session hosts via direct WTS.
    /// If every host fails with access-denied, throws RdsAccessDeniedException so
    /// the UI can show the permissions message.
    /// </summary>
    public List<RdsSession> GetAllSessions()
    {
        var sessions = new List<RdsSession>();
        var hosts = GetSessionHosts();

        int hostsTried = 0, hostsAccessDenied = 0;

        foreach (var host in hosts)
        {
            hostsTried++;
            IntPtr h = IntPtr.Zero;
            try
            {
                // Skip a fast reachability gate for the local machine; for remote
                // hosts, bail quickly if RDP isn't answering rather than letting
                // WTSOpenServer block for tens of seconds on a dropped host.
                bool isLocalHost =
                    string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
                    host.StartsWith(Environment.MachineName + ".", StringComparison.OrdinalIgnoreCase);
                if (!isLocalHost && !IsRdpReachable(host, 1500))
                    continue;

                h = WTSOpenServer(host);
                if (h == IntPtr.Zero)
                {
                    // Couldn't open - treat as unreachable, skip (not necessarily access denied).
                    continue;
                }

                if (!WTSEnumerateSessions(h, 0, 1, out IntPtr pSessions, out int count))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_ACCESS_DENIED) hostsAccessDenied++;
                    continue;
                }

                try
                {
                    int dataSize = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                    IntPtr cur = pSessions;
                    for (int i = 0; i < count; i++)
                    {
                        var si = (WTS_SESSION_INFO)Marshal.PtrToStructure(cur, typeof(WTS_SESSION_INFO));
                        cur = IntPtr.Add(cur, dataSize);

                        string user   = QueryString(h, si.SessionID, WTS_INFO_CLASS.WTSUserName);
                        if (string.IsNullOrWhiteSpace(user)) continue;   // skip system/listener sessions
                        string domain = QueryString(h, si.SessionID, WTS_INFO_CLASS.WTSDomainName);

                        sessions.Add(new RdsSession
                        {
                            UserName         = user,
                            DomainName       = domain,
                            HostServer       = host,
                            UnifiedSessionId = si.SessionID,
                            SessionState     = MapState(si.State),
                            IdleTime         = QueryIdle(h, si.SessionID)
                        });
                    }
                }
                finally
                {
                    WTSFreeMemory(pSessions);
                }
            }
            catch
            {
                // Skip a host we couldn't process; keep going.
            }
            finally
            {
                if (h != IntPtr.Zero) WTSCloseServer(h);
            }
        }

        // If we had hosts and every one we could open returned access-denied, surface it.
        if (hostsTried > 0 && hostsAccessDenied > 0 && sessions.Count == 0 &&
            hostsAccessDenied >= hostsTried)
        {
            throw new RdsAccessDeniedException(
                "Access denied querying RDS sessions on the session host(s). The account " +
                "needs the Query permission on the RDP-Tcp listener.");
        }

        return sessions;
    }

    // ===================== logoff =====================
    /// <summary>
    /// True if this account is on the protected list and must never be logged
    /// off from the dashboard. Compares the bare SAM name of the session user.
    /// </summary>
    public static bool IsProtectedAccount(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return false;
        return AppConfig.ProtectedLogoffAccounts.Contains(userName!.Trim());
    }

    public LogoffResult LogoffSession(RdsSession session)
    {
        var result = new LogoffResult { Session = session };

        // FINAL BACKSTOP - every logoff path in the app funnels through here,
        // so protected accounts are safe even if a UI check is missed.
        if (IsProtectedAccount(session.UserName))
        {
            result.Success = false;
            result.Error   = $"'{session.QualifiedUser}' is a protected account - " +
                             "logoff from the dashboard is blocked by policy.";
            return result;
        }
        IntPtr h = IntPtr.Zero;
        try
        {
            h = WTSOpenServer(session.HostServer);
            if (h == IntPtr.Zero)
            {
                result.Success = false;
                result.Error   = $"Could not open session host '{session.HostServer}'.";
                return result;
            }

            if (WTSLogoffSession(h, session.UnifiedSessionId, true))
            {
                result.Success = true;
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                result.Success = false;
                result.Error   = err == ERROR_ACCESS_DENIED
                    ? "Access denied - need Reset+Logoff on the RDP-Tcp listener (Reset is required to end another user's session)."
                    : $"WTSLogoffSession failed (Win32 error {err}).";
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error   = ex.Message;
        }
        finally
        {
            if (h != IntPtr.Zero) WTSCloseServer(h);
        }
        return result;
    }

    // ===================== helpers =====================
    private static string QueryString(IntPtr h, int sessionId, WTS_INFO_CLASS info)
    {
        if (WTSQuerySessionInformation(h, sessionId, info, out IntPtr buf, out int _))
        {
            try { return Marshal.PtrToStringUni(buf) ?? ""; }
            finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
        }
        return "";
    }

    private static TimeSpan? QueryIdle(IntPtr h, int sessionId)
    {
        // Best-effort. WTSINFO gives LastInputTime and CurrentTime as FILETIME ticks.
        // Heavily guarded: any odd value yields null ("unknown") rather than garbage.
        try
        {
            if (!WTSQuerySessionInformation(h, sessionId, WTS_INFO_CLASS.WTSSessionInfo,
                                            out IntPtr buf, out int bytes))
                return null;
            try
            {
                if (buf == IntPtr.Zero || bytes < Marshal.SizeOf(typeof(WTSINFO))) return null;
                var info = (WTSINFO)Marshal.PtrToStructure(buf, typeof(WTSINFO));
                if (info.LastInputTime <= 0 || info.CurrentTime <= 0) return null;
                long diff = info.CurrentTime - info.LastInputTime;
                if (diff < 0) return null;
                var ts = TimeSpan.FromTicks(diff);
                if (ts.TotalDays > 365) return null;   // sanity guard
                return ts;
            }
            finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
        }
        catch { return null; }
    }

    private static string MapState(WTS_CONNECTSTATE_CLASS s)
    {
        switch (s)
        {
            case WTS_CONNECTSTATE_CLASS.Active:       return "Active";
            case WTS_CONNECTSTATE_CLASS.Connected:    return "Connected";
            case WTS_CONNECTSTATE_CLASS.ConnectQuery: return "ConnectQuery";
            case WTS_CONNECTSTATE_CLASS.Shadow:       return "Shadow";
            case WTS_CONNECTSTATE_CLASS.Disconnected: return "Disconnected";
            case WTS_CONNECTSTATE_CLASS.Idle:         return "Idle";
            case WTS_CONNECTSTATE_CLASS.Listen:       return "Listen";
            case WTS_CONNECTSTATE_CLASS.Reset:        return "Reset";
            case WTS_CONNECTSTATE_CLASS.Down:         return "Down";
            case WTS_CONNECTSTATE_CLASS.Init:         return "Init";
            default:                                  return "Unknown";
        }
    }
}
