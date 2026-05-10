# BetterXeneonWidget.Host — install bundle

Self-contained companion service for the Better Audio Switcher and Better Media widgets. Required for the widgets to read/control Windows audio devices, drive media transport (SMTC), and connect to Spotify. Single loopback listener at `http://127.0.0.1:8976`.

## Install

Right-click `install-host.ps1` → **Run with PowerShell**. Or from a terminal:

```pwsh
pwsh -ExecutionPolicy Bypass -File .\install-host.ps1
```

What it does:
- Copies the `.exe` and supporting files to `%LOCALAPPDATA%\Programs\BetterXeneonWidget\`
- Registers the `betterxeneonwidget://` URI scheme handler in `HKCU\Software\Classes` (Spotify OAuth callback)
- Registers `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\BetterXeneonWidget` so the service launches at login (silently, via `launcher.vbs`)
- Starts the service now and probes `/api/health` to confirm it's listening

No admin rights required. The .NET runtime is bundled in the `.exe` — no separate install needed. No HTTPS, no certs, no dependencies on the .NET SDK.

## Uninstall

```pwsh
pwsh -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\BetterXeneonWidget\uninstall-host.ps1"
```

Stops the service, removes the autostart entry, removes the URI scheme handler, and deletes the install directory.

## Change the port

Edit `appsettings.json` next to the `.exe` (default location: `%LOCALAPPDATA%\Programs\BetterXeneonWidget\appsettings.json`):

```json
{
  "Listen": { "Port": 9876 },
  "Spotify": { "ClientId": "..." }
}
```

Then stop and relaunch the service:

```pwsh
Get-Process BetterXeneonWidget.Host | Stop-Process -Force
Start-Process wscript.exe -ArgumentList "`"$env:LOCALAPPDATA\Programs\BetterXeneonWidget\launcher.vbs`""
```

The widget hard-codes port `8976` — if you change it, update the widget's `HostClient` baseUrl too. If you change the port, you also need to edit `oauth-forward.vbs` to forward to the new port.

## Spotify integration

The OAuth callback uses a custom URI scheme — `betterxeneonwidget://callback` — registered in your user's `HKCU\Software\Classes`. When the browser hits the redirect, Windows runs `oauth-forward.vbs`, which forwards the auth code to the host over plain HTTP. No HTTPS, no cert, no browser warning.

The `Spotify.ClientId` is committed in `appsettings.json` for this repo's owner. If you fork this and use your own Spotify app, register `betterxeneonwidget://callback` as a redirect URI in the Spotify Developer Dashboard, then put your client ID in `appsettings.json`.

## Logs

The host writes a rolling log to `%LOCALAPPDATA%\BetterXeneonWidget\host.log` (1 MB cap, one previous file kept as `host.log.old`). If the widget can't reach the host, the log is the first place to look.

## Manual run / debugging

```pwsh
& "$env:LOCALAPPDATA\Programs\BetterXeneonWidget\BetterXeneonWidget.Host.exe"
```

This launches with a visible console so you can see startup errors. `Ctrl+C` to stop.
