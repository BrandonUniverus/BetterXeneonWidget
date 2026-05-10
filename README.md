# BetterXeneonWidget

Better widgets for the [Corsair Xeneon Edge](https://www.corsair.com/us/en/p/xeneon-edge) touchscreen — and the small Windows companion service they need.

Two widgets ship today:

- **Better Media** — Spotify-aware media player with synced lyrics, queue + library, transport, and an "ambient" media-mode that dims the controls after idle.
- **Better Audio Switcher** — pick the default output device, set per-app outputs, control system volume.

Both run inside iCUE's widget sandbox and talk to a tiny .NET background service (`BetterXeneonWidget.Host`) that exposes the Windows things the sandbox can't reach: SMTC media control, audio-device switching, system accent color, Spotify Web API, and LRClib lyrics.

---

## Install (use it)

The single installer EXE bundles the host service + both widgets. Run it once on the PC where iCUE lives.

### 1. Get the installer

If a release is published in this repo, grab `BetterXeneonWidget-Setup.exe` from the latest [release](https://github.com/BrandonUniverus/BetterXeneonWidget/releases). Otherwise build it yourself — see [Build from source](#build-from-source) below; the result is at `dist\BetterXeneonWidget-Setup.exe`.

### 2. Run the installer

Double-click `BetterXeneonWidget-Setup.exe`. It will:

- Copy the host service to `%LOCALAPPDATA%\Programs\BetterXeneonWidget\`
- Register `betterxeneonwidget://` as a Windows URI scheme handler (used for Spotify OAuth callback — no HTTPS or cert plumbing needed)
- Add an autostart entry under `HKCU\…\Run` (no admin required)
- Start the host service immediately on `http://127.0.0.1:8976`
- Drop the two `.icuewidget` packages into a folder it opens for you — double-click each one to import it into iCUE

### 3. Add the widgets to your Edge

In iCUE, open the Xeneon Edge dashboard, pick a cell, and choose `Better Media` or `Better Audio Switcher` from the widget gallery.

### 4. (Optional) Spotify integration

The Better Media widget works with any audio source via Windows SMTC (Spotify, browsers, foobar2000, etc). If you also want **the Library / Queue / Playlists modal**, **synced lyrics** (via LRClib), and **cross-device playback control** when Spotify is playing on another device, you need a Spotify Developer Client ID:

1. Go to [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard) and create an app.
2. Set the **Redirect URI** to `betterxeneonwidget://callback`.
3. Copy the **Client ID** from the dashboard.
4. Open `%LOCALAPPDATA%\Programs\BetterXeneonWidget\appsettings.json` and set:
   ```json
   "Spotify": { "ClientId": "your-client-id-here" }
   ```
5. Restart the host (kill `BetterXeneonWidget.Host.exe` from Task Manager — it'll auto-restart on next login, or run the EXE manually).
6. In the widget, tap **Connect Spotify** and complete the browser auth.

Without a Client ID the rest of the widget still works fine; the Spotify-specific modal just shows a Connect button that's disabled.

### Uninstall

Run `uninstall-host.ps1` from `%LOCALAPPDATA%\Programs\BetterXeneonWidget\` (PowerShell, no admin). Removes the autostart entry, the URI scheme registration, and the install dir. Remove the widgets from iCUE separately.

---

## Build from source

### Prereqs

- Windows 11
- iCUE 5.44+ with a Xeneon Edge connected (only required to actually use the widgets)
- [.NET SDK 10](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) 20+ (24 recommended)

### Clone + install

```pwsh
git clone https://github.com/BrandonUniverus/BetterXeneonWidget.git
cd BetterXeneonWidget
npm install
```

### Build the installer EXE

```pwsh
node tools/build-installer.mjs
```

That runs the full pipeline:
1. `vite build` for both widgets
2. Packages each as a `.icuewidget` (zip with `manifest.json`, inlined `index.html`, icon, translations)
3. `dotnet publish` the host as a self-contained single-file Windows binary
4. Bundles host + widgets + install scripts into `dist\BetterXeneonWidget-Host.zip`
5. `dotnet publish` the installer with that zip embedded as a resource

Output: `dist\BetterXeneonWidget-Setup.exe` (~98 MB — most of that is the .NET runtime statically linked into the host service).

### Run for development

For iterating on widget code without rebuilding the EXE every time:

```pwsh
# Terminal 1 — companion host (live-reloads .cs changes)
cd src/host/BetterXeneonWidget.Host
dotnet run -c Release

# Terminal 2 — widget dev server with HMR
cd src/widgets/betterxeneon-media
npm run dev   # opens at http://localhost:5174 in your browser
```

Browser dev mimics the iCUE sandbox closely enough for most UI work. To verify behavior on the actual Edge, repackage the `.icuewidget`:

```pwsh
cd src/widgets/betterxeneon-media
npm run build
node scripts/package.mjs
# → dist/com-betterxeneon-media.icuewidget — double-click to reimport in iCUE
```

### Spotify Client ID for development

The host loads the Client ID from any of these (in order of precedence):

1. `BETTERXENEON_SPOTIFY_CLIENT_ID` environment variable
2. `appsettings.Local.json` next to `appsettings.json` (gitignored — recommended for dev)
3. `appsettings.json` (tracked — leave empty)

Example local override (won't be committed):

```json
// src/host/BetterXeneonWidget.Host/appsettings.Local.json
{ "Spotify": { "ClientId": "your-client-id-here" } }
```

---

## Layout

```
src/
  shared/                              TypeScript lib used by both widgets
  widgets/
    betterxeneon-media/                Better Media (Spotify + SMTC + lyrics)
    betterxeneon-audio-switcher/       Better Audio Switcher (output / volume)
  host/
    BetterXeneonWidget.Host/           .NET 10 background service
  installer/                           Single-EXE installer (dotnet publish + embedded zip)
tools/
  build-installer.mjs                  Top-level build script
  host-bundle/                         install-host.ps1, oauth-forward.vbs, etc.
  widgetbuilder-kit/                   Corsair's official kit + CLI installer + docs (gitignored)
```

## Why a companion host?

Widgets run inside iCUE's QtWebEngine sandbox. The widget JS API only exposes Sensors / Media / Link plugins — no audio device control, no Spotify OAuth, no album-art bytes from SMTC, no LRClib HTTP. The companion service provides those over `http://127.0.0.1:8976`, which the widget calls via `fetch`. Loopback-only.

## Non-obvious constraints (gotchas)

These bit the project enough times to be worth flagging:

1. **All JS must be inlined in `index.html`.** QtWebEngine blocks `<script src="...">` for widgets loaded from file://. Vite is configured with `vite-plugin-singlefile` so the entire bundle lands inside the HTML.
2. **`icueEvents` must be assigned with bare `=`, never `var`/`let`/`const`.** The runtime bridge can't see lexically-scoped declarations.
3. **Aspect ratio, not width.** Xeneon Edge has Small horizontal (840×344) and Medium horizontal (840×696) sharing width 840 but with very different shapes. The "wide-strip" listening layout switches at aspect ≥ 2:1, not at a width breakpoint.
4. **SMTC `Position` is whatever the source last reported.** Spotify only updates on play/pause/seek/track-change, so the raw value can be 3+ seconds stale. The host extrapolates `Position += (now - LastUpdatedTime)` while playing to give an accurate current-time value.
5. **Spotify rate-limits hard.** `/me/playlists` 429s within minutes if polled aggressively. The host honors `Retry-After`, persists the cooldown to disk, and serves cached data through the lockout.

## License

[MIT](LICENSE) — Copyright © 2026 Brandon Bachmeier
