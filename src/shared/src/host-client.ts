export interface AudioDevice {
  id: string;
  name: string;
  isDefault: boolean;
  volume: number;
  muted: boolean;
}

export interface AudioSession {
  id: string;
  deviceId: string;
  deviceName: string;
  processId: number;
  processName: string;
  displayName: string;
  state: string;
  volume: number;
  muted: boolean;
  /**
   * Real-time peak amplitude (0..1) from WASAPI's per-session meter.
   * Non-zero means this session has produced sound in the last audio
   * buffer. Widget uses this to sort "currently making sound" first
   * and to dim rows that haven't produced sound recently.
   */
  peak: number;
}

export interface VolumeState {
  level: number;
  muted: boolean;
}

/**
 * Snapshot of SteelSeries Sonar's "ALL OUTPUT DEVICES" panel — which
 * physical devices Sonar knows about and which one is currently selected
 * as Sonar's master output. `available: false` means the host couldn't
 * reach Sonar (GG not running, port discovery failed, etc.); the rest of
 * the fields will be null/empty in that case.
 */
export interface SteelSeriesStatus {
  available: boolean;
  currentDeviceName: string | null;
  currentDeviceId: string | null;
  devices: SteelSeriesDevice[];
}

export interface SteelSeriesDevice {
  id: string;
  name: string;
  isCurrent: boolean;
}

export interface SteelSeriesSwapResult {
  ok: boolean;
  newDeviceName: string | null;
  newDeviceId: string | null;
  error: string | null;
}

export interface AccentColor {
  hex: string;
}

export interface SpotifyStatus {
  connected: boolean;
  displayName: string | null;
}

/**
 * Snapshot of /me/player — what the user's Spotify account is doing on
 * whichever device is currently active. Distinct from the SMTC now-playing
 * which only reflects this PC. The widget reconciles the two so it can show
 * "playing on [Device]" when the audio isn't local.
 */
export interface SpotifyPlayback {
  hasSession: boolean;
  isPlaying: boolean;
  trackId: string;
  title: string;
  artist: string;
  album: string;
  albumArtUrl: string | null;
  deviceName: string;
  deviceType: string;
  progressMs: number;
  durationMs: number;
}

export interface SpotifyQueueItem {
  id: string;
  title: string;
  artist: string;
  albumArtUrl: string | null;
}

export interface SpotifyPlaylist {
  id: string;
  name: string;
  imageUrl: string | null;
  trackCount: number;
}

/**
 * Track lyrics from LRClib via the host. `found` is false when LRClib has
 * no entry; `plainLyrics` is line-separated text; `syncedLyrics` is LRC
 * format ([mm:ss.xx]Line) when available, otherwise null.
 */
export interface Lyrics {
  found: boolean;
  plainLyrics: string | null;
  syncedLyrics: string | null;
}

/**
 * Snapshot of Windows' SMTC current session — title/artist/album plus
 * playback state and the source app so the widget can decide whether to
 * surface Spotify-only quadrants.
 */
export interface NowPlaying {
  hasSession: boolean;
  title: string;
  artist: string;
  album: string;
  status: string;            // "Playing" | "Paused" | "Stopped" | "Closed" | "Opened" | "Changing"
  sourceAppId: string;       // AUMID, e.g. "Spotify.exe"
  isSpotify: boolean;
  hasArt: boolean;
  artVersion: number;
  canPlay: boolean;
  canPause: boolean;
  canGoNext: boolean;
  canGoPrevious: boolean;
  // SMTC timeline (milliseconds). 0/0 if the source app didn't publish one.
  // Sampled per host poll; the widget interpolates locally between samples.
  positionMs: number;
  durationMs: number;
}

export interface HostClientOptions {
  baseUrl?: string;
  fetchImpl?: typeof fetch;
}

const DEFAULT_BASE_URL = 'http://127.0.0.1:8976';

export class HostClient {
  private readonly baseUrl: string;
  private readonly fetchImpl: typeof fetch;

  constructor(options: HostClientOptions = {}) {
    this.baseUrl = (options.baseUrl ?? DEFAULT_BASE_URL).replace(/\/$/, '');
    this.fetchImpl = options.fetchImpl ?? fetch.bind(globalThis);
  }

  async health(): Promise<boolean> {
    try {
      const res = await this.fetchImpl(`${this.baseUrl}/api/health`);
      return res.ok;
    } catch {
      return false;
    }
  }

  // ---------- Devices ----------

  async listAudioDevices(): Promise<AudioDevice[]> {
    return this.json<AudioDevice[]>('/api/audio/devices');
  }

  async setDefaultAudioDevice(id: string): Promise<void> {
    await this.send('/api/audio/default', { id });
  }

  async setDeviceVolume(id: string, level: number): Promise<void> {
    await this.send('/api/audio/devices/volume', { id, level });
  }

  async setDeviceMute(id: string, muted: boolean): Promise<void> {
    await this.send('/api/audio/devices/mute', { id, muted });
  }

  /**
   * Returns the URL the host serves the Windows-extracted icon PNG at, for a
   * given device id. The image element using this src will get a 404 if the
   * device has no icon — handle via onerror.
   */
  iconUrlForDevice(id: string): string {
    return `${this.baseUrl}/api/audio/devices/icon?id=${encodeURIComponent(id)}`;
  }

  /**
   * URL for the icon of the process producing this audio session — extracted
   * from the .exe via Win32 ExtractIconEx. 404 when the session has no PID
   * (system sounds), the process has exited, or the .exe has no icon.
   */
  iconUrlForSession(id: string): string {
    return `${this.baseUrl}/api/audio/sessions/icon?id=${encodeURIComponent(id)}`;
  }

  // ---------- Config (host-side preferences) ----------

  async getConfig(): Promise<{ pinnedIds: string[]; initialized: boolean }> {
    return this.json<{ pinnedIds: string[]; initialized: boolean }>('/api/config/');
  }

  async setPinnedIds(pinnedIds: string[]): Promise<void> {
    await this.send('/api/config/pins', { pinnedIds });
  }

  /**
   * Returns the shared widget-settings JSON object — the same blob the media
   * widget uses to persist its theme/options. Untyped because both widgets
   * push arbitrary keys into it; callers should pick out the keys they care
   * about and tolerate missing or unexpected values.
   */
  async getWidgetSettings(): Promise<Record<string, unknown>> {
    return this.json<Record<string, unknown>>('/api/widget/settings');
  }

  // ---------- Sessions (apps mixer) ----------

  async listSessions(): Promise<AudioSession[]> {
    return this.json<AudioSession[]>('/api/audio/sessions');
  }

  async setSessionVolume(id: string, level: number): Promise<void> {
    await this.send('/api/audio/sessions/volume', { id, level });
  }

  async setSessionMute(id: string, muted: boolean): Promise<void> {
    await this.send('/api/audio/sessions/mute', { id, muted });
  }

  // ---------- SteelSeries Sonar (output device cycling) ----------

  async getSteelSeriesStatus(): Promise<SteelSeriesStatus> {
    return this.json<SteelSeriesStatus>('/api/audio/steelseries/status');
  }

  /**
   * Cycles Sonar's master output to the next physical device. Resolves with
   * the new device on success; throws on transport errors. A 502 response
   * (Sonar unreachable, or some channel PUT failed mid-swap) is surfaced as
   * an Error with the body message.
   */
  async swapSteelSeriesOutput(): Promise<SteelSeriesSwapResult> {
    const res = await this.fetchImpl(`${this.baseUrl}/api/audio/steelseries/swap`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{}',
    });
    const body = (await res.json()) as SteelSeriesSwapResult;
    if (!res.ok && !body.error) throw new Error(`/api/audio/steelseries/swap → ${res.status}`);
    return body;
  }

  // ---------- System ----------

  async getAccentColor(): Promise<AccentColor> {
    return this.json<AccentColor>('/api/system/accent-color');
  }

  // ---------- Media (SMTC) ----------

  async getNowPlaying(): Promise<NowPlaying> {
    return this.json<NowPlaying>('/api/media/now-playing');
  }

  async mediaPlay(): Promise<void> { await this.send('/api/media/play', {}); }
  async mediaPause(): Promise<void> { await this.send('/api/media/pause', {}); }
  async mediaToggle(): Promise<void> { await this.send('/api/media/toggle', {}); }
  async mediaNext(): Promise<void> { await this.send('/api/media/next', {}); }
  async mediaPrevious(): Promise<void> { await this.send('/api/media/previous', {}); }

  /**
   * URL for the current track's album art. The host caches bytes per-track
   * and returns 404 when none are available — bind onerror to hide the img.
   * Pass the latest `artVersion` from getNowPlaying() as a cache-buster.
   */
  albumArtUrl(version: number): string {
    return `${this.baseUrl}/api/media/album-art?v=${version}`;
  }

  // ---------- Spotify (Web API via host-side OAuth) ----------

  /** Triggers OAuth in the user's default browser. */
  async spotifyConnect(): Promise<void> { await this.send('/api/spotify/connect', {}); }
  async spotifyDisconnect(): Promise<void> { await this.send('/api/spotify/disconnect', {}); }

  async getSpotifyStatus(): Promise<SpotifyStatus> {
    return this.json<SpotifyStatus>('/api/spotify/status');
  }

  async getSpotifyQueue(): Promise<SpotifyQueueItem[]> {
    return this.json<SpotifyQueueItem[]>('/api/spotify/queue');
  }

  async getSpotifyRecentlyPlayed(): Promise<SpotifyQueueItem[]> {
    return this.json<SpotifyQueueItem[]>('/api/spotify/recently-played');
  }

  async getSpotifyPlaylists(): Promise<SpotifyPlaylist[]> {
    return this.json<SpotifyPlaylist[]>('/api/spotify/playlists');
  }

  /** Plays a single Spotify track. Replaces the current playback context. */
  async playSpotifyTrack(trackId: string): Promise<void> {
    await this.send(`/api/spotify/play/track/${encodeURIComponent(trackId)}`, {});
  }

  /** Plays a Spotify playlist from the start. */
  async playSpotifyPlaylist(playlistId: string): Promise<void> {
    await this.send(`/api/spotify/play/playlist/${encodeURIComponent(playlistId)}`, {});
  }

  async getSpotifyPlayback(): Promise<SpotifyPlayback> {
    return this.json<SpotifyPlayback>('/api/spotify/playback');
  }

  // Transport via Spotify Web API — affects whichever device is currently
  // active on the user's account. Used when the active source is Spotify
  // (local or remote); SMTC transport is used for non-Spotify sources.
  async spotifyResume(): Promise<void>   { await this.send('/api/spotify/playback/play', {}); }
  async spotifyPause(): Promise<void>    { await this.send('/api/spotify/playback/pause', {}); }
  async spotifyNext(): Promise<void>     { await this.send('/api/spotify/playback/next', {}); }
  async spotifyPrevious(): Promise<void> { await this.send('/api/spotify/playback/previous', {}); }

  // ---------- Lyrics (LRClib via host) ----------

  async getLyrics(artist: string, title: string, album?: string): Promise<Lyrics> {
    const params = new URLSearchParams({ artist, title });
    if (album) params.set('album', album);
    return this.json<Lyrics>(`/api/lyrics?${params.toString()}`);
  }

  // ---------- internals ----------

  private async json<T>(path: string): Promise<T> {
    const res = await this.fetchImpl(`${this.baseUrl}${path}`);
    if (!res.ok) throw new Error(`${path} → ${res.status} ${res.statusText}`);
    return (await res.json()) as T;
  }

  private async send(path: string, body: unknown): Promise<void> {
    const res = await this.fetchImpl(`${this.baseUrl}${path}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`${path} → ${res.status} ${res.statusText}`);
  }
}
