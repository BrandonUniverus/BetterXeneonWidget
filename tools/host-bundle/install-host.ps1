#Requires -Version 5.1
<#
.SYNOPSIS
  Installs BetterXeneonWidget.Host as a per-user, autostart-on-login background service.

.DESCRIPTION
  Copies the published files to %LOCALAPPDATA%\Programs\BetterXeneonWidget, registers an
  autostart entry in HKCU\Software\Microsoft\Windows\CurrentVersion\Run pointing at
  launcher.vbs (which starts the .exe with no visible console window), generates a
  trusted self-signed cert for the HTTPS callback (used by Spotify OAuth), and starts
  the service immediately.

  No admin rights required. No .NET SDK required — the cert is created via
  built-in PowerShell cmdlets. Reversible via uninstall-host.ps1.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$exeName       = 'BetterXeneonWidget.Host.exe'
$installDir    = Join-Path $env:LOCALAPPDATA 'Programs\BetterXeneonWidget'
$autostartName = 'BetterXeneonWidget'
$runKey        = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$exeSource     = Join-Path $PSScriptRoot $exeName

if (-not (Test-Path $exeSource)) {
    throw "Did not find $exeName next to install-host.ps1. Run this from the publish bundle directory."
}

Write-Host "Installing BetterXeneonWidget.Host..." -ForegroundColor Cyan

# ----- 1. Stop any running instance so we can overwrite the .exe -----
$running = Get-Process -Name 'BetterXeneonWidget.Host' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "  Stopping existing service ($($running.Count) process(es))..."
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# ----- 2. Register the betterxeneonwidget:// URI scheme handler -----
# Spotify OAuth callback redirects to betterxeneonwidget://callback, which
# Windows hands off to oauth-forward.vbs. The VBS forwards the code to the
# host over plain HTTP — no cert dance needed anywhere.
function Register-UriScheme {
    $schemeKey = 'HKCU:\Software\Classes\betterxeneonwidget'
    $forwardScript = Join-Path $installDir 'oauth-forward.vbs'

    if (-not (Test-Path $schemeKey)) {
        New-Item -Path $schemeKey -Force | Out-Null
    }
    Set-ItemProperty -Path $schemeKey -Name '(default)' -Value 'URL: BetterXeneonWidget Spotify OAuth callback'
    Set-ItemProperty -Path $schemeKey -Name 'URL Protocol' -Value ''

    $cmdKey = Join-Path $schemeKey 'shell\open\command'
    if (-not (Test-Path $cmdKey)) {
        New-Item -Path $cmdKey -Force | Out-Null
    }
    Set-ItemProperty -Path $cmdKey -Name '(default)' -Value "wscript.exe `"$forwardScript`" `"%1`""
    Write-Host "  URI handler registered: betterxeneonwidget://"
}

# ----- 3. Copy bundle files into the install directory -----
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
}

$bundleFiles = @($exeName, 'launcher.vbs', 'oauth-forward.vbs', 'uninstall-host.ps1', 'README.md')
foreach ($name in $bundleFiles) {
    $src = Join-Path $PSScriptRoot $name
    if (Test-Path $src) {
        Copy-Item -Force -Path $src -Destination $installDir | Out-Null
    }
}

# Preserve user edits to appsettings.json across reinstalls
$appsettingsSrc  = Join-Path $PSScriptRoot 'appsettings.json'
$appsettingsDest = Join-Path $installDir   'appsettings.json'
if (Test-Path $appsettingsSrc) {
    if (-not (Test-Path $appsettingsDest)) {
        Copy-Item -Force -Path $appsettingsSrc -Destination $installDir | Out-Null
    }
    else {
        Write-Host "  Preserving existing appsettings.json (delete it manually to reset to defaults)"
    }
}

# ----- 4. Register URI scheme handler (after files are in place) -----
try {
    Register-UriScheme
}
catch {
    Write-Host "  URI handler registration failed: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "  Spotify Connect will not work until this is resolved." -ForegroundColor Yellow
}

# ----- 5. Register autostart at user login (no admin needed for HKCU) -----
$launcherPath = Join-Path $installDir 'launcher.vbs'
$runValue = "wscript.exe `"$launcherPath`""
Set-ItemProperty -Path $runKey -Name $autostartName -Value $runValue
Write-Host "  Autostart registered: $runKey\$autostartName"

# ----- 6. Start the service now -----
Start-Process wscript.exe -ArgumentList "`"$launcherPath`""
Start-Sleep -Milliseconds 400

# Verify it's listening
$port = 8976
try {
    $appsettings = Get-Content $appsettingsDest -Raw -ErrorAction Stop | ConvertFrom-Json
    if ($appsettings.Listen.Port) { $port = [int]$appsettings.Listen.Port }
}
catch { }

$ok = $false
for ($i = 0; $i -lt 10; $i++) {
    try {
        $resp = Invoke-WebRequest "http://127.0.0.1:$port/api/health" -UseBasicParsing -TimeoutSec 1 -ErrorAction Stop
        if ($resp.StatusCode -eq 200) { $ok = $true; break }
    }
    catch { Start-Sleep -Milliseconds 400 }
}

Write-Host ""
if ($ok) {
    Write-Host "Installed and running." -ForegroundColor Green
    Write-Host "  Endpoint:  http://127.0.0.1:$port"
}
else {
    Write-Host "Installed, but service did not respond on port $port within 4s." -ForegroundColor Yellow
    Write-Host "  Check the log:  $env:LOCALAPPDATA\BetterXeneonWidget\host.log"
    Write-Host "  Or run the .exe directly to see startup errors:"
    Write-Host "    & '$installDir\$exeName'"
}
Write-Host "  Location:  $installDir"
Write-Host "  Logs:      $env:LOCALAPPDATA\BetterXeneonWidget\host.log"
Write-Host "  Uninstall: $installDir\uninstall-host.ps1"
