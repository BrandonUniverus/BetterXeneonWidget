using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

// Alias `System` namespaces we need into something that doesn't collide with
// the project's own `BetterXeneonWidget.Host.System` namespace.
using SysNet = global::System.Net;

namespace BetterXeneonWidget.Host.Spotify;

public sealed class SpotifyOptions
{
    /// <summary>
    /// From the Spotify Developer Dashboard. Set in appsettings.json or the
    /// BETTERXENEON_SPOTIFY_CLIENT_ID env var. Public client (no secret) — we
    /// use Authorization Code with PKCE so no secret is required.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>
/// Spotify Web API client + OAuth 2.0 Authorization Code with PKCE flow.
///
/// PKCE = no client secret needed. We generate a random code_verifier per
/// connection attempt, hash it for the auth URL, then send the verifier with
/// the token exchange to prove we initiated the request. Standard practice
/// for desktop and mobile apps.
///
/// Tokens are persisted across restarts via SpotifyTokenStore. Access tokens
/// auto-refresh on demand (~1h lifetime); refresh tokens are long-lived.
/// </summary>
public sealed class SpotifyService
{
    private const string AuthUrl = "https://accounts.spotify.com/authorize";
    private const string TokenUrl = "https://accounts.spotify.com/api/token";
    private const string ApiBase = "https://api.spotify.com/v1";
    // Custom URI scheme — registered by the installer. Spotify allows custom
    // schemes for desktop apps as of 2025 (and prefers them over HTTPS-on-
    // loopback because there's no cert-trust dance). When the browser hits
    // this URL, Windows launches oauth-forward.vbs which posts the code back
    // to /api/spotify/callback over plain HTTP/8976.
    private const string RedirectUri = "betterxeneonwidget://callback";

    // Scopes:
    //   user-read-playback-state          → /me/player/queue
    //   user-read-recently-played         → /me/player/recently-played
    //   user-modify-playback-state        → start playback (tap playlist/queue item)
    //   playlist-read-private             → /me/playlists (private)
    //   playlist-read-collaborative       → /me/playlists (collab)
    //   user-read-private                 → /me (display name + verify auth on connect)
    private const string Scopes =
        "user-read-playback-state user-read-recently-played user-modify-playback-state " +
        "playlist-read-private playlist-read-collaborative user-read-private";

    private readonly SpotifyTokenStore _store;
    private readonly HttpClient _http;
    private readonly string _clientId;

    private readonly object _pendingLock = new();
    private string? _pendingVerifier;
    private string? _pendingState;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public SpotifyService(
        SpotifyTokenStore store,
        IHttpClientFactory httpFactory,
        IOptions<SpotifyOptions> options)
    {
        _store = store;
        _http = httpFactory.CreateClient("spotify");

        // Env var beats appsettings — easier for dev without editing JSON.
        _clientId = Environment.GetEnvironmentVariable("BETTERXENEON_SPOTIFY_CLIENT_ID")
            ?? options.Value.ClientId;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_clientId);

    /// <summary>
    /// Builds the auth URL with PKCE, opens the user's default browser there,
    /// and stashes the verifier+state until the callback comes back.
    /// </summary>
    public bool BeginAuth()
    {
        if (!IsConfigured) return false;

        var verifier = GenerateRandomToken(32);
        var challenge = ComputePkceChallenge(verifier);
        var state = GenerateRandomToken(16);

        lock (_pendingLock)
        {
            _pendingVerifier = verifier;
            _pendingState = state;
        }

        var url =
            $"{AuthUrl}?client_id={Uri.EscapeDataString(_clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scopes)}" +
            $"&code_challenge_method=S256" +
            $"&code_challenge={challenge}" +
            $"&state={state}";

        try
        {
            // ShellExecute hands the URL to the user's default browser, which
            // opens at its last-used position (typically not the touchscreen).
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Exchanges the auth code for tokens, persists them, and fetches /me to
    /// cache the display name. Returns false on any failure (state mismatch,
    /// network error, Spotify rejection).
    /// </summary>
    public async Task<bool> HandleCallbackAsync(string code, string state)
    {
        string? verifier;
        lock (_pendingLock)
        {
            if (_pendingState != state) return false;
            verifier = _pendingVerifier;
            _pendingVerifier = null;
            _pendingState = null;
        }
        if (verifier is null) return false;

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("code_verifier", verifier),
        });

        TokenResponse? token;
        try
        {
            var response = await _http.PostAsync(TokenUrl, form);
            if (!response.IsSuccessStatusCode) return false;
            token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        }
        catch
        {
            return false;
        }

        if (token is null || string.IsNullOrEmpty(token.AccessToken)) return false;

        var displayName = await TryGetDisplayNameAsync(token.AccessToken);

        _store.Save(new SpotifyTokens(
            AccessToken: token.AccessToken,
            RefreshToken: token.RefreshToken ?? string.Empty,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn - 60),
            Scope: token.Scope ?? Scopes,
            DisplayName: displayName));

        return true;
    }

    public SpotifyStatusDto GetStatus()
    {
        var tokens = _store.Read();
        if (tokens is null) return new SpotifyStatusDto(false, null);
        return new SpotifyStatusDto(true, tokens.DisplayName);
    }

    // Same Retry-After cooldown pattern as playlists, but for /me/player.
    // Without this, a 429 on this endpoint perpetuates because we re-poll
    // every 1.5s — Spotify keeps the rolling window open and the entire
    // account stays locked.
    private DateTimeOffset _playbackBlockedUntil = DateTimeOffset.MinValue;

    public async Task<SpotifyPlaybackDto> GetPlaybackAsync()
    {
        if (DateTimeOffset.UtcNow < _playbackBlockedUntil) return EmptyPlayback;

        var token = await GetValidAccessTokenAsync();
        if (token is null) return EmptyPlayback;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/me/player");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(req);

            // 204 = no active device; 200 = playing/paused with state in body.
            if (res.StatusCode == SysNet.HttpStatusCode.NoContent) return EmptyPlayback;
            if (res.StatusCode == SysNet.HttpStatusCode.TooManyRequests)
            {
                ApplyPlaybackBackoff(res);
                return EmptyPlayback;
            }
            if (!res.IsSuccessStatusCode) return EmptyPlayback;

            var body = await res.Content.ReadFromJsonAsync<SpotifyPlayerResponse>();
            if (body?.Item is null) return EmptyPlayback;

            return new SpotifyPlaybackDto(
                HasSession: true,
                IsPlaying: body.IsPlaying,
                TrackId: body.Item.Id ?? string.Empty,
                Title: body.Item.Name,
                Artist: body.Item.Artists is { Count: > 0 }
                    ? string.Join(", ", body.Item.Artists.Select(a => a.Name))
                    : string.Empty,
                Album: body.Item.Album?.Name ?? string.Empty,
                AlbumArtUrl: PickImage(body.Item.Album?.Images),
                DeviceName: body.Device?.Name ?? string.Empty,
                DeviceType: body.Device?.Type ?? string.Empty,
                ProgressMs: body.ProgressMs ?? 0,
                DurationMs: body.Item.DurationMs ?? 0);
        }
        catch
        {
            return EmptyPlayback;
        }
    }

    private static readonly SpotifyPlaybackDto EmptyPlayback =
        new(HasSession: false, IsPlaying: false, TrackId: "", Title: "", Artist: "", Album: "",
            AlbumArtUrl: null, DeviceName: "", DeviceType: "", ProgressMs: 0, DurationMs: 0);

    public Task<bool> PlaybackResumeAsync()   => TransportAsync("play",     HttpMethod.Put);
    public Task<bool> PlaybackPauseAsync()    => TransportAsync("pause",    HttpMethod.Put);
    public Task<bool> PlaybackNextAsync()     => TransportAsync("next",     HttpMethod.Post);
    public Task<bool> PlaybackPreviousAsync() => TransportAsync("previous", HttpMethod.Post);

    private async Task<bool> TransportAsync(string action, HttpMethod method)
    {
        var token = await GetValidAccessTokenAsync();
        if (token is null) return false;
        try
        {
            using var req = new HttpRequestMessage(method, $"{ApiBase}/me/player/{action}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // PUT /me/player/{play,pause} expects a Content-Length header even
            // when there's no body (Spotify's gateway 411s otherwise). An empty
            // JSON body with the right Content-Type satisfies it.
            if (method == HttpMethod.Put)
            {
                req.Content = new StringContent("", Encoding.UTF8, "application/json");
            }
            using var res = await _http.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Disconnect() => _store.Clear();

    /// <summary>
    /// Lists every Spotify Connect device Spotify's cloud has registered for
    /// the current account. Diagnostic — when /me/player returns no session,
    /// this tells us whether the desktop client is even known to the cloud.
    /// </summary>
    public async Task<IReadOnlyList<SpotifyDeviceDto>> GetDevicesAsync()
    {
        var token = await GetValidAccessTokenAsync();
        if (token is null) return Array.Empty<SpotifyDeviceDto>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/me/player/devices");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return Array.Empty<SpotifyDeviceDto>();
            await using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("devices", out var devicesEl)
                || devicesEl.ValueKind != JsonValueKind.Array) return Array.Empty<SpotifyDeviceDto>();
            var result = new List<SpotifyDeviceDto>(devicesEl.GetArrayLength());
            foreach (var d in devicesEl.EnumerateArray())
            {
                var id = d.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var name = d.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var type = d.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "" : "";
                var active = d.TryGetProperty("is_active", out var aEl) && aEl.ValueKind == JsonValueKind.True;
                var restricted = d.TryGetProperty("is_restricted", out var rEl) && rEl.ValueKind == JsonValueKind.True;
                result.Add(new SpotifyDeviceDto(id ?? "", name, type, active, restricted));
            }
            return result;
        }
        catch
        {
            return Array.Empty<SpotifyDeviceDto>();
        }
    }

    /// <summary>
    /// Force-activates a Spotify Connect device by transferring playback to
    /// it. Used to wake up the desktop client when it's playing locally but
    /// not currently the cloud-visible active device. play=false matches
    /// Spotify's "keep current state" semantics so we don't accidentally
    /// start playing when the user had paused.
    /// </summary>
    public async Task<bool> TransferPlaybackAsync(string deviceId, bool play = false)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var token = await GetValidAccessTokenAsync();
        if (token is null) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/me/player");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = JsonSerializer.Serialize(new { device_ids = new[] { deviceId }, play });
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<SpotifyQueueItemDto>> GetQueueAsync()
    {
        if (DateTimeOffset.UtcNow < _playbackBlockedUntil) return Array.Empty<SpotifyQueueItemDto>();

        var token = await GetValidAccessTokenAsync();
        if (token is null) return Array.Empty<SpotifyQueueItemDto>();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/me/player/queue");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(req);
            if (res.StatusCode == SysNet.HttpStatusCode.TooManyRequests)
            {
                ApplyPlaybackBackoff(res);
                return Array.Empty<SpotifyQueueItemDto>();
            }
            if (!res.IsSuccessStatusCode) return Array.Empty<SpotifyQueueItemDto>();
            var body = await res.Content.ReadFromJsonAsync<SpotifyQueueResponse>();
            if (body?.Queue is null) return Array.Empty<SpotifyQueueItemDto>();

            return body.Queue.Select(MapTrack).ToArray();
        }
        catch
        {
            return Array.Empty<SpotifyQueueItemDto>();
        }
    }

    private void ApplyPlaybackBackoff(HttpResponseMessage res)
    {
        var retry = res.Headers.RetryAfter?.Delta
                  ?? (res.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                  ?? DefaultRateLimitBackoff;
        if (retry < TimeSpan.FromSeconds(15)) retry = TimeSpan.FromSeconds(15);
        _playbackBlockedUntil = DateTimeOffset.UtcNow + retry + TimeSpan.FromMinutes(1);
    }

    public async Task<IReadOnlyList<SpotifyQueueItemDto>> GetRecentlyPlayedAsync(int limit = 20)
    {
        if (DateTimeOffset.UtcNow < _playbackBlockedUntil) return Array.Empty<SpotifyQueueItemDto>();

        var token = await GetValidAccessTokenAsync();
        if (token is null) return Array.Empty<SpotifyQueueItemDto>();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/me/player/recently-played?limit={limit}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(req);
            if (res.StatusCode == SysNet.HttpStatusCode.TooManyRequests)
            {
                ApplyPlaybackBackoff(res);
                return Array.Empty<SpotifyQueueItemDto>();
            }
            if (!res.IsSuccessStatusCode) return Array.Empty<SpotifyQueueItemDto>();
            var body = await res.Content.ReadFromJsonAsync<SpotifyRecentlyPlayedResponse>();
            if (body?.Items is null) return Array.Empty<SpotifyQueueItemDto>();

            // Recently-played returns the same track many times when looping —
            // dedupe by track id, keeping the first (most recent) occurrence.
            var seen = new HashSet<string>();
            var result = new List<SpotifyQueueItemDto>(body.Items.Count);
            foreach (var item in body.Items)
            {
                if (item.Track is null) continue;
                var id = item.Track.Id;
                if (id is not null && !seen.Add(id)) continue;
                result.Add(MapTrack(item.Track));
            }
            return result;
        }
        catch
        {
            return Array.Empty<SpotifyQueueItemDto>();
        }
    }

    /// <summary>
    /// Plays a track. If Spotify already has an active playback context
    /// (a playlist, album, or artist page is queued), we use that context
    /// + an offset so the queue continues normally after the chosen track.
    /// Falls back to a single-URI play if no context is currently active.
    /// </summary>
    public async Task<bool> PlayTrackAsync(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return false;
        var token = await GetValidAccessTokenAsync();
        if (token is null) return false;

        // Probe current context first.
        string? contextUri = null;
        try
        {
            using var stateReq = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/me/player");
            stateReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var stateRes = await _http.SendAsync(stateReq);
            if (stateRes.IsSuccessStatusCode)
            {
                await using var stream = await stateRes.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (doc.RootElement.TryGetProperty("context", out var ctxEl)
                    && ctxEl.ValueKind == JsonValueKind.Object
                    && ctxEl.TryGetProperty("uri", out var uriEl)
                    && uriEl.ValueKind == JsonValueKind.String)
                {
                    contextUri = uriEl.GetString();
                }
            }
        }
        catch
        {
            /* fall through to no-context play */
        }

        string body = !string.IsNullOrEmpty(contextUri)
            ? JsonSerializer.Serialize(new
              {
                  context_uri = contextUri,
                  offset = new { uri = $"spotify:track:{trackId}" },
              })
            : JsonSerializer.Serialize(new
              {
                  uris = new[] { $"spotify:track:{trackId}" },
              });
        return await PlayCoreAsync(body);
    }

    /// <summary>
    /// Starts playback of a playlist from its first track. Spotify will then
    /// continue through the playlist normally as tracks finish.
    /// </summary>
    public async Task<bool> PlayPlaylistAsync(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return false;
        var body = JsonSerializer.Serialize(new { context_uri = $"spotify:playlist:{playlistId}" });
        return await PlayCoreAsync(body);
    }

    private async Task<bool> PlayCoreAsync(string jsonBody)
    {
        var token = await GetValidAccessTokenAsync();
        if (token is null) return false;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/me/player/play");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            // 204 = success; 404 = no active device; 403 = restriction (e.g. free tier)
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Playlists rarely change and Spotify rate-limits this endpoint *hard*.
    // Three protections layered together:
    //   1. Disk-persisted cache (survives host restart so we don't burn the
    //      rate limit re-fetching the same data after every reinstall).
    //   2. 30-min in-memory TTL — playlists genuinely don't change song-to-
    //      song, so refreshing every couple of seconds is pure waste.
    //   3. 429 cool-down: when Spotify returns Retry-After we honor it and
    //      don't even attempt the API call again until that window passes.
    //      Without this, a wedged limit kept respawning failed requests
    //      forever, perpetuating the 429.
    private List<SpotifyPlaylistDto> _cachedPlaylists = new();
    private DateTimeOffset _cachedPlaylistsAt = DateTimeOffset.MinValue;
    private DateTimeOffset _playlistsBlockedUntil = DateTimeOffset.MinValue;
    private static readonly TimeSpan PlaylistsCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromMinutes(2);
    private string? _playlistsCachePath;
    private bool _playlistsCacheLoaded;

    private string PlaylistsCachePath => _playlistsCachePath ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BetterXeneonWidget",
        "playlists-cache.json");

    private void LoadPlaylistsCacheIfNeeded()
    {
        if (_playlistsCacheLoaded) return;
        _playlistsCacheLoaded = true;
        try
        {
            if (!File.Exists(PlaylistsCachePath)) return;
            var json = File.ReadAllText(PlaylistsCachePath);
            var snap = JsonSerializer.Deserialize<PlaylistsCacheSnapshot>(json);
            if (snap is null) return;
            if (snap.Items is { Count: > 0 })
            {
                _cachedPlaylists = snap.Items;
                _cachedPlaylistsAt = snap.SavedAt;
            }
            // Restore the rate-limit window. Without this, restarting the
            // host immediately after Spotify ban-hammered us would re-fire
            // the call and extend the lockout.
            _playlistsBlockedUntil = snap.BlockedUntil;
        }
        catch { /* best effort */ }
    }

    private void SavePlaylistsCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlaylistsCachePath)!);
            var snap = new PlaylistsCacheSnapshot
            {
                Items = _cachedPlaylists,
                SavedAt = _cachedPlaylistsAt,
                BlockedUntil = _playlistsBlockedUntil,
            };
            File.WriteAllText(PlaylistsCachePath, JsonSerializer.Serialize(snap));
        }
        catch { /* best effort */ }
    }

    private sealed class PlaylistsCacheSnapshot
    {
        public List<SpotifyPlaylistDto> Items { get; set; } = new();
        public DateTimeOffset SavedAt { get; set; }
        public DateTimeOffset BlockedUntil { get; set; }
    }

    public async Task<IReadOnlyList<SpotifyPlaylistDto>> GetPlaylistsAsync()
    {
        LoadPlaylistsCacheIfNeeded();

        var now = DateTimeOffset.UtcNow;

        // Two reasons not to hit the API:
        //   - Hot cache (within TTL) — serve immediately
        //   - Spotify cool-down active — serve whatever we have, even if
        //     past TTL. Spotify's Retry-After can be hours; re-fetching
        //     during the window just extends the lockout.
        bool freshEnough = _cachedPlaylists.Count > 0
            && now - _cachedPlaylistsAt < PlaylistsCacheLifetime;
        bool inCooldown = now < _playlistsBlockedUntil;
        if (freshEnough || inCooldown)
        {
            return _cachedPlaylists;
        }

        var token = await GetValidAccessTokenAsync();
        if (token is null) return _cachedPlaylists;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/me/playlists?limit=20");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(req);

            if (res.StatusCode == SysNet.HttpStatusCode.TooManyRequests)
            {
                // Spotify sends Retry-After (seconds, sometimes hours when
                // they really want us to back off). Honor it + 1 min pad so
                // we don't fire the call right as the window closes.
                var retry = res.Headers.RetryAfter?.Delta
                          ?? (res.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                          ?? DefaultRateLimitBackoff;
                if (retry < TimeSpan.FromSeconds(15)) retry = TimeSpan.FromSeconds(15);
                _playlistsBlockedUntil = DateTimeOffset.UtcNow + retry + TimeSpan.FromMinutes(1);
                SavePlaylistsCache(); // persist the cool-down window too
                return _cachedPlaylists;
            }

            // Any other non-success: short backoff so we don't hammer.
            if (!res.IsSuccessStatusCode)
            {
                _playlistsBlockedUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                return _cachedPlaylists;
            }

            await using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                return _cachedPlaylists;

            var result = new List<SpotifyPlaylistDto>(itemsEl.GetArrayLength());
            foreach (var item in itemsEl.EnumerateArray())
            {
                // Spotify can include null entries for deleted/unavailable
                // playlists. Skip cleanly so a single bad entry doesn't drop
                // the whole list to empty.
                if (item.ValueKind != JsonValueKind.Object) continue;

                try
                {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;

                // Spotify renamed `tracks` → `items` for the per-playlist
                // count reference at some point. Try both for robustness.
                int trackCount = 0;
                JsonElement countObj = default;
                bool found = (item.TryGetProperty("items", out countObj)
                              && countObj.ValueKind == JsonValueKind.Object)
                          || (item.TryGetProperty("tracks", out countObj)
                              && countObj.ValueKind == JsonValueKind.Object);
                if (found
                    && countObj.TryGetProperty("total", out var totalEl)
                    && totalEl.TryGetInt32(out var t))
                {
                    trackCount = t;
                }

                string? imageUrl = null;
                if (item.TryGetProperty("images", out var imgsEl) && imgsEl.ValueKind == JsonValueKind.Array)
                {
                    var images = new List<SpotifyImage>();
                    foreach (var img in imgsEl.EnumerateArray())
                    {
                        var url = img.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        int? w = img.TryGetProperty("width", out var wEl) && wEl.ValueKind == JsonValueKind.Number ? wEl.GetInt32() : null;
                        int? h = img.TryGetProperty("height", out var hEl) && hEl.ValueKind == JsonValueKind.Number ? hEl.GetInt32() : null;
                        if (!string.IsNullOrEmpty(url)) images.Add(new SpotifyImage(url, w, h));
                    }
                    imageUrl = PickImage(images);
                }

                result.Add(new SpotifyPlaylistDto(id, name, imageUrl, trackCount));
                }
                catch
                {
                    // Single bad entry — skip and keep going.
                }
            }

            // Only commit to cache if we actually got something. Empty
            // results from a successful 200 are unusual (account with no
            // playlists), but we'd rather keep the previous list than
            // overwrite it with empty.
            if (result.Count > 0)
            {
                _cachedPlaylists = result;
                _cachedPlaylistsAt = DateTimeOffset.UtcNow;
                SavePlaylistsCache();
            }
            return result.Count > 0 ? result : _cachedPlaylists;
        }
        catch
        {
            // Network error — short backoff (different signal than 429).
            _playlistsBlockedUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            return _cachedPlaylists;
        }
    }

    // --- internals ---------------------------------------------------------

    private static SpotifyQueueItemDto MapTrack(SpotifyTrack t) => new(
        Id: t.Id ?? Guid.NewGuid().ToString("n"),
        Title: t.Name,
        Artist: t.Artists is { Count: > 0 } ? string.Join(", ", t.Artists.Select(a => a.Name)) : string.Empty,
        AlbumArtUrl: PickImage(t.Album?.Images));

    private static string? PickImage(List<SpotifyImage>? images)
    {
        if (images is null || images.Count == 0) return null;
        // Prefer ~64-300 px thumbnails to keep widget render cheap. Spotify
        // returns largest first; pick the smallest >= 64 or the smallest overall.
        var smallEnough = images
            .Where(i => i.Width.HasValue && i.Width >= 64 && i.Width <= 300)
            .OrderBy(i => i.Width)
            .FirstOrDefault();
        return smallEnough?.Url ?? images.LastOrDefault()?.Url;
    }

    private async Task<string?> TryGetDisplayNameAsync(string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            var me = await res.Content.ReadFromJsonAsync<SpotifyMe>();
            return me?.DisplayName;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetValidAccessTokenAsync()
    {
        var tokens = _store.Read();
        if (tokens is null) return null;
        if (DateTimeOffset.UtcNow < tokens.ExpiresAtUtc) return tokens.AccessToken;

        // Token expired — serialize refreshes so concurrent callers don't
        // burn through the refresh-token rate limit racing each other.
        await _refreshLock.WaitAsync();
        try
        {
            tokens = _store.Read();
            if (tokens is null) return null;
            if (DateTimeOffset.UtcNow < tokens.ExpiresAtUtc) return tokens.AccessToken;

            var refreshed = await RefreshAsync(tokens);
            return refreshed?.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<SpotifyTokens?> RefreshAsync(SpotifyTokens tokens)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken)) return null;

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", tokens.RefreshToken),
            new KeyValuePair<string, string>("client_id", _clientId),
        });

        TokenResponse? token;
        try
        {
            var response = await _http.PostAsync(TokenUrl, form);
            if (!response.IsSuccessStatusCode)
            {
                // 400/401 here means the refresh token is dead — purge so the
                // widget surfaces a "reconnect" affordance instead of looping.
                if ((int)response.StatusCode is 400 or 401) _store.Clear();
                return null;
            }
            token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        }
        catch
        {
            return null;
        }

        if (token is null || string.IsNullOrEmpty(token.AccessToken)) return null;

        var next = new SpotifyTokens(
            AccessToken: token.AccessToken,
            // Spotify only sends a new refresh token sometimes; reuse the old
            // one when it doesn't (their docs explicitly call this out).
            RefreshToken: string.IsNullOrEmpty(token.RefreshToken) ? tokens.RefreshToken : token.RefreshToken,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn - 60),
            Scope: token.Scope ?? tokens.Scope,
            DisplayName: tokens.DisplayName);

        _store.Save(next);
        return next;
    }

    private static string GenerateRandomToken(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64UrlEncode(bytes);
    }

    private static string ComputePkceChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
