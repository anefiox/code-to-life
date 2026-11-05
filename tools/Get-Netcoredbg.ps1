<#
.SYNOPSIS
    Downloads netcoredbg, the open-source (MIT) .NET debugger from Samsung, into tools/netcoredbg.

.DESCRIPTION
    Code to Life drives netcoredbg over the Debug Adapter Protocol to step through code and light
    up the call stack on the graph. Microsoft's vsdbg can't be used: its license only allows
    Microsoft's own tools.

    Releases: https://github.com/Samsung/netcoredbg/releases

.PARAMETER Version
    A release tag such as 3.1.2-1054. Defaults to the latest release.

.PARAMETER Destination
    Where to put it. Defaults to tools/netcoredbg next to this script, which Code to Life checks.
#>
[CmdletBinding()]
param(
    [string] $Version = 'latest',
    [string] $Destination = (Join-Path $PSScriptRoot 'netcoredbg')
)

$ErrorActionPreference = 'Stop'

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($IsWindows -or $env:OS -eq 'Windows_NT') { $asset = 'netcoredbg-win64.zip' }
elseif ($IsMacOS) { $asset = 'netcoredbg-osx-amd64.tar.gz' }
elseif ($arch -eq 'Arm64') { $asset = 'netcoredbg-linux-arm64.tar.gz' }
else { $asset = 'netcoredbg-linux-amd64.tar.gz' }

$api = if ($Version -eq 'latest') { 'https://api.github.com/repos/Samsung/netcoredbg/releases/latest' }
       else { "https://api.github.com/repos/Samsung/netcoredbg/releases/tags/$Version" }

Write-Host "Looking up netcoredbg ($Version)..."
$release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'code-to-life' }
$download = $release.assets | Where-Object { $_.name -eq $asset } | Select-Object -First 1
if (-not $download) { throw "Release $($release.tag_name) has no $asset. See https://github.com/Samsung/netcoredbg/releases" }

$temp = Join-Path ([System.IO.Path]::GetTempPath()) "netcoredbg-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $archive = Join-Path $temp $asset
    Write-Host "Downloading $($download.browser_download_url) ($([math]::Round($download.size / 1MB, 1)) MB)..."
    Invoke-WebRequest -Uri $download.browser_download_url -OutFile $archive -Headers @{ 'User-Agent' = 'code-to-life' }

    $extract = Join-Path $temp 'x'
    if ($asset.EndsWith('.zip')) { Expand-Archive -Path $archive -DestinationPath $extract }
    else { New-Item -ItemType Directory -Path $extract | Out-Null; tar -xzf $archive -C $extract }

    # The archives contain a single netcoredbg/ folder.
    $inner = Join-Path $extract 'netcoredbg'
    if (-not (Test-Path $inner)) { $inner = $extract }
    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    Move-Item -Path $inner -Destination $Destination

    $exe = Join-Path $Destination ($(if ($asset.EndsWith('.zip')) { 'netcoredbg.exe' } else { 'netcoredbg' }))
    if (-not ($asset.EndsWith('.zip'))) { chmod +x $exe }
    Write-Host "Installed netcoredbg $($release.tag_name) to $exe"
    Write-Host "Code to Life finds it there automatically. Restart it if it's running."
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
