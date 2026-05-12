using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterXeneonWidget.Host.Audio;

/// <summary>
/// Talks to SteelSeries GG / Sonar's local REST API to cycle the active
/// output device. Mirrors what the user's Sonar hotkey does — flips the
/// "ALL OUTPUT DEVICES" selection — but driven from a widget button so it
/// works regardless of focus / VM state.
///
/// Discovery is two-stage:
///   1. Read %ProgramData%\SteelSeries\GG\coreProps.json (or the older
///      ...\SteelSeries Engine 3\coreProps.json) for the GG endpoint
///      address. The port is randomized on every GG launch, so we re-read
///      every cache miss instead of stashing it across restarts.
///   2. GET {gg}/subApps → subApps.sonar.metadata.webServerAddress gives
///      Sonar's REST base. That URL is the one we hit for /audioDevices
///      and /classicRedirections.
///
/// Both HTTP and HTTPS variants of the GG endpoint show up in the wild
/// depending on GG version. We try plain HTTP first (the `address` field
/// in coreProps), then fall back to HTTPS using `encryptedAddress` with
/// cert validation bypassed — GG ships a self-signed cert per install.
/// </summary>
public sealed class SteelSeriesService
{
    private readonly ILogger<SteelSeriesService> _log;
    private readonly HttpClient _http;

    // Cached Sonar base URL. Invalidated when a call returns a connection
    // failure — Sonar's port changes on every GG restart, so a transient
    // failure usually means we need to re-discover.
    private string? _cachedSonarBase;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);

    private static readonly string[] CorePropsCandidates =
    {
        // GG (post-rename) — the user's machine in May 2026 has this.
        @"C:\ProgramData\SteelSeries\GG\coreProps.json",
        // SteelSeries Engine 3 (legacy install path some users still have).
        @"C:\ProgramData\SteelSeries\SteelSeries Engine 3\coreProps.json",
    };

    // Channels Sonar uses for classic-mode redirection. We flip all four
    // together to mimic the "Sound Device Manager" master-output behavior —
    // there's no single endpoint that switches the master, redirections are
    // per-channel.
    private static readonly string[] ClassicChannels = { "game", "media", "chat", "aux" };

    public SteelSeriesService(ILogger<SteelSeriesService> log)
    {
        _log = log;

        // GG's HTTPS endpoint uses a self-signed cert that's regenerated on
        // every install. We're talking to 127.0.0.1 only — skipping cert
        // validation here is the standard pattern every community wrapper
        // uses; there's no realistic MITM vector on loopback.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public sealed record StatusDto(bool Available, string? CurrentDeviceName, string? CurrentDeviceId, IReadOnlyList<SteelSeriesDeviceDto> Devices);
    public sealed record SteelSeriesDeviceDto(string Id, string Name, bool IsCurrent);
    public sealed record SwapResultDto(bool Ok, string? NewDeviceName, string? NewDeviceId, string? Error);

    /// <summary>
    /// Returns the list of physical render devices Sonar knows about, plus
    /// which one is currently selected as Sonar's master output (read off
    /// the "media" channel's redirection — all four classic channels stay
    /// in sync because we always flip them together).
    /// </summary>
    public async Task<StatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var baseUrl = await DiscoverSonarBaseAsync(ct).ConfigureAwait(false);
        if (baseUrl is null) return new StatusDto(false, null, null, Array.Empty<SteelSeriesDeviceDto>());

        try
        {
            var (devices, currentId) = await ReadDevicesAndCurrentAsync(baseUrl, ct).ConfigureAwait(false);
            var current = devices.FirstOrDefault(d => d.Id == currentId);
            var dtos = devices
                .Select(d => new SteelSeriesDeviceDto(d.Id, d.Name, d.Id == currentId))
                .ToArray();
            return new StatusDto(true, current?.Name, currentId, dtos);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sonar status read failed; will retry on next call");
            InvalidateBase();
            return new StatusDto(false, null, null, Array.Empty<SteelSeriesDeviceDto>());
        }
    }

    /// <summary>
    /// Cycles all classic Sonar channels to the next physical render device
    /// in the order returned by /audioDevices (which matches the order the
    /// Sonar UI shows in "ALL OUTPUT DEVICES"). Returns the new device on
    /// success. If only one device is available, returns Ok=true with the
    /// same device — no-op rather than error.
    /// </summary>
    public async Task<SwapResultDto> CycleNextAsync(CancellationToken ct = default)
    {
        var baseUrl = await DiscoverSonarBaseAsync(ct).ConfigureAwait(false);
        if (baseUrl is null) return new SwapResultDto(false, null, null, "Sonar not reachable");

        try
        {
            var (devices, currentId) = await ReadDevicesAndCurrentAsync(baseUrl, ct).ConfigureAwait(false);
            if (devices.Count == 0) return new SwapResultDto(false, null, null, "No physical output devices");

            var idx = devices.FindIndex(d => d.Id == currentId);
            // If we couldn't read a current device, start from device 0; the
            // resulting "next" still picks something sensible (index 1 if >1
            // device, index 0 if only one).
            var nextIdx = (idx < 0 ? 0 : idx + 1) % devices.Count;
            var next = devices[nextIdx];

            foreach (var channel in ClassicChannels)
            {
                var url = $"{baseUrl.TrimEnd('/')}/classicRedirections/{channel}/deviceId/{Uri.EscapeDataString(next.Id)}";
                using var req = new HttpRequestMessage(HttpMethod.Put, url);
                req.Content = new StringContent(string.Empty);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                    _log.LogWarning("Sonar PUT classicRedirections/{Channel} returned {Status}: {Body}", channel, (int)resp.StatusCode, body);
                    return new SwapResultDto(false, null, null, $"PUT {channel} → HTTP {(int)resp.StatusCode}");
                }
            }

            _log.LogInformation("Sonar output cycled to {Name} ({Id})", next.Name, next.Id);
            return new SwapResultDto(true, next.Name, next.Id, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sonar swap failed");
            InvalidateBase();
            return new SwapResultDto(false, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Diagnostic dump — returns raw responses from subApps + audioDevices
    /// + classicRedirections so we can iterate on the parsing logic without
    /// rebuilding the host every time. Wired to GET
    /// /api/audio/steelseries/probe.
    /// </summary>
    public async Task<JsonObject> ProbeAsync(CancellationToken ct = default)
    {
        var result = new JsonObject();
        var coreProps = TryReadCoreProps();
        result["coreProps"] = coreProps is null ? null : JsonNode.Parse(coreProps);

        var ggBase = TryResolveGgBase(coreProps);
        result["ggBase"] = ggBase;

        if (ggBase != null)
        {
            result["subApps"] = await TryGetJsonAsync(ggBase + "/subApps", ct).ConfigureAwait(false);
        }

        var sonarBase = await DiscoverSonarBaseAsync(ct).ConfigureAwait(false);
        result["sonarBase"] = sonarBase;
        if (sonarBase != null)
        {
            result["audioDevices"] = await TryGetJsonAsync(sonarBase + "/audioDevices", ct).ConfigureAwait(false);
            result["classicRedirections"] = await TryGetJsonAsync(sonarBase + "/classicRedirections", ct).ConfigureAwait(false);
        }

        return result;
    }

    // ---------------- internals ----------------

    private async Task<string?> DiscoverSonarBaseAsync(CancellationToken ct)
    {
        if (_cachedSonarBase is not null) return _cachedSonarBase;

        await _discoveryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedSonarBase is not null) return _cachedSonarBase;

            var coreProps = TryReadCoreProps();
            var ggBase = TryResolveGgBase(coreProps);
            if (ggBase is null)
            {
                _log.LogDebug("coreProps.json not found or unparseable");
                return null;
            }

            var subAppsRaw = await TryGetJsonAsync(ggBase + "/subApps", ct).ConfigureAwait(false);
            if (subAppsRaw is null)
            {
                _log.LogDebug("GG /subApps returned null at {Url}", ggBase);
                return null;
            }

            // Shape: { "subApps": { "sonar": { "metadata": { "webServerAddress": "..." } } } }
            var webServerAddress =
                subAppsRaw["subApps"]?["sonar"]?["metadata"]?["webServerAddress"]?.GetValue<string>()
                ?? subAppsRaw["sonar"]?["metadata"]?["webServerAddress"]?.GetValue<string>();
            if (string.IsNullOrEmpty(webServerAddress))
            {
                _log.LogDebug("Sonar webServerAddress missing from /subApps response");
                return null;
            }

            _cachedSonarBase = webServerAddress.TrimEnd('/');
            _log.LogInformation("Discovered Sonar API at {Url}", _cachedSonarBase);
            return _cachedSonarBase;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private void InvalidateBase() => _cachedSonarBase = null;

    private static string? TryReadCoreProps()
    {
        foreach (var path in CorePropsCandidates)
        {
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch { /* permission edge case — try next path */ }
        }
        return null;
    }

    /// <summary>
    /// coreProps.json has at minimum: address (plain HTTP),
    /// encryptedAddress (HTTPS), ggEncryptedAddress (HTTPS, newer GG core).
    /// Newer GG versions sometimes serve subApps only on the HTTPS variants.
    /// We prefer plain HTTP for cheaper connections, then fall through to
    /// HTTPS — the HttpClient is already configured to ignore cert errors
    /// on loopback.
    /// </summary>
    private static string? TryResolveGgBase(string? corePropsJson)
    {
        if (string.IsNullOrEmpty(corePropsJson)) return null;
        try
        {
            var doc = JsonNode.Parse(corePropsJson);
            if (doc is null) return null;
            var http = doc["address"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(http)) return "http://" + http;
            var https = doc["encryptedAddress"]?.GetValue<string>()
                ?? doc["ggEncryptedAddress"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(https)) return "https://" + https;
        }
        catch { /* fall through to null */ }
        return null;
    }

    private async Task<JsonNode?> TryGetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("GET {Url} → HTTP {Status}", url, (int)resp.StatusCode);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GET {Url} failed", url);
            return null;
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private sealed record SonarDevice(string Id, string Name);

    /// <summary>
    /// Pulls /audioDevices, filters to the physical render endpoints Sonar
    /// is willing to route to (excluding Sonar's own virtual devices), and
    /// reads /classicRedirections to find which of those is currently
    /// receiving the "media" channel — that's what the Sonar UI shows as
    /// the active output and what we treat as the "current" device.
    ///
    /// The exact field names below are best-effort guesses derived from
    /// community reverse-engineering — `id` / `name` / `friendlyName` and
    /// flags like `isVirtualDevice` / `sonarVirtual` / `dataFlow` show up
    /// across the projects but the official schema isn't documented. We
    /// try several common shapes and skip anything we can't parse.
    /// </summary>
    private async Task<(List<SonarDevice> Devices, string? CurrentId)> ReadDevicesAndCurrentAsync(string baseUrl, CancellationToken ct)
    {
        var devices = new List<SonarDevice>();
        var devicesNode = await TryGetJsonAsync(baseUrl + "/audioDevices", ct).ConfigureAwait(false);
        if (devicesNode is JsonArray arr)
        {
            foreach (var item in arr) AppendIfPhysicalRender(item, devices);
        }
        else if (devicesNode is JsonObject obj)
        {
            // Some versions wrap the array as { "devices": [...] }.
            if (obj["devices"] is JsonArray inner)
            {
                foreach (var item in inner) AppendIfPhysicalRender(item, devices);
            }
            else
            {
                // Or as a dict keyed by id. Fall through and try iterating values.
                foreach (var kvp in obj)
                {
                    if (kvp.Value is JsonObject) AppendIfPhysicalRender(kvp.Value, devices);
                }
            }
        }

        var redirections = await TryGetJsonAsync(baseUrl + "/classicRedirections", ct).ConfigureAwait(false);
        var currentId = ExtractMediaDeviceId(redirections);

        return (devices, currentId);
    }

    private static void AppendIfPhysicalRender(JsonNode? node, List<SonarDevice> sink)
    {
        if (node is not JsonObject obj) return;

        var id = obj["id"]?.GetValue<string>()
            ?? obj["deviceId"]?.GetValue<string>()
            ?? obj["device"]?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id)) return;

        var name = obj["friendlyName"]?.GetValue<string>()
            ?? obj["name"]?.GetValue<string>()
            ?? obj["displayName"]?.GetValue<string>()
            ?? id;

        // Exclude Sonar's own virtual devices — they show up alongside the
        // physical ones in /audioDevices and routing to them would be a
        // self-loop. We test a few likely flag names; if none are present
        // we keep the device (better to over-include than crash on swap).
        if (TryBool(obj, "isVirtualDevice") == true) return;
        if (TryBool(obj, "isSonarVirtualDevice") == true) return;
        if (TryBool(obj, "isSonar") == true) return;
        if (TryBool(obj, "virtual") == true) return;

        // Restrict to render (output) endpoints. Sonar exposes capture
        // devices on the same list; routing the Game stream to a mic would
        // be a hard error.
        var dataFlow = obj["dataFlow"]?.GetValue<string>()
            ?? obj["flow"]?.GetValue<string>()
            ?? obj["direction"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(dataFlow) &&
            !dataFlow.Equals("render", StringComparison.OrdinalIgnoreCase) &&
            !dataFlow.Equals("output", StringComparison.OrdinalIgnoreCase) &&
            !dataFlow.Equals("playback", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sink.Add(new SonarDevice(id, name));
    }

    private static bool? TryBool(JsonObject obj, string key)
    {
        var node = obj[key];
        if (node is null) return null;
        try { return node.GetValue<bool>(); } catch { return null; }
    }

    /// <summary>
    /// Pulls the current device id off the "media" channel of
    /// /classicRedirections. Shape varies — we accept either
    /// { "media": { "deviceId": "..." } } or { "media": "..." } and a few
    /// nesting variants seen in the wild.
    /// </summary>
    private static string? ExtractMediaDeviceId(JsonNode? redirections)
    {
        if (redirections is not JsonObject obj) return null;

        // Try "media" first; fall back to "game" since they're flipped
        // together. Either is fine for "what's currently active".
        foreach (var key in new[] { "media", "game", "chat", "aux" })
        {
            var node = obj[key] ?? obj["redirections"]?[key] ?? obj["classicRedirections"]?[key];
            if (node is null) continue;
            if (node is JsonObject channelObj)
            {
                var id = channelObj["deviceId"]?.GetValue<string>()
                    ?? channelObj["id"]?.GetValue<string>()
                    ?? channelObj["device"]?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) return id;
            }
            else
            {
                try { var v = node.GetValue<string>(); if (!string.IsNullOrEmpty(v)) return v; }
                catch { /* not a string — move on */ }
            }
        }
        return null;
    }
}
