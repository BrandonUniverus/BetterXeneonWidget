namespace BetterXeneonWidget.Host.Media;

/// <summary>
/// Snapshot of Windows' current SMTC session. Mirrors what the Win11 media
/// flyout shows — title/artist plus playback state and source app.
/// </summary>
public sealed record NowPlayingDto(
    bool HasSession,
    string Title,
    string Artist,
    string Album,
    string Status,        // "Playing" | "Paused" | "Stopped" | "Closed" | "Opened" | "Changing"
    string SourceAppId,   // AUMID, e.g. "Spotify.exe" or "Microsoft.ZuneMusic_..."
    bool IsSpotify,       // Convenience flag the widget uses to gate Spotify-only UI
    bool HasArt,          // True when album-art bytes are available at /album-art
    long ArtVersion,      // Bumps each time art bytes change; use as cache-buster
    bool CanPlay,
    bool CanPause,
    bool CanGoNext,
    bool CanGoPrevious,
    // SMTC timeline — Position/Duration in milliseconds. Lets the widget
    // interpolate playback progress locally for any SMTC source (Spotify
    // included), avoiding a Spotify Web API call per second for the lyrics
    // highlighter and progress display. Both are 0 if SMTC didn't expose
    // a timeline (rare — most apps publish it).
    long PositionMs,
    long DurationMs);

public sealed record MediaCommandResult(bool Ok, string? Error);
