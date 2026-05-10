using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace BetterXeneonWidget.Host.Audio;

/// <summary>
/// Process-lifetime cache of extracted PNG icon bytes, keyed by the original
/// IconPath string. Multiple devices commonly share the same icon resource
/// (e.g. mmres.dll,-3010 for generic speakers) — caching once per resource
/// avoids re-running ExtractIconEx for every poll.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IconCache
{
    private static readonly byte[] Sentinel = [];
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    public byte[]? Get(string iconPath)
    {
        if (string.IsNullOrEmpty(iconPath)) return null;
        if (_cache.TryGetValue(iconPath, out var cached))
            return cached.Length > 0 ? cached : null;

        var bytes = IconExtractor.GetPngBytes(iconPath);
        _cache[iconPath] = bytes ?? Sentinel;
        return bytes;
    }
}
