<#
.SYNOPSIS
    Installs BwPicker into Program Files and starts it as administrator at sign-in, so it can also type
    into apps that run as administrator (Windows blocks typing from normal apps into those).

.DESCRIPTION
    - Copies BwPicker.exe to "C:\Program Files\BwPicker", where only administrators can change it.
      (Starting an exe from a user-writable folder as admin would let anything that can swap it gain admin.)
    - Starts that copy as administrator; it replaces any normal "Start with Windows" entry with a scheduled
      task ("BwPicker", run with highest privileges at sign-in), so there is no UAC prompt at each sign-in.
    Asks for administrator rights once (UAC). Use -Uninstall to remove the task and the installed copy.

.PARAMETER Source
    The BwPicker.exe to install. Defaults to the one next to this script (as in the release zip).
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot 'BwPicker.exe'),
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $arguments += '-Uninstall' } else { $arguments += @('-Source', "`"$((Resolve-Path $Source).Path)`"") }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait
    exit
}

$target = Join-Path $env:ProgramFiles 'BwPicker'
$exe = Join-Path $target 'BwPicker.exe'

Get-Process BwPicker -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    schtasks.exe /Delete /TN BwPicker /F 2>$null | Out-Null
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Write-Host 'BwPicker admin install removed. Your settings and vault are untouched.'
    exit
}

if (-not (Test-Path $Source)) { throw "BwPicker.exe not found at $Source" }
$version = (Get-Item $Source).VersionInfo.ProductVersion
New-Item -ItemType Directory -Force -Path $target | Out-Null
# BwPicker.exe plus the native libraries next to it (SkiaSharp, HarfBuzz, ANGLE).
$sourceDir = Split-Path (Resolve-Path $Source).Path
Copy-Item $Source $exe -Force
Get-ChildItem $sourceDir -Filter *.dll | Copy-Item -Destination $target -Force
Get-ChildItem $target -Include *.exe, *.dll -Recurse | Unblock-File

# BwPicker needs the official Bitwarden CLI for signing in and syncing.
$bw = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\bw.exe'
if (-not (Test-Path $bw) -and -not (Get-Command bw.exe -ErrorAction SilentlyContinue)) {
    if (Get-Command winget.exe -ErrorAction SilentlyContinue) {
        $answer = Read-Host 'The Bitwarden CLI (needed to sign in and sync) is not installed. Install it now with winget? [Y/n]'
        if ($answer -notmatch '^[Nn]') {
            winget.exe install --id Bitwarden.CLI --exact --source winget --accept-package-agreements --accept-source-agreements --silent
        }
    } else {
        Write-Host 'The Bitwarden CLI is not installed and winget is unavailable. Install it from https://bitwarden.com/help/cli/'
    }
}

# The installed copy sets up the admin startup task itself, then keeps running in the tray.
Start-Process $exe -ArgumentList '--enable-admin-autostart'
Write-Host "Installed BwPicker $version to $target and set it to start as administrator at sign-in."
