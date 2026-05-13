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
///      addresses. The port is randomized on every GG launch, so we re-read
///      every cache miss instead of stashing it across restarts.
///   2. GET {gg}/subApps -> subApps.sonar.metadata.webServerAddress gives
///      Sonar's REST base. That URL is the one we hit for /AudioDevices
///      and /ClassicRedirections.
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

    public sealed record StatusDto(bool Available, string? CurrentDeviceName, string? CurrentDeviceId, IReadOnlyList<SteelSeriesDeviceDto> Devices, string? Error);
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
        if (baseUrl is null) return new StatusDto(false, null, null, Array.Empty<SteelSeriesDeviceDto>(), "Sonar not reachable");

        try
        {
            var snapshot = await ReadDevicesAndCurrentAsync(baseUrl, ct).ConfigureAwait(false);
            var current = snapshot.Devices.FirstOrDefault(d => d.Id == snapshot.CurrentId);
            var dtos = snapshot.Devices
                .Select(d => new SteelSeriesDeviceDto(d.Id, d.Name, d.Id == snapshot.CurrentId))
                .ToArray();
            return new StatusDto(true, current?.Name, snapshot.CurrentId, dtos, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sonar status read failed; will retry on next call");
            InvalidateBase();
            return new StatusDto(false, null, null, Array.Empty<SteelSeriesDeviceDto>(), ex.Message);
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
            var snapshot = await ReadDevicesAndCurrentAsync(baseUrl, ct).ConfigureAwait(false);
            var devices = snapshot.Devices;
            var currentId = snapshot.CurrentId;
            if (devices.Count == 0) return new SwapResultDto(false, null, null, "No physical output devices");

            var idx = devices.FindIndex(d => d.Id == currentId);
            // If we couldn't read a current device, start from device 0; the
            // resulting "next" still picks something sensible (index 1 if >1
            // device, index 0 if only one).
            var nextIdx = (idx < 0 ? 0 : idx + 1) % devices.Count;
            var next = devices[nextIdx];

            var errors = new List<string>();
            var successfulPuts = 0;
            async Task<bool> TryPutAsync(string label, string url)
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, url);
                req.Content = new StringContent(string.Empty);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;

                var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                var error = $"{label} HTTP {(int)resp.StatusCode}";
                errors.Add(error);
                _log.LogWarning("Sonar PUT {Label} returned {Status}: {Body}", label, (int)resp.StatusCode, body);
                return false;
            }

            var encodedId = Uri.EscapeDataString(next.Id);
            foreach (var channel in new[] { "game", "media", "chat", "aux" })
            {
                var url = $"{baseUrl.TrimEnd('/')}/classicRedirections/{channel}/deviceId/{encodedId}";
                if (await TryPutAsync($"classicRedirections/{channel}", url).ConfigureAwait(false)) successfulPuts++;
            }

            if (successfulPuts > 0)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var after = await ReadDevicesAndCurrentAsync(baseUrl, ct).ConfigureAwait(false);
                if (string.Equals(after.CurrentId, next.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("Sonar output cycled to {Name} ({Id})", next.Name, next.Id);
                    return new SwapResultDto(true, next.Name, next.Id, null);
                }
            }

            foreach (var redirectionId in snapshot.RedirectionIds)
            {
                var url = $"{baseUrl.TrimEnd('/')}/ClassicRedirections/{redirectionId}/deviceId/{encodedId}";
                if (await TryPutAsync($"ClassicRedirections/{redirectionId}", url).ConfigureAwait(false)) successfulPuts++;
            }

            if (successfulPuts == 0)
            {
                var detail = errors.Count == 0 ? "no Sonar route accepted the device" : string.Join("; ", errors.Distinct());
                return new SwapResultDto(false, null, null, $"Sonar rejected the output switch: {detail}");
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
            var finalSnapshot = await ReadDevicesAndCurrentAsync(baseUrl, ct).ConfigureAwait(false);
            if (!string.Equals(finalSnapshot.CurrentId, next.Id, StringComparison.OrdinalIgnoreCase))
            {
                var stillOn = devices.FirstOrDefault(d => d.Id == finalSnapshot.CurrentId)?.Name ?? finalSnapshot.CurrentId ?? "unknown";
                return new SwapResultDto(false, null, null, $"Sonar accepted the request but still reports {stillOn}");
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

        var candidates = ResolveGgCandidates(coreProps);
        var candidateNodes = new JsonArray();
        foreach (var candidate in candidates)
        {
            candidateNodes.Add(new JsonObject
            {
                ["source"] = candidate.Source,
                ["url"] = candidate.Url,
            });
        }
        result["ggCandidates"] = candidateNodes;

        var subAppsAttempts = new JsonArray();
        foreach (var candidate in candidates)
        {
            var attempt = await ProbeGetJsonAsync(candidate.Url + "/subApps", ct).ConfigureAwait(false);
            attempt["source"] = candidate.Source;
            subAppsAttempts.Add(attempt);
        }
        result["subAppsAttempts"] = subAppsAttempts;

        var sonarBase = await DiscoverSonarBaseAsync(ct).ConfigureAwait(false);
        result["sonarBase"] = sonarBase;
        if (sonarBase != null)
        {
            result["fallbackLists"] = await ProbeGetJsonAsync(sonarBase + "/FallbackSettings/lists", ct).ConfigureAwait(false);
            result["audioDevices"] = await ProbeGetJsonAsync(sonarBase + "/AudioDevices", ct).ConfigureAwait(false);
            result["classicRedirections"] = await ProbeGetJsonAsync(sonarBase + "/ClassicRedirections", ct).ConfigureAwait(false);
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
            var candidates = ResolveGgCandidates(coreProps);
            if (candidates.Count == 0)
            {
                _log.LogDebug("coreProps.json not found or unparseable");
                return null;
            }

            foreach (var candidate in candidates)
            {
                var subAppsRaw = await TryGetJsonAsync(candidate.Url + "/subApps", ct).ConfigureAwait(false);
                if (subAppsRaw is null)
                {
                    _log.LogDebug("GG /subApps returned null at {Url}", candidate.Url);
                    continue;
                }

                // Shape: { "subApps": { "sonar": { "metadata": { "webServerAddress": "..." } } } }
                var webServerAddress =
                    GetString(subAppsRaw["subApps"]?["sonar"]?["metadata"]?["webServerAddress"])
                    ?? GetString(subAppsRaw["sonar"]?["metadata"]?["webServerAddress"]);
                if (string.IsNullOrWhiteSpace(webServerAddress))
                {
                    _log.LogDebug("Sonar webServerAddress missing from /subApps response at {Url}", candidate.Url);
                    continue;
                }

                _cachedSonarBase = NormalizeBaseUrl(webServerAddress.Trim(), "http").TrimEnd('/');
                _log.LogInformation("Discovered Sonar API at {Url}", _cachedSonarBase);
                return _cachedSonarBase;
            }

            return null;
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

    private sealed record GgCandidate(string Source, string Url);

    private static IReadOnlyList<GgCandidate> ResolveGgCandidates(string? corePropsJson)
    {
        var candidates = new List<GgCandidate>();
        if (string.IsNullOrEmpty(corePropsJson)) return candidates;
        try
        {
            var doc = JsonNode.Parse(corePropsJson);
            if (doc is null) return candidates;

            void Add(string key, string scheme)
            {
                var value = GetString(doc[key]);
                if (string.IsNullOrWhiteSpace(value)) return;
                var url = NormalizeBaseUrl(value.Trim(), scheme);
                if (candidates.All(c => !string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(new GgCandidate(key, url));
                }
            }

            Add("address", "http");
            Add("encryptedAddress", "https");
            Add("ggEncryptedAddress", "https");
        }
        catch { /* return whatever we found before parse failed */ }
        return candidates;
    }

    private static string NormalizeBaseUrl(string value, string defaultScheme)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return value.TrimEnd('/');
        }

        return $"{defaultScheme}://{value.TrimEnd('/')}";
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

    private async Task<JsonObject> ProbeGetJsonAsync(string url, CancellationToken ct)
    {
        var result = new JsonObject
        {
            ["url"] = url,
        };

        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            result["ok"] = resp.IsSuccessStatusCode;
            result["status"] = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    result["json"] = JsonNode.Parse(body);
                }
                catch
                {
                    result["body"] = body.Length > 4096 ? body[..4096] : body;
                }
            }
        }
        catch (Exception ex)
        {
            result["ok"] = false;
            result["error"] = ex.Message;
            result["exceptionType"] = ex.GetType().Name;
        }

        return result;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private sealed record SonarDevice(string Id, string Name);
    private sealed record SonarSnapshot(List<SonarDevice> Devices, string? CurrentId, IReadOnlyList<int> RedirectionIds);

    /// <summary>
    /// Pulls /FallbackSettings/lists first because that response mirrors
    /// Sonar's "ALL OUTPUT DEVICES" list directly, including isActive and
    /// isExcluded flags. /AudioDevices is only a fallback for older GG
    /// builds where FallbackSettings is unavailable.
    /// </summary>
    private async Task<SonarSnapshot> ReadDevicesAndCurrentAsync(string baseUrl, CancellationToken ct)
    {
        var devices = new List<SonarDevice>();
        string? fallbackCurrentId = null;

        var fallbackNode = await TryGetJsonAsync(baseUrl + "/FallbackSettings/lists", ct).ConfigureAwait(false);
        if (fallbackNode is JsonArray fallbackLists)
        {
            foreach (var listNode in fallbackLists)
            {
                if (listNode is not JsonObject listObj || listObj["value"] is not JsonArray values) continue;
                var listDevices = new List<SonarDevice>();
                string? listCurrentId = null;
                foreach (var valueNode in values)
                {
                    if (valueNode is not JsonObject deviceObj || TryBool(deviceObj, "isExcluded") == true) continue;
                    var id = GetString(deviceObj["id"]);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var name = GetString(deviceObj["label"]) ?? id;
                    if (IsSonarVirtualDevice(id, name)) continue;
                    listDevices.Add(new SonarDevice(id, name));
                    if (TryBool(deviceObj, "isActive") == true) listCurrentId = id;
                }

                if (listDevices.Count > 0 &&
                    (devices.Count == 0 || (fallbackCurrentId is null && listCurrentId is not null) || listDevices.Count > devices.Count))
                {
                    devices = listDevices;
                    fallbackCurrentId = listCurrentId;
                }
            }
        }

        if (devices.Count == 0)
        {
            var devicesNode = await TryGetJsonAsync(baseUrl + "/AudioDevices", ct).ConfigureAwait(false);
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
        }

        var redirections = await TryGetJsonAsync(baseUrl + "/ClassicRedirections", ct).ConfigureAwait(false);
        var currentId = ExtractCurrentDeviceId(redirections) ?? fallbackCurrentId;

        return new SonarSnapshot(devices, currentId, ExtractRedirectionIds(redirections));
    }

    private static void AppendIfPhysicalRender(JsonNode? node, List<SonarDevice> sink)
    {
        if (node is not JsonObject obj) return;

        var id = GetString(obj["id"])
            ?? GetString(obj["deviceId"])
            ?? GetString(obj["device"]?["id"]);
        if (string.IsNullOrEmpty(id)) return;

        var name = GetString(obj["friendlyName"])
            ?? GetString(obj["name"])
            ?? GetString(obj["displayName"])
            ?? id;
        if (IsSonarVirtualDevice(id, name)) return;

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
        var dataFlow = GetString(obj["dataFlow"])
            ?? GetString(obj["flow"])
            ?? GetString(obj["direction"]);
        if (!string.IsNullOrEmpty(dataFlow) &&
            !dataFlow.Equals("render", StringComparison.OrdinalIgnoreCase) &&
            !dataFlow.Equals("output", StringComparison.OrdinalIgnoreCase) &&
            !dataFlow.Equals("playback", StringComparison.OrdinalIgnoreCase) &&
            dataFlow != "0")
        {
            return;
        }

        sink.Add(new SonarDevice(id, name));
    }

    private static bool IsSonarVirtualDevice(string id, string name)
    {
        return id.Contains("SteelSeries Sonar -", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SteelSeries Sonar -", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Sonar -", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? TryBool(JsonObject obj, string key)
    {
        var node = obj[key];
        if (node is null) return null;
        try { return node.GetValue<bool>(); } catch { return null; }
    }

    private static string? GetString(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<string>(); }
        catch
        {
            try { return node.GetValue<int>().ToString(); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Pulls the first running device id off /ClassicRedirections. The
    /// reverse-engineered schema returns an array of { id, deviceId,
    /// isRunning }, but we keep a small object fallback for older builds.
    /// </summary>
    private static string? ExtractCurrentDeviceId(JsonNode? redirections)
    {
        if (redirections is JsonArray arr)
        {
            string? firstDeviceId = null;
            foreach (var item in arr)
            {
                if (item is not JsonObject obj) continue;
                var deviceId = GetString(obj["deviceId"]);
                firstDeviceId ??= deviceId;
                if (TryBool(obj, "isRunning") == true && !string.IsNullOrWhiteSpace(deviceId)) return deviceId;
            }
            return firstDeviceId;
        }

        if (redirections is JsonObject redirectionObj)
        {
            foreach (var key in new[] { "media", "game", "chat", "aux" })
            {
                var node = redirectionObj[key] ?? redirectionObj["redirections"]?[key] ?? redirectionObj["classicRedirections"]?[key];
                if (node is null) continue;
                if (node is JsonObject channelObj)
                {
                    var id = GetString(channelObj["deviceId"])
                        ?? GetString(channelObj["id"])
                        ?? GetString(channelObj["device"]?["id"]);
                    if (!string.IsNullOrEmpty(id)) return id;
                }
                else
                {
                    var id = GetString(node);
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<int> ExtractRedirectionIds(JsonNode? redirections)
    {
        var ids = new List<int>();

        if (redirections is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject obj) continue;
                var idText = GetString(obj["id"]);
                if (int.TryParse(idText, out var id) && !ids.Contains(id)) ids.Add(id);
            }
        }
        else if (redirections is JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                if (int.TryParse(kvp.Key, out var id) && !ids.Contains(id)) ids.Add(id);
            }
        }

        return ids;
    }
}
