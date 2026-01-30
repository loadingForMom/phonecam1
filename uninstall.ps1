# PhoneCam Virtual Camera (DirectShow) - uninstall/unregister
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

$filterBin = Join-Path $repo 'PhoneCam.VirtualCam.Filter\bin'
$registerBin = Join-Path $repo 'PhoneCam.VirtualCam.RegisterTool\bin'

$filterDll = Find-Artifact (Join-Path $filterBin $Configuration) 'PhoneCam.VirtualCam.Filter.dll'
$registerExe = Find-Artifact (Join-Path $registerBin $Configuration) 'PhoneCam.VirtualCam.RegisterTool.exe'
if (-not $registerExe) {
    $registerExe = Find-Artifact $registerBin 'PhoneCam.VirtualCam.RegisterTool.exe'
}

$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if (-not (Test-Path $regasm)) {
    throw "RegAsm not found at: $regasm"
}

if ($registerExe) {
    try {
        Write-Host "Unregistering from DirectShow Video Capture Sources category..." -ForegroundColor Cyan
        & $registerExe.FullName unregister | Out-Host
    } catch {
        Write-Warning "RegisterTool failed: $_"
    }
} else {
    Write-Warning "RegisterTool not found; skipping FilterMapper2 unregister"
}

if ($filterDll) {
    try {
        Write-Host "Unregistering COM class via RegAsm: $($filterDll.FullName)" -ForegroundColor Cyan
        & $regasm $filterDll.FullName /u /nologo | Out-Host
    } catch {
        Write-Warning "RegAsm /u failed: $_"
    }
} else {
    Write-Warning "Filter DLL not found; skipping RegAsm /u"
}

Write-Host "OK: PhoneCam Virtual Camera unregistered." -ForegroundColor Green
