using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BetterXeneonWidget.Host.Lyrics;

/// <summary>
/// Fetches lyrics from LRClib (https://lrclib.net) — a free, community-curated
/// lyrics database with no auth and decent coverage of popular tracks. Returns
/// both plain and time-synced (LRC format) when available.
///
/// Spotify's official Web API does NOT expose lyrics anymore — they removed
/// the endpoint a few years back. LRClib is the practical free fallback.
///
/// Results are cached in-memory by (artist|title) so we hammer LRClib at most
/// once per track.
/// </summary>
public sealed class LyricsService
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LyricsDto> _cache = new();

    public LyricsService(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("lyrics");
        _http.Timeout = TimeSpan.FromSeconds(8);
    }

    public async Task<LyricsDto> GetAsync(string artist, string title, string? album, int? durationSec)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return Empty;

        var key = $"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            if (!string.IsNullOrEmpty(album)) url += $"&album_name={Uri.EscapeDataString(album)}";
            if (durationSec.HasValue && durationSec.Value > 0) url += $"&duration={durationSec.Value}";

            using var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode)
            {
                // 404 = no lyrics for this track. Cache so we don't keep
                // pinging LRClib for an obscure song every poll cycle.
                var miss = new LyricsDto(false, null, null);
                _cache[key] = miss;
                return miss;
            }

            var body = await res.Content.ReadFromJsonAsync<LrcLibResponse>();
            var hasPlain = !string.IsNullOrWhiteSpace(body?.PlainLyrics);
            var hasSynced = !string.IsNullOrWhiteSpace(body?.SyncedLyrics);
            if (!hasPlain && !hasSynced)
            {
                var empty = new LyricsDto(false, null, null);
                _cache[key] = empty;
                return empty;
            }

            var dto = new LyricsDto(true, body!.PlainLyrics, body.SyncedLyrics);
            _cache[key] = dto;
            return dto;
        }
        catch
        {
            // Don't cache transient failures — try again next time.
            return Empty;
        }
    }

    private static readonly LyricsDto Empty = new(false, null, null);
}

public sealed record LyricsDto(bool Found, string? PlainLyrics, string? SyncedLyrics);

internal sealed record LrcLibResponse(
    [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
    [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);
