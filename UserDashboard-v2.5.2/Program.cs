using System;
using System.DirectoryServices.ActiveDirectory;
using System.Threading;
using System.Windows.Forms;
using UserDashboard.Services;

namespace UserDashboard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // On .NET Framework 4.8 there is no ApplicationConfiguration.Initialize()
        // source generator, so do the setup manually. Per-Monitor V2 DPI awareness
        // is declared in app.manifest; DpiHelper.ConfigureForm() sets AutoScaleMode.Dpi
        // on each form so layouts scale on 4K / 125–200% displays.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ---- Single instance PER SESSION -----------------------------------
        // "Local\" scopes the mutex to this RDS session, so different users on
        // the same session host each get their own instance (required for
        // multi-user RDS), but one user can't accidentally stack up several
        // copies inside their own session.
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\GSS-UserDashboard-" + Environment.UserName,
            createdNew: out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "User Dashboard is already running in this session.\r\n" +
                "Check your taskbar for the existing window.",
                "User Dashboard",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // ---- Global crash handlers ------------------------------------------
        // Any unhandled error is audited (so ops can see WHY it died) and shown
        // as a friendly dialog instead of the default .NET crash box.
        Application.ThreadException += (_, e) => HandleFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleFatal(e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Preflight: confirm we can talk to a domain. Equivalent to the PowerShell
        // RSAT/ActiveDirectory module check at the top of UserDashboard.ps1.
        try
        {
            _ = Domain.GetCurrentDomain();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "This dashboard requires the machine to be joined to an Active Directory domain " +
                "and the user to have read access to the domain.\r\n\r\n" +
                "Details: " + ex.Message,
                "No Active Directory Domain Available",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // Startup heartbeat with the build version - makes "which build is
        // deployed / when was it last used" answerable from the audit log alone.
        AuditLogger.Write("Startup", Environment.UserName,
            $"UserDashboard v{AppConfig.Version} on {Environment.MachineName}" +
            (AppConfig.ConfigFileLoaded ? $" (config: {AppConfig.ConfigFilePath})" : " (no site config file)"));

        Application.Run(new MainForm());
    }

    private static void HandleFatal(Exception? ex)
    {
        try
        {
            AuditLogger.Write("UnhandledError", Environment.UserName,
                ex?.ToString() ?? "(unknown exception)");
        }
        catch { /* auditing must not block the error dialog */ }

        MessageBox.Show(
            "An unexpected error occurred:\r\n\r\n" +
            (ex?.Message ?? "(unknown)") + "\r\n\r\n" +
            "The error has been written to the audit log. If it keeps happening, " +
            "please submit a cloud request with the time it occurred.",
            "User Dashboard - Unexpected Error",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
