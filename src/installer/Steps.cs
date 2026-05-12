using System.Diagnostics;

namespace BetterXeneonWidget.Installer;

internal static class Steps
{
    private const string HostExeName = "BetterXeneonWidget.Host.exe";
    private const string LauncherName = "launcher.vbs";
    private const string OAuthForwardName = "oauth-forward.vbs";
    private const string AppsettingsName = "appsettings.json";
    private const string AppsettingsLocalName = "appsettings.Local.json";
    private const string AudioWidget = "com-betterxeneon-audioswitcher.icuewidget";
    // v2 is the shipping media widget (vanilla JS rebuild). v1 lives in
    // archive/v1-fallback-phase-7.5/ for emergency rollback only.
    private const string MediaWidget = "com-betterxeneon-media-v2.icuewidget";

    public static void Install()
    {
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", Program.AppName);
        var widgetsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", Program.AppName);
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Program.AppName, "host.log");

        Pretty.BeginPlan(8);

        Pretty.Step("Stopping any running host...");
        var killed = Host.StopAll();
        Pretty.Detail(killed > 0 ? $"Stopped {killed} process(es)" : "No running host");

        Pretty.Step($"Installing host to {installDir}...");
        Directory.CreateDirectory(installDir);
        var hostExeSize = Resources.EntryLength(HostExeName);
        Resources.ExtractTo(HostExeName, Path.Combine(installDir, HostExeName));
        Pretty.Detail($"Wrote {HostExeName} ({hostExeSize / (1024 * 1024)} MB)");
        Resources.ExtractTo(LauncherName, Path.Combine(installDir, LauncherName));
        Pretty.Detail($"Wrote {LauncherName}");
        var oauthForwardPath = Path.Combine(installDir, OAuthForwardName);
        Resources.ExtractTo(OAuthForwardName, oauthForwardPath);
        Pretty.Detail($"Wrote {OAuthForwardName}");
        var appsettingsPath = Path.Combine(installDir, AppsettingsName);
        if (File.Exists(appsettingsPath))
        {
            Pretty.Detail($"Preserved existing {AppsettingsName} (delete it to reset to defaults)");
        }
        else
        {
            Resources.ExtractTo(AppsettingsName, appsettingsPath);
            Pretty.Detail($"Wrote {AppsettingsName}");
        }

        // appsettings.Local.json is the per-build dev override (e.g. a private
        // Spotify ClientId). Present in the payload only when the dev machine
        // had one at publish time. ASP.NET Core's layered config loads it
        // after the base file, so its values override appsettings.json.
        // PRESERVE existing files on reinstall — same rule as appsettings.json.
        // Earlier this always overwrote, which would silently stomp another
        // machine's working ClientId when an installer built on a different
        // dev machine was run there. Devs that want to push a new override
        // can either delete the live file before reinstall, or edit it
        // in place.
        if (Resources.HasEntry(AppsettingsLocalName))
        {
            var localPath = Path.Combine(installDir, AppsettingsLocalName);
            if (File.Exists(localPath))
            {
                Pretty.Detail($"Preserved existing {AppsettingsLocalName}");
            }
            else
            {
                Resources.ExtractTo(AppsettingsLocalName, localPath);
                Pretty.Detail($"Wrote {AppsettingsLocalName} (dev override)");
            }
        }

        Pretty.Step("Registering Spotify OAuth URI handler (betterxeneonwidget://)...");
        UriScheme.Register(oauthForwardPath);
        Pretty.Detail("HKCU\\Software\\Classes\\betterxeneonwidget");

        Pretty.Step("Registering autostart at user login...");
        Host.RegisterAutostart(installDir);
        Pretty.Detail($"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\{Host.AutostartName}");

        Pretty.Step($"Copying widgets to {widgetsDir}...");
        Directory.CreateDirectory(widgetsDir);
        Resources.ExtractTo(AudioWidget, Path.Combine(widgetsDir, AudioWidget));
        Pretty.Detail(AudioWidget);
        Resources.ExtractTo(MediaWidget, Path.Combine(widgetsDir, MediaWidget));
        Pretty.Detail(MediaWidget);

        Pretty.Step("Starting host service...");
        Host.StartViaLauncher(installDir);

        Pretty.Step("Waiting for host to come online...");
        var port = Host.ResolvePort(installDir);
        var healthy = Host.WaitForHealth(port, TimeSpan.FromSeconds(8));
        if (healthy)
        {
            Pretty.Detail($"Responding on http://127.0.0.1:{port}");
        }
        else
        {
            Pretty.WriteWarning($"      Host did not respond within 8s.");
            Pretty.WriteWarning($"      Check the log: {logPath}");
        }

        Pretty.Step("Opening widgets folder in Explorer...");
        try
        {
            Process.Start(new ProcessStartInfo { FileName = widgetsDir, UseShellExecute = true });
            Pretty.Detail("Opened.");
        }
        catch (Exception ex)
        {
            Pretty.WriteWarning($"      Could not open Explorer: {ex.Message}");
        }

        Console.WriteLine();
        Pretty.WriteSuccess("Done.");
        Console.WriteLine($"  Host:      {installDir}");
        Console.WriteLine($"  Widgets:   {widgetsDir}");
        Console.WriteLine($"  Logs:      {logPath}");
        Console.WriteLine($"  Uninstall: re-run this exe with --uninstall");
        Console.WriteLine();
        Console.WriteLine("Spotify Developer Dashboard redirect URI must be:");
        Pretty.WriteAccent("  betterxeneonwidget://callback");
        Console.WriteLine();
        Console.WriteLine("Double-click each .icuewidget in Explorer to install it in iCUE.");
    }

    public static void Uninstall()
    {
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", Program.AppName);

        Pretty.BeginPlan(4);

        Pretty.Step("Stopping host...");
        var killed = Host.StopAll();
        Pretty.Detail(killed > 0 ? $"Stopped {killed} process(es)" : "Not running");

        Pretty.Step("Removing autostart...");
        Host.UnregisterAutostart();
        Pretty.Detail("HKCU autostart entry cleared");

        Pretty.Step("Removing Spotify OAuth URI handler...");
        UriScheme.Unregister();
        Pretty.Detail("HKCU\\Software\\Classes\\betterxeneonwidget cleared");

        Pretty.Step($"Removing {installDir}...");
        if (Directory.Exists(installDir))
        {
            try
            {
                Directory.Delete(installDir, recursive: true);
                Pretty.Detail("Deleted");
            }
            catch (Exception ex)
            {
                Pretty.WriteWarning($"      Could not delete: {ex.Message}");
            }
        }
        else
        {
            Pretty.Detail("Not present");
        }

        Console.WriteLine();
        Pretty.WriteSuccess("Done.");
        Console.WriteLine();
        Console.WriteLine($"  The widgets in ~\\Downloads\\{Program.AppName} were not removed.");
        Console.WriteLine($"  Uninstall them from inside iCUE if you no longer want them.");
    }
}
