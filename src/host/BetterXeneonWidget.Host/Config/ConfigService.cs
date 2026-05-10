using System.Text.Json;

namespace BetterXeneonWidget.Host.Config;

/// <summary>
/// Host-side persistence for the user's widget preferences. Lives at
/// %APPDATA%\BetterXeneonWidget\config.json so it survives iCUE preset swaps,
/// widget reinstalls, and even iCUE reinstalls. The widget's per-instance
/// localStorage is too volatile — every preset gets a new uniqueId.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _lock = new();

    public ConfigService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterXeneonWidget");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
    }

    public ConfigDto Read()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path))
                    return new ConfigDto(Array.Empty<string>(), false);
                var json = File.ReadAllText(_path);
                var dto = JsonSerializer.Deserialize<ConfigDto>(json, JsonOpts);
                return dto ?? new ConfigDto(Array.Empty<string>(), false);
            }
            catch
            {
                return new ConfigDto(Array.Empty<string>(), false);
            }
        }
    }

    public void WritePins(string[] pinnedIds)
    {
        lock (_lock)
        {
            try
            {
                var dto = new ConfigDto(pinnedIds ?? Array.Empty<string>(), true);
                var json = JsonSerializer.Serialize(dto, JsonOpts);
                File.WriteAllText(_path, json);
            }
            catch
            {
                /* disk write failure — pins will reset next session, acceptable */
            }
        }
    }
}

public sealed record ConfigDto(string[] PinnedIds, bool Initialized);
public sealed record SetPinsRequest(string[] PinnedIds);
