using System.Text.Json;
using System.Text.Json.Nodes;

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

    private static ConfigDto Empty() => new(Array.Empty<string>(), false, null, null);

    public ConfigDto Read()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return Empty();
                var json = File.ReadAllText(_path);
                var dto = JsonSerializer.Deserialize<ConfigDto>(json, JsonOpts);
                return dto ?? Empty();
            }
            catch
            {
                return Empty();
            }
        }
    }

    /// <summary>
    /// Atomically read-modify-write the persisted config. The mutator
    /// receives the current value and returns the new one. Used for any
    /// field that mixes with others — keeps unrelated fields intact when
    /// only one is being updated.
    /// </summary>
    public void Update(Func<ConfigDto, ConfigDto> mutator)
    {
        lock (_lock)
        {
            try
            {
                ConfigDto current;
                if (File.Exists(_path))
                {
                    try { current = JsonSerializer.Deserialize<ConfigDto>(File.ReadAllText(_path), JsonOpts) ?? Empty(); }
                    catch { current = Empty(); }
                }
                else current = Empty();

                var next = mutator(current) with { Initialized = true };
                File.WriteAllText(_path, JsonSerializer.Serialize(next, JsonOpts));
            }
            catch
            {
                /* disk write failure — change won't persist; acceptable */
            }
        }
    }

    public void WritePins(string[] pinnedIds) =>
        Update(c => c with { PinnedIds = pinnedIds ?? Array.Empty<string>() });

    public void WriteAudioCaptureDeviceId(string? deviceId) =>
        Update(c => c with { AudioCaptureDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId });

    public string? ReadAudioCaptureDeviceId() => Read().AudioCaptureDeviceId;

    /// <summary>
    /// Settings owned by the widget UI (poll cadence, lyric options,
    /// listening-view mode, theme, etc.). Stored as an opaque JSON
    /// object so adding a new setting doesn't require touching this
    /// host code — the widget POSTs the full settings object and we
    /// store it verbatim. Round-trips through JsonNode so we don't
    /// re-stringify on every read.
    /// </summary>
    public JsonObject ReadWidgetSettings() => Read().WidgetSettings ?? new JsonObject();

    public void WriteWidgetSettings(JsonObject settings) =>
        Update(c => c with { WidgetSettings = settings });
}

public sealed record ConfigDto(
    string[] PinnedIds,
    bool Initialized,
    /// <summary>
    /// Windows MMDevice endpoint ID (the long {0.0.0.x}.{guid} string)
    /// for the audio render device the spectrum service should capture
    /// from. Null = use the system default device. Lets users with
    /// software mixers (SteelSeries Sonar, Voicemeeter, Equalizer APO,
    /// etc.) pick the specific virtual device carrying their music
    /// without changing their Windows default output.
    /// </summary>
    string? AudioCaptureDeviceId,
    /// <summary>
    /// Settings object owned by the widget UI — opaque to the host. The
    /// widget POSTs the full settings object on change; we persist it
    /// verbatim, return it verbatim on the next GET. Lets us add new
    /// widget settings without touching the host schema.
    /// </summary>
    JsonObject? WidgetSettings);

public sealed record SetPinsRequest(string[] PinnedIds);
public sealed record SetAudioCaptureSourceRequest(string? DeviceId);
