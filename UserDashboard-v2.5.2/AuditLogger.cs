using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace UserDashboard.Services;

/// <summary>
/// CSV audit logger. Writes one file per day:
/// <c>%PROGRAMDATA%\GSS\UserDashboard\Log\audit_YYYY-MM-DD.csv</c> (default) with
/// fallback to <c>%LOCALAPPDATA%</c> if ProgramData is not writable.
///
/// Design goals (and where they map to NIST 800-53 / 800-171):
///   * AU-2  - logs every mutating operation taken via the dashboard
///   * AU-3  - timestamp, operator, machine, action, target, details
///   * AU-5  - failures are surfaced via <see cref="WriteFailed"/> instead of swallowed
///   * AU-12 - centralized through this single component, no other write paths
///
/// This class is thread-safe (process-wide lock around file I/O).
/// </summary>
public static class AuditLogger
{
    private static readonly object _lock = new();

    private static readonly string[] Header =
        { "Timestamp", "Operator", "Machine", "Action", "Target", "Details" };

    private static string? _resolvedDir;
    private static int _failureCount;
    private static int _successCount;
    private static bool _initialized;
    private static string? _lastFailureMessage;

    /// <summary>The directory the logger has resolved to write into. Null until Initialize() runs.</summary>
    public static string? CurrentDirectory => _resolvedDir;

    /// <summary>The full path of today's audit file. Null until a directory is resolved.</summary>
    public static string? CurrentFilePath =>
        _resolvedDir == null
            ? null
            : Path.Combine(_resolvedDir, $"audit_{DateTime.Now:yyyy-MM-dd}.csv");

    /// <summary>Number of failed write attempts since startup. > 0 means audit log is broken.</summary>
    public static int FailureCount => _failureCount;

    /// <summary>Number of successful write attempts since startup.</summary>
    public static int SuccessCount => _successCount;

    /// <summary>Last error message produced by a failed write, if any.</summary>
    public static string? LastFailureMessage => _lastFailureMessage;

    /// <summary>True if a writable audit directory has been successfully resolved.</summary>
    public static bool IsHealthy => _resolvedDir != null && _failureCount == 0;

    /// <summary>
    /// Raised when a write fails. Subscribed by the UI to show a one-time warning.
    /// Argument is a human-readable description of the failure.
    /// </summary>
    public static event Action<string>? WriteFailed;

    // ---- Windows Event Log dual sink (FedRAMP AU-9 friendly) ----------------
    // CSV files can be modified by anyone with file access; the Application
    // event log is append-only for non-admins and feeds standard log
    // collection. Source creation needs admin ONCE (done by the setup script
    // via New-EventLog, or automatically here if the app ever runs elevated);
    // if the source doesn't exist we skip silently and CSV remains the record.
    private const string EventLogSource = "GSS-UserDashboard";
    private static bool? _eventLogUsable;

    /// <summary>True if audit entries are also being written to the Windows Event Log.</summary>
    public static bool EventLogActive => _eventLogUsable == true;

    private static void TryWriteEventLog(string action, string target, string details)
    {
        try
        {
            if (_eventLogUsable == null)
            {
                try
                {
                    if (System.Diagnostics.EventLog.SourceExists(EventLogSource))
                        _eventLogUsable = true;
                    else
                    {
                        // Works only if running elevated; harmless if it throws.
                        System.Diagnostics.EventLog.CreateEventSource(EventLogSource, "Application");
                        _eventLogUsable = System.Diagnostics.EventLog.SourceExists(EventLogSource);
                    }
                }
                catch { _eventLogUsable = false; }
            }
            if (_eventLogUsable != true) return;

            System.Diagnostics.EventLog.WriteEntry(EventLogSource,
                $"Action: {action}\r\nTarget: {target}\r\nOperator: {SafeGetUser()}\r\nDetails: {details}",
                System.Diagnostics.EventLogEntryType.Information);
        }
        catch { /* event log problems must never break the dashboard or the CSV sink */ }
    }

    /// <summary>
    /// Resolve a writable audit directory. Tries (in order):
    ///   1. <see cref="Constants.AuditDir"/> (default: %PROGRAMDATA%\GSS\UserDashboard\Log)
    ///   2. <see cref="Constants.AuditDirFallback"/> (per-user %LOCALAPPDATA%)
    /// Once a directory passes a write probe, records the path and writes a startup entry.
    ///
    /// Idempotent: safe to call multiple times. Subsequent calls are no-ops.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        var candidates = new List<string>
        {
            AppConfig.AuditDir,          // config-file override, else Constants.AuditDir
            Constants.AuditDirFallback,
        };

        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                Directory.CreateDirectory(dir);

                // Probe write: create and delete a small file. If this works, the
                // directory is genuinely writable by us with our current ACLs.
                string probe = Path.Combine(dir, $".probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);

                _resolvedDir = dir;
                break;
            }
            catch (Exception ex)
            {
                _lastFailureMessage = $"Couldn't use '{dir}': {ex.Message}";
            }
        }

        if (_resolvedDir == null)
        {
            _failureCount++;
            string msg = "No writable audit directory found. " +
                         $"Tried: {string.Join("; ", candidates)}.\r\n" +
                         (_lastFailureMessage ?? "");
            WriteFailed?.Invoke(msg);
            return;
        }

        // Startup entry proves logging works and gives ops a heartbeat to look for.
        WriteInternal("ApplicationStarted",
                      Environment.MachineName,
                      $"UserDashboard launched - audit dir: {_resolvedDir}");
    }

    /// <summary>
    /// Append a single audit row. Auto-initializes on first call.
    /// Failures are surfaced via <see cref="WriteFailed"/>; this method never throws.
    /// </summary>
    public static void Write(string action, string target, string details)
    {
        if (!_initialized) Initialize();
        WriteInternal(action, target, details);
    }

    private static void WriteInternal(string action, string target, string details)
    {
        if (_resolvedDir == null)
        {
            _failureCount++;
            _lastFailureMessage = "Audit directory not resolved";
            WriteFailed?.Invoke(_lastFailureMessage);
            return;
        }

        try
        {
            string operator_ = SafeGetUser();
            string machine   = SafeGetMachine();
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                                                    CultureInfo.InvariantCulture);
            string date      = DateTime.Now.ToString("yyyy-MM-dd",
                                                    CultureInfo.InvariantCulture);
            string path = Path.Combine(_resolvedDir, $"audit_{date}.csv");

            var row = new[] { timestamp, operator_, machine, action, target, details };

            lock (_lock)
            {
                bool isNew = !File.Exists(path);
                // FileShare.Read so a SIEM agent can tail the file while we're writing.
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                if (isNew)
                {
                    sw.WriteLine(JoinCsv(Header));
                }
                sw.WriteLine(JoinCsv(row));
                sw.Flush();
            }

            _successCount++;
        }
        catch (Exception ex)
        {
            _failureCount++;
            _lastFailureMessage = $"Audit write failed: {ex.Message}";
            WriteFailed?.Invoke(_lastFailureMessage);
        }

        // Second sink - independent of CSV success/failure.
        TryWriteEventLog(action, target, details);
    }

    private static string SafeGetUser()
    {
        try { return WindowsIdentity.GetCurrent().Name ?? "<unknown>"; }
        catch { return "<unknown>"; }
    }

    private static string SafeGetMachine()
    {
        try { return Environment.MachineName ?? "<unknown>"; }
        catch { return "<unknown>"; }
    }

    private static string JoinCsv(IEnumerable<string> fields)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var f in fields)
        {
            if (!first) sb.Append(',');
            sb.Append(EscapeField(f));
            first = false;
        }
        return sb.ToString();
    }

    private static string EscapeField(string? value)
    {
        value ??= string.Empty;
        bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!needsQuotes) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
