using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace BetterXeneonWidget.Host.Media;

/// <summary>
/// Wraps Windows' System Media Transport Controls (SMTC). Anything that
/// publishes media to SMTC — Spotify, browsers, foobar2000, etc. — shows up
/// here, and we can drive transport (play/pause/next/previous) without
/// needing per-app integration.
///
/// The manager hands out the *current* session — the one Windows considers
/// foreground media. We snapshot it on each request rather than keeping a
/// long-lived subscription; the widget polls.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // Album-art cache. Keyed by source-app id; bytes stay valid until either
    // the title changes (track change) or another app takes over the session.
    private readonly object _artLock = new();
    private byte[]? _artBytes;
    private string _artKey = string.Empty;
    private long _artVersion;

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync()
    {
        if (_manager != null) return _manager;
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch
        {
            return null;
        }
        finally
        {
            _initLock.Release();
        }
        return _manager;
    }

    public async Task<NowPlayingDto> GetNowPlayingAsync()
    {
        var manager = await GetManagerAsync().ConfigureAwait(false);
        var session = manager?.GetCurrentSession();
        if (session is null) return Empty();

        GlobalSystemMediaTransportControlsSessionMediaProperties? props;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? info;
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
            info = session.GetPlaybackInfo();
            timeline = session.GetTimelineProperties();
        }
        catch
        {
            return Empty();
        }

        if (props is null) return Empty();

        // SMTC's `Position` is whatever the source app last reported — for
        // Spotify that's only on play/pause/seek/track-change, NOT continuously.
        // `LastUpdatedTime` says WHEN it was reported. So Position alone can be
        // 3+ seconds stale ("play" was reported 3s ago, position is 3s behind).
        // Extrapolate Position forward by (now - LastUpdatedTime) while playing
        // to give the widget an accurate "current" position. Without this the
        // lyrics highlighter is always behind by network + sample-age.
        var sourceId = session.SourceAppUserModelId ?? string.Empty;
        var isSpotify = sourceId.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0;
        var status = info?.PlaybackStatus.ToString() ?? "Unknown";

        long posMs = 0, durMs = 0;
        if (timeline is not null)
        {
            posMs = (long)timeline.Position.TotalMilliseconds;
            durMs = (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds;
            if (posMs < 0) posMs = 0;
            if (durMs < 0) durMs = 0;

            if (status == "Playing")
            {
                var ageMs = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalMilliseconds;
                // Sanity-bound: never extrapolate by more than 2 minutes (the
                // source has clearly desynced) or use a negative value
                // (clock skew). Cap at duration too — Spotify often leaves
                // LastUpdatedTime stale across track boundaries.
                if (ageMs > 0 && ageMs < 120_000)
                {
                    posMs += (long)ageMs;
                    if (durMs > 0 && posMs > durMs) posMs = durMs;
                }
            }
        }
        var controls = info?.Controls;

        var title = props.Title ?? string.Empty;
        var artist = props.Artist ?? string.Empty;
        var album = props.AlbumTitle ?? string.Empty;

        await UpdateArtCacheAsync(sourceId, title, props.Thumbnail).ConfigureAwait(false);

        var hasArt = false;
        long artVer;
        lock (_artLock)
        {
            hasArt = _artBytes is { Length: > 0 } && _artKey == BuildArtKey(sourceId, title);
            artVer = _artVersion;
        }

        return new NowPlayingDto(
            HasSession: true,
            Title: title,
            Artist: artist,
            Album: album,
            Status: status,
            SourceAppId: sourceId,
            IsSpotify: isSpotify,
            HasArt: hasArt,
            ArtVersion: artVer,
            CanPlay: controls?.IsPlayEnabled ?? false,
            CanPause: controls?.IsPauseEnabled ?? false,
            CanGoNext: controls?.IsNextEnabled ?? false,
            CanGoPrevious: controls?.IsPreviousEnabled ?? false,
            PositionMs: posMs,
            DurationMs: durMs);
    }

    /// <summary>
    /// Diagnostic snapshot of the raw SMTC timeline. Returned by
    /// /api/media/timeline-debug for verifying the position-extrapolation logic.
    /// All times in milliseconds; ageMs = how old the source-reported sample is.
    /// </summary>
    public sealed record TimelineDebugDto(
        bool HasSession,
        string Status,
        long RawPositionMs,
        long DurationMs,
        long LastUpdatedAgeMs,
        long ExtrapolatedPositionMs,
        DateTimeOffset NowUtc,
        DateTimeOffset LastUpdatedUtc);

    public async Task<TimelineDebugDto?> GetTimelineDebugAsync()
    {
        var manager = await GetManagerAsync().ConfigureAwait(false);
        var session = manager?.GetCurrentSession();
        if (session is null) return null;
        var info = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        if (timeline is null) return null;
        var status = info?.PlaybackStatus.ToString() ?? "Unknown";
        var rawPos = (long)timeline.Position.TotalMilliseconds;
        var dur = (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds;
        var ageMs = (long)(DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalMilliseconds;
        var extrap = rawPos;
        if (status == "Playing" && ageMs > 0 && ageMs < 120_000)
        {
            extrap += ageMs;
            if (dur > 0 && extrap > dur) extrap = dur;
        }
        return new TimelineDebugDto(true, status, rawPos, dur, ageMs, extrap,
            DateTimeOffset.UtcNow, timeline.LastUpdatedTime);
    }

    public Task<bool> PlayAsync() => RunAsync(s => s.TryPlayAsync().AsTask());
    public Task<bool> PauseAsync() => RunAsync(s => s.TryPauseAsync().AsTask());
    public Task<bool> TogglePlayPauseAsync() => RunAsync(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task<bool> NextAsync() => RunAsync(s => s.TrySkipNextAsync().AsTask());
    public Task<bool> PreviousAsync() => RunAsync(s => s.TrySkipPreviousAsync().AsTask());

    public byte[]? GetArtBytes(out long version)
    {
        lock (_artLock)
        {
            version = _artVersion;
            return _artBytes;
        }
    }

    private async Task<bool> RunAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> op)
    {
        var manager = await GetManagerAsync().ConfigureAwait(false);
        var session = manager?.GetCurrentSession();
        if (session is null) return false;
        try
        {
            return await op(session).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildArtKey(string source, string title) => $"{source}|{title}";

    private async Task UpdateArtCacheAsync(string source, string title, IRandomAccessStreamReference? thumbRef)
    {
        var key = BuildArtKey(source, title);
        bool needsRefresh;
        lock (_artLock)
        {
            needsRefresh = key != _artKey;
        }
        if (!needsRefresh) return;

        byte[]? bytes = null;
        if (thumbRef is not null)
        {
            try
            {
                using var stream = await thumbRef.OpenReadAsync();
                if (stream is { Size: > 0 })
                {
                    var buffer = new byte[checked((int)stream.Size)];
                    var ibuf = buffer.AsBuffer();
                    await stream.ReadAsync(ibuf, ibuf.Capacity, InputStreamOptions.None);
                    bytes = buffer;
                }
            }
            catch
            {
                bytes = null;
            }
        }

        lock (_artLock)
        {
            _artBytes = bytes;
            _artKey = key;
            _artVersion++;
        }
    }

    private static NowPlayingDto Empty() => new(
        HasSession: false,
        Title: string.Empty,
        Artist: string.Empty,
        Album: string.Empty,
        Status: "Closed",
        SourceAppId: string.Empty,
        IsSpotify: false,
        HasArt: false,
        ArtVersion: 0,
        CanPlay: false,
        CanPause: false,
        CanGoNext: false,
        CanGoPrevious: false,
        PositionMs: 0,
        DurationMs: 0);
}
