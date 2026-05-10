import { tick } from 'svelte';
import { getIcueProperty } from '@betterxeneon/shared';
import { host } from './api.ts';
import { appStore } from './state.ts';

let timer: ReturnType<typeof setTimeout> | null = null;
let stopped = false;
const STARTED_KEY = '__bxm_polling_started__';

export function startPolling(): void {
  const g = globalThis as Record<string, unknown>;
  if (g[STARTED_KEY]) return;
  g[STARTED_KEY] = true;
  stopped = false;
  void poll();
}

export function stopPolling(): void {
  stopped = true;
  (globalThis as Record<string, unknown>)[STARTED_KEY] = false;
  if (timer) clearTimeout(timer);
  timer = null;
}

// Spotify queue/playlist polling cadence is decoupled from now-playing.
// Calling /me/playlists every 10s gets us 429-rate-limited within minutes.
// Track-change is the real trigger anyway — we refresh on:
//   - first poll after auth flips to connected
//   - every Nth now-playing tick (~60s with default 1500ms cadence)
//   - immediately when the current Spotify track changes (covers the
//     "user skipped, picked a queue item, or started a playlist" cases)
// Playlists are additionally cached server-side for 30 minutes so the host
// never hits Spotify more often than that even if we ask repeatedly.
const SPOTIFY_REFRESH_EVERY_N_TICKS = 40;
// Playlists DON'T change song-to-song, so don't bundle them with the
// per-track refresh — that's what 429-locked us. Refresh on a much longer
// cadence and on demand when the Library modal opens.
const PLAYLISTS_REFRESH_EVERY_N_TICKS = 400; // ~10 min at 1500ms
let tickCounter = 0;
let lastSeenTrackId: string | null = null;

async function poll(): Promise<void> {
  if (stopped) return;
  try {
    // Two cheap host calls run in parallel: SMTC now-playing and Spotify
    // status. When authed, we also fetch /me/player so the widget can show
    // the across-devices Spotify state alongside the local SMTC view.
    const [nowPlaying, spotifyStatus] = await Promise.all([
      host.getNowPlaying(),
      host.getSpotifyStatus(),
    ]);

    const playback = spotifyStatus.connected ? await host.getSpotifyPlayback() : null;
    const playbackFetchedAt = performance.now();
    const nowPlayingFetchedAt = performance.now();

    let prevAuthed = false;
    appStore.update(s => {
      prevAuthed = s.spotifyAuthed;
      const now = performance.now();
      const expectedStatus = s.optimisticPlay === true ? 'Playing' : s.optimisticPlay === false ? 'Paused' : null;
      const matched = expectedStatus !== null && nowPlaying.status === expectedStatus;
      const expired = s.optimisticUntil !== 0 && now >= s.optimisticUntil;
      const next: typeof s = {
        ...s,
        connected: true,
        error: null,
        nowPlaying,
        nowPlayingFetchedAt,
        spotifyAuthed: spotifyStatus.connected,
        spotifyDisplayName: spotifyStatus.displayName,
        spotifyPlayback: playback,
        spotifyPlaybackFetchedAt: playback ? playbackFetchedAt : 0,
        // Once status flips to connected, drop the connecting flag so the
        // CTA returns to a non-spinner state.
        spotifyConnecting: spotifyStatus.connected ? false : s.spotifyConnecting,
      };
      if (matched || expired) {
        next.optimisticPlay = null;
        next.optimisticUntil = 0;
      }
      return next;
    });

    // Refresh queue + playlists whenever we're authed, on these triggers:
    //   - first poll after auth flips to connected
    //   - the periodic 10s tick
    //   - track ID change (user just tapped Next, queue picker, etc.)
    // Previously this also gated on "Spotify is the active source" — that
    // meant playlists never loaded if the user opened the Library modal
    // while nothing was playing, even though they're authed.
    const justAuthed = !prevAuthed && spotifyStatus.connected;
    const dueForRefresh = tickCounter % SPOTIFY_REFRESH_EVERY_N_TICKS === 0;
    const currentTrackId = playback?.trackId ?? null;
    const trackChanged = currentTrackId !== null && currentTrackId !== lastSeenTrackId;
    if (currentTrackId !== null) lastSeenTrackId = currentTrackId;

    if (spotifyStatus.connected && (justAuthed || dueForRefresh || trackChanged)) {
      void refreshSpotifyData();
    }

    // Playlists: cheap to ask the host (it caches), but every "miss" past
    // the host cache hits Spotify and risks 429. Keep this on its own slow
    // schedule, and only on first auth + every PLAYLISTS_REFRESH_EVERY_N_TICKS.
    const playlistsDue = tickCounter % PLAYLISTS_REFRESH_EVERY_N_TICKS === 0;
    if (spotifyStatus.connected && (justAuthed || playlistsDue)) {
      void refreshSpotifyPlaylists();
    }

    // Lyrics — only refetch when the displayed track changes. Uses Spotify
    // Web API track when active, otherwise falls back to SMTC's title/artist.
    const dispArtist = playback?.hasSession ? playback.artist : nowPlaying.artist;
    const dispTitle = playback?.hasSession ? playback.title : nowPlaying.title;
    const dispAlbum = playback?.hasSession ? playback.album : nowPlaying.album;
    if (dispArtist && dispTitle) {
      const lyricsKey = `${dispArtist}|${dispTitle}`;
      let prevLyricsKey: string | null = null;
      appStore.subscribe(s => { prevLyricsKey = s.lyricsForTrack; })();
      if (lyricsKey !== prevLyricsKey) {
        void refreshLyrics(dispArtist, dispTitle, dispAlbum, lyricsKey);
      }
    }

    tickCounter++;

    await tick();
  } catch (e) {
    appStore.update(s => ({
      ...s,
      connected: false,
      error: e instanceof Error ? e.message : String(e),
    }));
  } finally {
    if (!stopped) {
      const interval = clampInterval(getIcueProperty<number>('pollIntervalMs') ?? 1500);
      timer = setTimeout(() => void poll(), interval);
    }
  }
}

async function refreshSpotifyData(): Promise<void> {
  try {
    const [queue, recentlyPlayed] = await Promise.all([
      host.getSpotifyQueue(),
      host.getSpotifyRecentlyPlayed(),
    ]);
    appStore.update(s => ({
      ...s,
      spotifyQueue: queue,
      spotifyRecentlyPlayed: recentlyPlayed,
    }));
  } catch {
    /* surfaced via banner via the next now-playing call if the host is gone */
  }
}

async function refreshSpotifyPlaylists(): Promise<void> {
  try {
    const playlists = await host.getSpotifyPlaylists();
    appStore.update(s => ({ ...s, spotifyPlaylists: playlists }));
  } catch { /* host returns last-cached on error; ignore */ }
}

/**
 * Force a Spotify refresh now (queue + recently-played). Called when the
 * user opens a modal so they don't have to stare at "Loading…" waiting for
 * the next polling tick. Playlists are NOT included here — they're refreshed
 * by `refreshPlaylistsNow()` which the Library modal opens explicitly.
 */
export function refreshSpotifyNow(): void {
  void refreshSpotifyData();
}

/**
 * Force a playlists refresh now. Called by the Library modal so the user
 * sees the freshest list (subject to the host's 30-min cache + 429 backoff).
 */
export function refreshPlaylistsNow(): void {
  void refreshSpotifyPlaylists();
}

async function refreshLyrics(artist: string, title: string, album: string, key: string): Promise<void> {
  // Mark the request immediately so a subsequent poll on the same track
  // doesn't fire a duplicate fetch while this one is in flight.
  appStore.update(s => ({ ...s, lyricsForTrack: key, lyrics: null }));
  try {
    const lyrics = await host.getLyrics(artist, title, album || undefined);
    // Only commit if the track is still current — user may have skipped
    // mid-fetch.
    appStore.update(s => s.lyricsForTrack === key ? { ...s, lyrics } : s);
  } catch {
    appStore.update(s => s.lyricsForTrack === key ? { ...s, lyrics: null } : s);
  }
}

function clampInterval(value: unknown): number {
  const n = typeof value === 'number' ? value : Number(value);
  if (!Number.isFinite(n)) return 1500;
  return Math.max(500, Math.min(5000, Math.round(n)));
}
