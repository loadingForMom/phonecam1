# PhoneCam MF Virtual Camera - uninstall/unregister
# Run this script in an elevated PowerShell (Run as Administrator).

param(
    [ValidateSet('Release','Debug')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Ensure-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
        throw 'This script must be run as Administrator.'
    }
}

function Find-Artifact([string]$Root, [string]$FilterName) {
    $items = Get-ChildItem -Path $Root -Recurse -Filter $FilterName -ErrorAction SilentlyContinue
    if (-not $items) { return $null }
    return $items | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

Ensure-Admin

$repo = $PSScriptRoot

$sourceBin = Join-Path $repo 'PhoneCam.VirtualCam.MF.Source'
$driverBin = Join-Path $repo 'PhoneCam.VirtualCam.MF.Driver'

$sourceDll = Find-Artifact (Join-Path $sourceBin $Configuration) 'PhoneCam.VirtualCam.MF.Source.dll'
if (-not $sourceDll) {
    $sourceDll = Find-Artifact $sourceBin 'PhoneCam.VirtualCam.MF.Source.dll'
}

$driverExe = Find-Artifact (Join-Path $driverBin $Configuration) 'PhoneCam.VirtualCam.MF.Driver.exe'
if (-not $driverExe) {
    $driverExe = Find-Artifact $driverBin 'PhoneCam.VirtualCam.MF.Driver.exe'
}

if ($driverExe) {
    Write-Host "Unregistering MF virtual camera..." -ForegroundColor Cyan
    & $driverExe.FullName unregister | Out-Host
} else {
    Write-Warning "Driver EXE not found; skipping virtual camera unregister."
}

if ($sourceDll) {
    Write-Host "Unregistering COM media source via regsvr32: $($sourceDll.FullName)" -ForegroundColor Cyan
    & regsvr32 /s /u $sourceDll.FullName
} else {
    Write-Warning "Source DLL not found; skipping regsvr32 /u."
}

Write-Host "OK: PhoneCam MF virtual camera unregistered." -ForegroundColor Green
