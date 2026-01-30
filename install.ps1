# PhoneCam Virtual Camera (DirectShow) - install/register
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
if (-not $filterDll) {
    throw "Filter DLL not found. Build PhoneCam.VirtualCam.Filter ($Configuration|x64) first. Searched: $filterBin"
}

$registerExe = Find-Artifact (Join-Path $registerBin $Configuration) 'PhoneCam.VirtualCam.RegisterTool.exe'
if (-not $registerExe) {
    # SDK-style projects often output into bin\<Config>\net48
    $registerExe = Find-Artifact $registerBin 'PhoneCam.VirtualCam.RegisterTool.exe'
}
if (-not $registerExe) {
    throw "RegisterTool EXE not found. Build PhoneCam.VirtualCam.RegisterTool ($Configuration|x64) first. Searched: $registerBin"
}

$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if (-not (Test-Path $regasm)) {
    throw "RegAsm not found at: $regasm"
}

$tlb = Join-Path $filterDll.Directory.FullName 'PhoneCam.VirtualCam.Filter.tlb'

Write-Host "Registering COM class via RegAsm: $($filterDll.FullName)" -ForegroundColor Cyan
& $regasm $filterDll.FullName /codebase /tlb:$tlb /nologo | Out-Host

Write-Host "Registering in DirectShow Video Capture Sources category..." -ForegroundColor Cyan
& $registerExe.FullName register | Out-Host

Write-Host "OK: PhoneCam Virtual Camera registered." -ForegroundColor Green
Write-Host "Tip: use GraphStudioNext -> Graph -> Insert Filter -> 'PhoneCam Virtual Camera'" -ForegroundColor Gray
