# Build mvxxcb-restrain as a self-contained single-file exe and package it.
# Requires .NET 8 SDK. No-admin install (per-user):
#   irm https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
#   .\dotnet-install.ps1 -Channel 8.0   # installs to %LOCALAPPDATA%\Microsoft\dotnet
param([string]$Version = '0.3.0')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = @('D:\dev\dotnet\dotnet.exe', (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dotnet) { $dotnet = 'dotnet' }

& $dotnet publish (Join-Path $repoRoot 'src\mvxxcb-restrain') `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o (Join-Path $repoRoot 'dist')
if ($LASTEXITCODE -ne 0) { throw "publish failed: $LASTEXITCODE" }

$exe = Join-Path $repoRoot 'dist\mvxxcb-restrain.exe'
if (-not (Test-Path $exe)) { throw "expected exe missing: $exe" }

# portable package: exe + installer script + readme
$pkgDir = Join-Path $repoRoot "dist\package"
New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null
Copy-Item $exe $pkgDir -Force
Copy-Item (Join-Path $repoRoot 'install.ps1') $pkgDir -Force
Copy-Item (Join-Path $repoRoot 'README.md') $pkgDir -Force
$zip = Join-Path $repoRoot "dist\mvxxcb-restrain-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pkgDir '*') -DestinationPath $zip
Remove-Item $pkgDir -Recurse -Force

Write-Host "exe: $exe"
Write-Host "zip: $zip"
Get-Item $exe, $zip | Select-Object Name, Length, LastWriteTime
