# BetterXeneonWidget

Better widgets for the Corsair Xeneon Edge. Each widget is a standalone `.icuewidget` package; the optional `betterxeneon-loader` widget hosts the others (and any 3rd-party `.icuewidget`) inside a single canvas with preset switching.

## Layout

```
src/
  shared/                              # TypeScript lib reused by every widget
  widgets/
    betterxeneon-audio-switcher/       # Phase 1 — output device picker + volume
    betterxeneon-spotify/              # Phase 2 — playlists / library / queue
    betterxeneon-presets/              # Phase 3 — preset rail UI
    betterxeneon-loader/               # Phase 3 — the host widget
  host/
    BetterXeneonWidget.Host/           # .NET 10 companion (Kestrel @ 127.0.0.1:8976)
tools/
  widgetbuilder-kit/                   # Corsair's official kit + CLI installer + docs
```

## Prereqs

- Windows 11
- iCUE 5.44+ with a Xeneon Edge connected
- .NET SDK 10
- Node 20+ (24 recommended)
- `icuewidget` CLI installed from `tools/widgetbuilder-kit/icuewidget-0.2.3-windows-installer.exe`

## Run the dev loop (audio-switcher)

```pwsh
# 1. Companion host (terminal 1)
npm run host:run

# 2. Build the widget single-file bundle (terminal 2)
npm run build:audio-switcher

# 3. Package as .icuewidget
npm run package:audio-switcher

# 4. Install: double-click dist/betterxeneon-audio-switcher.icuewidget
#    (iCUE prompts to import; widget appears in the Edge widget picker)
```

## Publish the host for your actual machine

The dev `dotnet run` only works on a machine with the .NET 10 SDK. To install on your normal PC, build a self-contained, single-file Windows binary:

```pwsh
npm run publish:host
```

That produces:
- `dist/host/`                          — folder with the `.exe` + install scripts
- `dist/BetterXeneonWidget-Host.zip`    — the same, zipped for transfer

On the target PC: extract the zip somewhere, then run `install-host.ps1`. It copies the `.exe` to `%LOCALAPPDATA%\Programs\BetterXeneonWidget\`, registers an autostart entry under `HKCU\…\Run` (no admin needed), and starts the service silently. Uninstall via the matching `uninstall-host.ps1` in the install dir. Full notes: [tools/host-bundle/README.md](tools/host-bundle/README.md).

## Why a companion host?

Widgets run inside iCUE's QtWebEngine sandbox. The widget JS API only exposes Sensors / Media / Link plugins — no audio device control, no Spotify OAuth, no `.icuewidget` filesystem. The companion service provides those over `http://127.0.0.1:8976`, which the widget calls via `fetch`. Loopback-only, no auth in dev (Phase 1).

## Constraints to keep in mind

`tools/widgetbuilder-kit/WidgetBuilder/` is the source of truth for everything iCUE-side. The two non-obvious rules that bite you:

1. **All JS must be inlined in `index.html`.** QtWebEngine blocks `<script src="...">` for widgets loaded from file://. Vite is configured with `vite-plugin-singlefile` so the entire bundle ends up inside the HTML.
2. **`icueEvents` must be assigned with bare `=`, never `var`/`let`/`const`.** The runtime bridge can't see lexically-scoped declarations.
