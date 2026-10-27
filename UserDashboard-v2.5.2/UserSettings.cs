using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace UserDashboard.Services;

/// <summary>
/// Persists window position/size/state and per-column widths between sessions.
///
/// XML-serialized to %APPDATA%\UserDashboard\settings.xml. Uses XmlSerializer
/// (part of the core BCL) instead of System.Text.Json so it works on
/// .NET Framework 4.8 with zero NuGet dependencies.
///
/// All save/load operations are best-effort: failures are swallowed so a
/// corrupt settings file never prevents the app from launching.
/// </summary>
[XmlRoot("UserSettings")]
public sealed class UserSettings
{
    // Window placement
    public int? WindowX       { get; set; }
    public int? WindowY       { get; set; }
    public int? WindowWidth   { get; set; }
    public int? WindowHeight  { get; set; }
    public bool WindowMaximized { get; set; }

    // Theme
    public bool IsDarkMode { get; set; }

    /// <summary>
    /// Which main-window tab is selected: "Active" (hide disabled),
    /// "Disabled", "All", or "LoggedIn" (live RDS sessions). Defaults to
    /// Active so disabled accounts stay out of the daily working view.
    /// </summary>
    public string AccountView { get; set; } = "Active";

    // Per-column widths, keyed by column name
    public List<ColumnWidth> ColumnWidths { get; set; } = new();

    public sealed class ColumnWidth
    {
        [XmlAttribute] public string Name  { get; set; } = string.Empty;
        [XmlAttribute] public int    Width { get; set; }
    }

    // ------------------------------------------------------------------
    //  IO
    // ------------------------------------------------------------------

    private static string GetSettingsPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UserDashboard");
        return Path.Combine(dir, "settings.xml");
    }

    public static UserSettings Load()
    {
        try
        {
            string path = GetSettingsPath();
            if (!File.Exists(path)) return new UserSettings();

            using var fs = File.OpenRead(path);
            var serializer = new XmlSerializer(typeof(UserSettings));
            return (UserSettings?)serializer.Deserialize(fs) ?? new UserSettings();
        }
        catch
        {
            // Corrupt or unreadable settings - start fresh, do not crash the app.
            return new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            string path = GetSettingsPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            using var fs = File.Create(path);
            var serializer = new XmlSerializer(typeof(UserSettings));
            serializer.Serialize(fs, this);
        }
        catch
        {
            // Best effort - if we can't write to AppData, just continue.
        }
    }

    // ------------------------------------------------------------------
    //  Convenience helpers
    // ------------------------------------------------------------------

    public int? GetColumnWidth(string columnName)
    {
        foreach (var c in ColumnWidths)
        {
            if (string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase))
                return c.Width;
        }
        return null;
    }

    public void SetColumnWidth(string columnName, int width)
    {
        foreach (var c in ColumnWidths)
        {
            if (string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                c.Width = width;
                return;
            }
        }
        ColumnWidths.Add(new ColumnWidth { Name = columnName, Width = width });
    }
}
