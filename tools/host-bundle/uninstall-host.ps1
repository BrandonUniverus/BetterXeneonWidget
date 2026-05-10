#Requires -Version 5.1
<#
.SYNOPSIS
  Uninstalls BetterXeneonWidget.Host.

.DESCRIPTION
  Stops the running service, removes the HKCU\Run autostart entry, removes the
  betterxeneonwidget:// URI scheme handler, and deletes the install directory.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$installDir    = Join-Path $env:LOCALAPPDATA 'Programs\BetterXeneonWidget'
$autostartName = 'BetterXeneonWidget'
$runKey        = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$schemeKey     = 'HKCU:\Software\Classes\betterxeneonwidget'

Write-Host "Uninstalling BetterXeneonWidget.Host..." -ForegroundColor Cyan

# Stop running instances
$running = Get-Process -Name 'BetterXeneonWidget.Host' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "  Stopping $($running.Count) process(es)..."
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# Remove autostart
$existing = Get-ItemProperty -Path $runKey -Name $autostartName -ErrorAction SilentlyContinue
if ($existing) {
    Remove-ItemProperty -Path $runKey -Name $autostartName
    Write-Host "  Removed autostart: $runKey\$autostartName"
}

# Remove URI scheme handler
if (Test-Path $schemeKey) {
    Remove-Item -Path $schemeKey -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Removed URI handler: $schemeKey"
}

# Remove install dir (allow self-removal — we may be running from inside it)
if (Test-Path $installDir) {
    # Move ourselves to temp first if running from inside the install dir, so the dir can be removed.
    $running = $false
    try {
        $myPath = $MyInvocation.MyCommand.Path
        if ($myPath -and $myPath.StartsWith($installDir, [System.StringComparison]::OrdinalIgnoreCase)) {
            $running = $true
        }
    } catch { }

    if ($running) {
        # Schedule removal via a detached cmd.exe so we don't fight the lock
        $cmd = "ping 127.0.0.1 -n 2 > nul & rmdir /S /Q `"$installDir`""
        Start-Process cmd.exe -ArgumentList @('/C', $cmd) -WindowStyle Hidden
        Write-Host "  Scheduled removal of $installDir (we are running from inside it)"
    } else {
        Remove-Item -Recurse -Force -Path $installDir
        Write-Host "  Removed: $installDir"
    }
}

Write-Host "Done." -ForegroundColor Green
