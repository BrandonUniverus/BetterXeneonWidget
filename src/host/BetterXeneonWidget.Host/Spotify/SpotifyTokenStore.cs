using System.Text.Json;

namespace BetterXeneonWidget.Host.Spotify;

/// <summary>
/// File-backed token store at %APPDATA%\BetterXeneonWidget\spotify.json.
/// %APPDATA% is per-user already, so we don't bother with DPAPI for now —
/// the refresh token is the only sensitive value and trusting the user's
/// home directory is the same trust model as Spotify's official desktop app.
/// </summary>
public sealed class SpotifyTokenStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private SpotifyTokens? _cached;

    public SpotifyTokenStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterXeneonWidget");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "spotify.json");
    }

    public SpotifyTokens? Read()
    {
        lock (_lock)
        {
            if (_cached != null) return _cached;
            if (!File.Exists(_path)) return null;
            try
            {
                var json = File.ReadAllText(_path);
                _cached = JsonSerializer.Deserialize<SpotifyTokens>(json, JsonOpts);
                return _cached;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Save(SpotifyTokens tokens)
    {
        lock (_lock)
        {
            _cached = tokens;
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(tokens, JsonOpts));
            }
            catch
            {
                /* disk write failure — next refresh will re-authenticate, acceptable */
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cached = null;
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* ignore */ }
        }
    }
}
