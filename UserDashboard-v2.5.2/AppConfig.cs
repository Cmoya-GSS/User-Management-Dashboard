using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace UserDashboard;

/// <summary>
/// Runtime configuration for the dashboard:
///
///  1. Version - read from the assembly (set in the .csproj), used in the
///     title bar, About dialog, and audit log so the running build is always
///     identifiable.
///
///  2. Optional site config overlay - if a file named
///     "UserDashboard.config.json" exists NEXT TO THE EXE, its values override
///     the compiled-in defaults from Constants. This lets the SAME binary run
///     in any domain (gss-cloud.com, gsserpgov.com, ...) with at most a tiny
///     per-site text file - no recompile needed. Missing file or missing keys
///     simply fall back to Constants; a malformed file is ignored (the
///     dashboard never fails to start because of config).
///
/// Supported keys (all optional):
/// {
///   "RdsSessionHosts":         [ "EXTRAHOST.otherdomain.com" ],
///   "PasswordChangeUrlOverride": "https://aci.gsserpgov.com/RDWeb/Pages/en-US/password.aspx",
///   "AuditDir":                "D:\\GSS\\UserDashboard\\Log",
///   "CompanyCodeGroupPrefix":  "CC-",
///   "CompanyCodePrimaryAttribute": "comment"
/// }
/// </summary>
internal static class AppConfig
{
    // ---- Version -----------------------------------------------------------

    /// <summary>e.g. "2.4.1" - from InformationalVersion in the csproj.</summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (!string.IsNullOrWhiteSpace(info?.InformationalVersion))
                return info!.InformationalVersion;
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch { return "0.0.0"; }
    }

    // ---- Site config overlay ------------------------------------------------

    private static readonly Dictionary<string, object> _cfg = LoadConfigFile();

    /// <summary>Full path of the config file (whether or not it exists).</summary>
    public static string ConfigFilePath { get; } =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "UserDashboard.config.json");

    /// <summary>True if a config file was found and parsed successfully.</summary>
    public static bool ConfigFileLoaded { get; private set; }

    private static Dictionary<string, object> LoadConfigFile()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".",
                                       "UserDashboard.config.json");
            if (!File.Exists(path)) return new Dictionary<string, object>();

            string json = File.ReadAllText(path);
            var parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if (parsed == null) return new Dictionary<string, object>();

            ConfigFileLoaded = true;
            // Case-insensitive key access.
            return new Dictionary<string, object>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Malformed config must never stop the dashboard - fall back to defaults.
            return new Dictionary<string, object>();
        }
    }

    private static string? GetString(string key)
    {
        if (_cfg.TryGetValue(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
            return s.Trim();
        return null;
    }

    private static string[] GetStringArray(string key)
    {
        if (_cfg.TryGetValue(key, out var v) && v is System.Collections.IEnumerable en && !(v is string))
        {
            return en.Cast<object>()
                     .Select(o => o?.ToString() ?? "")
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Select(s => s.Trim())
                     .ToArray();
        }
        return Array.Empty<string>();
    }

    // ---- Effective settings (config overlay -> Constants default) -----------

    /// <summary>
    /// Extra/forced RDS session hosts: config-file entries plus the compiled-in
    /// Constants list. (Auto-discovery via AD still runs; these are supplements.)
    /// </summary>
    public static string[] RdsSessionHosts =>
        GetStringArray("RdsSessionHosts")
            .Concat(Constants.RdsSessionHosts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>RD Web password page override: config file wins, then Constants.</summary>
    public static string PasswordChangeUrlOverride =>
        GetString("PasswordChangeUrlOverride") ?? Constants.PasswordChangeUrlOverride;

    /// <summary>Primary audit directory: config file wins, then Constants.</summary>
    public static string AuditDir =>
        GetString("AuditDir") ?? Constants.AuditDir;

    /// <summary>
    /// Prefix for Global Shop company-code security groups. Config file wins,
    /// then Constants. Empty/whitespace falls back to the compiled default.
    /// Must stay in sync with README/Apply-CompanyCodes.ps1.
    /// </summary>
    public static string CompanyCodeGroupPrefix
    {
        get
        {
            string? v = GetString("CompanyCodeGroupPrefix");
            return string.IsNullOrWhiteSpace(v) ? Constants.CompanyCodeGroupPrefix : v!;
        }
    }

    /// <summary>
    /// LDAP attribute used for the user's default company code. Config file
    /// wins, then Constants.
    /// </summary>
    public static string CompanyCodePrimaryAttribute
    {
        get
        {
            string? v = GetString("CompanyCodePrimaryAttribute");
            return string.IsNullOrWhiteSpace(v) ? Constants.CompanyCodePrimaryAttribute : v!;
        }
    }

    /// <summary>
    /// Accounts the dashboard refuses to log off: compiled defaults
    /// (Constants.ProtectedLogoffAccounts) PLUS any site-config additions.
    /// Config can only ADD protections, never remove the built-ins.
    /// </summary>
    public static HashSet<string> ProtectedLogoffAccounts
    {
        get
        {
            var set = new HashSet<string>(Constants.ProtectedLogoffAccounts,
                                          StringComparer.OrdinalIgnoreCase);
            foreach (var extra in GetStringArray("ProtectedLogoffAccounts"))
                set.Add(extra);
            return set;
        }
    }
}
