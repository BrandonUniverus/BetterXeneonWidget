using System.Diagnostics;
using System.Net.Http;
using Microsoft.Win32;

namespace BetterXeneonWidget.Installer;

/// <summary>
/// Wraps the lifecycle ops the installer performs on the host service:
/// stop/start, autostart registration, health probe.
/// </summary>
internal static class Host
{
    public const string ProcessName = "BetterXeneonWidget.Host";
    public const string LauncherFile = "launcher.vbs";
    public const int DefaultPort = 8976;
    public const string AutostartName = "BetterXeneonWidget";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static int StopAll()
    {
        var killed = 0;
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            try
            {
                p.Kill(entireProcessTree: false);
                p.WaitForExit(2000);
                killed++;
            }
            catch
            {
                /* process exited between enumeration and kill, fine */
            }
            finally
            {
                p.Dispose();
            }
        }
        return killed;
    }

    public static void StartViaLauncher(string installDir)
    {
        var launcher = Path.Combine(installDir, LauncherFile);
        Process.Start(new ProcessStartInfo
        {
            FileName = "wscript.exe",
            Arguments = $"\"{launcher}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    public static void RegisterAutostart(string installDir)
    {
        var launcher = Path.Combine(installDir, LauncherFile);
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(AutostartName, $"wscript.exe \"{launcher}\"");
    }

    public static void UnregisterAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(AutostartName) != null)
            key.DeleteValue(AutostartName, throwOnMissingValue: false);
    }

    /// <summary>Reads Listen.Port from appsettings.json, falling back to 8976.</summary>
    public static int ResolvePort(string installDir)
    {
        var path = Path.Combine(installDir, "appsettings.json");
        if (!File.Exists(path)) return DefaultPort;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Listen", out var listen)
                && listen.TryGetProperty("Port", out var port)
                && port.TryGetInt32(out var n))
            {
                return n;
            }
        }
        catch
        {
            /* malformed config — fall through to default */
        }
        return DefaultPort;
    }

    public static bool WaitForHealth(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var res = http.GetAsync($"http://127.0.0.1:{port}/api/health").GetAwaiter().GetResult();
                if (res.IsSuccessStatusCode) return true;
            }
            catch
            {
                /* connection refused while host warms up — keep trying */
            }
            Thread.Sleep(300);
        }
        return false;
    }
}
