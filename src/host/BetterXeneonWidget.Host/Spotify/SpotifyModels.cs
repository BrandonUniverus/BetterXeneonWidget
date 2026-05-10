using System.Text.Json.Serialization;

namespace BetterXeneonWidget.Host.Spotify;

/// <summary>
/// Persisted to %APPDATA%\BetterXeneonWidget\spotify.json. AccessToken is
/// short-lived (1 hour); RefreshToken is long-lived and what survives across
/// host restarts. ExpiresAtUtc has a 60s safety buffer baked in.
/// </summary>
public sealed record SpotifyTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string Scope,
    string? DisplayName);

public sealed record SpotifyStatusDto(bool Connected, string? DisplayName);

/// <summary>
/// Snapshot of `GET /me/player` — what the user's Spotify account is doing
/// across all their devices. The widget reconciles this against SMTC (which
/// only sees local audio) so it can show "Playing on [Device]" when audio
/// is happening on a non-local device.
/// </summary>
public sealed record SpotifyPlaybackDto(
    bool HasSession,        // false = no active device anywhere on the account
    bool IsPlaying,
    string TrackId,
    string Title,
    string Artist,
    string Album,
    string? AlbumArtUrl,
    string DeviceName,      // e.g. "DESKTOP-XYZ", "John's iPhone"
    string DeviceType,      // "Computer", "Smartphone", "Speaker", "TV", ...
    long ProgressMs,        // Current playback position from /me/player.progress_ms
    long DurationMs);       // Track length, for progress bar / "is at end" checks

public sealed record SpotifyQueueItemDto(
    string Id,
    string Title,
    string Artist,
    string? AlbumArtUrl);

public sealed record SpotifyPlaylistDto(
    string Id,
    string Name,
    string? ImageUrl,
    int TrackCount);

// --- Spotify Web API responses (camelCase JSON via System.Text.Json) ---

internal sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("token_type")] string? TokenType);

internal sealed record SpotifyMe(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string? DisplayName);

internal sealed record SpotifyImage(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("width")] int? Width,
    [property: JsonPropertyName("height")] int? Height);

internal sealed record SpotifyArtist(
    [property: JsonPropertyName("name")] string Name);

internal sealed record SpotifyAlbum(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("images")] List<SpotifyImage>? Images);

internal sealed record SpotifyTrack(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("artists")] List<SpotifyArtist>? Artists,
    [property: JsonPropertyName("album")] SpotifyAlbum? Album,
    [property: JsonPropertyName("duration_ms")] long? DurationMs);

internal sealed record SpotifyQueueResponse(
    [property: JsonPropertyName("currently_playing")] SpotifyTrack? CurrentlyPlaying,
    [property: JsonPropertyName("queue")] List<SpotifyTrack>? Queue);

// /me/playlists is parsed manually via JsonDocument in SpotifyService —
// Spotify renamed the per-playlist count reference from `tracks` to `items`
// and we want to handle both. Keeping the parsing inline there means there's
// only one place to update if Spotify renames again.

internal sealed record SpotifyRecentlyPlayedItem(
    [property: JsonPropertyName("track")] SpotifyTrack? Track,
    [property: JsonPropertyName("played_at")] string? PlayedAt);

internal sealed record SpotifyRecentlyPlayedResponse(
    [property: JsonPropertyName("items")] List<SpotifyRecentlyPlayedItem>? Items);

internal sealed record SpotifyPlayerResponse(
    [property: JsonPropertyName("device")] SpotifyDevice? Device,
    [property: JsonPropertyName("is_playing")] bool IsPlaying,
    [property: JsonPropertyName("item")] SpotifyTrack? Item,
    [property: JsonPropertyName("progress_ms")] long? ProgressMs);

internal sealed record SpotifyDevice(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("is_active")] bool IsActive);
