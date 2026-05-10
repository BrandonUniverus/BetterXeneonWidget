using Microsoft.Win32;
using System.Runtime.Versioning;

namespace BetterXeneonWidget.Installer;

/// <summary>
/// Registers / unregisters the `betterxeneonwidget://` custom URI scheme
/// that Spotify's OAuth callback redirects to. Per-user (HKCU\Software\Classes)
/// — no admin needed. Windows uses this to find the handler when the browser
/// follows the redirect.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UriScheme
{
    public const string Scheme = "betterxeneonwidget";
    private const string ClassesKey = @"Software\Classes\" + Scheme;

    public static void Register(string handlerScriptPath)
    {
        // Per the Win32 URL Protocol contract:
        //   HKCU\Software\Classes\<scheme>\
        //     (Default)        = "URL: <description>"
        //     URL Protocol     = ""        ← presence of this value is what marks it
        //     shell\open\command\
        //       (Default)      = wscript.exe "...\oauth-forward.vbs" "%1"
        //
        // %1 is the full URL Windows hands the handler — including the query
        // string with ?code=&state=.
        using var root = Registry.CurrentUser.CreateSubKey(ClassesKey);
        root.SetValue("", "URL: BetterXeneonWidget Spotify OAuth callback");
        root.SetValue("URL Protocol", "");

        using var cmd = root.CreateSubKey(@"shell\open\command");
        cmd.SetValue("", $"wscript.exe \"{handlerScriptPath}\" \"%1\"");
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(ClassesKey, throwOnMissingSubKey: false);
        }
        catch
        {
            /* not registered, nothing to do */
        }
    }
}
