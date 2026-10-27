using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UserDashboard;

/// <summary>
/// Writes or removes GSSLOGIN.DIR in a user's Windows profile. Global Shop
/// reads this file at launch; it does not pick up AD group changes by itself.
/// </summary>
internal static class CompanyCodeLoginFile
{
    public const string FileName = "GSSLOGIN.DIR";
    public const string ListSeparator = "*!*";

    public static string BuildContent(IEnumerable<string> codes, string? primary)
    {
        var list = (codes ?? Enumerable.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0) return string.Empty;

        string selected = list[0];
        if (!string.IsNullOrWhiteSpace(primary))
        {
            string want = primary!.Trim().ToUpperInvariant();
            if (list.Contains(want, StringComparer.OrdinalIgnoreCase))
                selected = want;
        }

        return "SELECTEDCOMPANYCODE::" + selected + "\r\n" +
               "COMPANYCODELIST::" + string.Join(ListSeparator, list) + "\r\n";
    }

    public static List<string> CandidatePaths(string samAccountName)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(samAccountName)) return paths;

        string usersDir = Path.Combine(
            Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "Users");
        if (!Directory.Exists(usersDir)) return paths;

        void Add(string profileDir)
        {
            if (string.IsNullOrWhiteSpace(profileDir)) return;
            string full = Path.Combine(profileDir, FileName);
            if (!paths.Contains(full, StringComparer.OrdinalIgnoreCase))
                paths.Add(full);
        }

        string exact = Path.Combine(usersDir, samAccountName.Trim());
        if (Directory.Exists(exact)) Add(exact);

        try
        {
            foreach (var dir in Directory.GetDirectories(usersDir, samAccountName.Trim() + ".*"))
                Add(dir);
        }
        catch { /* ignore enumeration failures */ }

        return paths;
    }

    /// <summary>
    /// Write GSSLOGIN.DIR for the assigned codes, or delete it when
    /// unrestricted. Returns human-readable notes (success and failures).
    /// </summary>
    public static List<string> Apply(string samAccountName, IEnumerable<string> codes, string? primary)
    {
        var notes = new List<string>();
        var paths = CandidatePaths(samAccountName);
        if (paths.Count == 0)
            return notes;

        string content = BuildContent(codes, primary);
        foreach (var path in paths)
        {
            try
            {
                if (string.IsNullOrEmpty(content))
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        notes.Add("Removed " + path + " (unrestricted).");
                    }
                }
                else
                {
                    File.WriteAllText(path, content, Encoding.ASCII);
                    notes.Add("Wrote " + path);
                }
            }
            catch (Exception ex)
            {
                notes.Add(path + ": " + ex.Message);
            }
        }
        return notes;
    }
}
