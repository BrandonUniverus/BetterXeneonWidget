using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BetterXeneonWidget.Host.Audio;

/// <summary>
/// Extracts the icon referenced by an MMDevice.IconPath string ("dll,-resourceID")
/// and returns it as PNG bytes. Returns null if the icon can't be loaded.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IconExtractor
{
    public static byte[]? GetPngBytes(string iconPath)
    {
        var (dllPath, resourceId) = ParseIconPath(iconPath);
        if (dllPath == null) return null;
        if (!File.Exists(dllPath)) return null;

        IntPtr[] handles = new IntPtr[1];
        // ExtractIconEx: nIconIndex < 0 means resource ID (path string already encodes
        // the negative for us, e.g. "%windir%\system32\mmres.dll,-3010" → resourceId = -3010).
        int extracted;
        try
        {
            extracted = ExtractIconEx(dllPath, resourceId, handles, null, 1);
        }
        catch
        {
            return null;
        }

        if (extracted <= 0 || handles[0] == IntPtr.Zero) return null;

        try
        {
            using var icon = Icon.FromHandle(handles[0]);
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }

    private static (string?, int) ParseIconPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return (null, 0);
        var idx = path.LastIndexOf(',');
        if (idx <= 0 || idx == path.Length - 1) return (null, 0);

        var dllPart = path[..idx];
        var idPart = path[(idx + 1)..];
        if (!int.TryParse(idPart, out var id)) return (null, 0);

        var expanded = Environment.ExpandEnvironmentVariables(dllPart).Trim('"');
        return (expanded, id);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int ExtractIconEx(
        string lpszFile, int nIconIndex,
        IntPtr[] phiconLarge, IntPtr[]? phiconSmall, int nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
