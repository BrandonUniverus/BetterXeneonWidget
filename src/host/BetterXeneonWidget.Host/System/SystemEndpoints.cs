using Microsoft.Win32;

namespace BetterXeneonWidget.Host.System;

public static class SystemEndpoints
{
    private const string WindowsDefaultBlue = "#0078d4";

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system");

        group.MapGet("/accent-color", () =>
        {
            return new { hex = ReadAccentColor() ?? WindowsDefaultBlue };
        });

        return app;
    }

    private static string? ReadAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            // AccentColor is stored as 0xAABBGGRR (little-endian DWORD); we want #RRGGBB.
            var raw = (int?)key?.GetValue("AccentColor");
            if (raw is null) return null;
            var v = raw.Value;
            var r = v & 0xFF;
            var g = (v >> 8) & 0xFF;
            var b = (v >> 16) & 0xFF;
            return $"#{r:x2}{g:x2}{b:x2}";
        }
        catch
        {
            return null;
        }
    }
}
