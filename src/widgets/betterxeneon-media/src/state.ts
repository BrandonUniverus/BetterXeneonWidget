import type {
  Lyrics,
  NowPlaying,
  SpotifyPlayback,
  SpotifyPlaylist,
  SpotifyQueueItem,
} from '@betterxeneon/shared';
import { writable } from 'svelte/store';

export type ModalView = 'none' | 'queue' | 'library';

/**
 * Layout state. `browse` = the 3-row interactive layout (now-playing,
 * transport, queue/buttons). `listening` = expanded ambient view with the
 * big now-playing on the left and just the Queue/Playlists buttons on the
 * right; transport row fades out. The widget auto-switches to `listening`
 * after a configurable inactivity period (iCUE setting).
 */
export type DisplayMode = 'browse' | 'listening';

export interface AppStateValue {
  connected: boolean;
  error: string | null;
  nowPlaying: NowPlaying | null;
  systemAccentColor: string | null;

  // Spotify Web API state. The host owns the OAuth flow — the widget reads
  // status, kicks off /connect, and polls for queue/playlists once authed.
  spotifyAuthed: boolean;
  spotifyDisplayName: string | null;
  spotifyPlayback: SpotifyPlayback | null;  // /me/player — across-devices state
  spotifyQueue: SpotifyQueueItem[];
  spotifyRecentlyPlayed: SpotifyQueueItem[];
  spotifyPlaylists: SpotifyPlaylist[];
  // True between tapping "Connect" and the next status poll showing connected.
  // Used to disable the connect button so the user doesn't spawn five browser
  // windows by tap-spamming.
  spotifyConnecting: boolean;

  // Full-takeover modal — used for the scrollable Queue and Library lists
  // that the bottom-row preview links into. 'none' = compact bottom row only.
  modalView: ModalView;

  // Browse vs listening layout. Updated by the inactivity tracker.
  displayMode: DisplayMode;

  // Lyrics for the currently-displayed track (LRClib via host). null while
  // fetching or when LRClib has no entry. `lyricsForTrack` is the cache key
  // (artist|title) so we know whether the loaded lyrics still belong to the
  // current track when polling.
  lyrics: Lyrics | null;
  lyricsForTrack: string | null;

  // Time-sync support: spotifyPlayback.progressMs is sampled every poll
  // (~1.5s). For smooth lyric highlighting we record performance.now() at
  // poll-completion in `spotifyPlaybackFetchedAt` and interpolate locally.
  spotifyPlaybackFetchedAt: number;
  // Same idea, but for SMTC's timeline (NowPlaying.PositionMs). Used when
  // the source is local SMTC (not the Spotify Web API), so we can interpolate
  // playback time without burning Spotify API calls.
  nowPlayingFetchedAt: number;

  // Optimistic UI: when the user taps a transport button we toggle the
  // expected state immediately so the icon flips even though the host poll
  // hasn't returned yet. Cleared on the next successful refresh.
  optimisticPlay: boolean | null;
  optimisticUntil: number; // performance.now() timestamp; suppresses stale updates
}

const initial: AppStateValue = {
  connected: false,
  error: null,
  nowPlaying: null,
  systemAccentColor: null,
  spotifyAuthed: false,
  spotifyDisplayName: null,
  spotifyPlayback: null,
  spotifyQueue: [],
  spotifyRecentlyPlayed: [],
  spotifyPlaylists: [],
  spotifyConnecting: false,
  modalView: 'none',
  displayMode: 'browse',
  lyrics: null,
  lyricsForTrack: null,
  spotifyPlaybackFetchedAt: 0,
  nowPlayingFetchedAt: 0,
  optimisticPlay: null,
  optimisticUntil: 0,
};

export const appStore = writable<AppStateValue>(initial);
(globalThis as Record<string, unknown>).__bxm_store = appStore;
