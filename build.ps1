# Build AceScheduler as a self-contained single-file exe.
# Requires .NET 8 SDK. No-admin install (per-user):
#   irm https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
#   .\dotnet-install.ps1 -Channel 8.0   # installs to %LOCALAPPDATA%\Microsoft\dotnet
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sdkDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$dotnet = if (Test-Path (Join-Path $sdkDir 'dotnet.exe')) { Join-Path $sdkDir 'dotnet.exe' } else { 'dotnet' }

& $dotnet publish (Join-Path $repoRoot 'src\AceScheduler') `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $repoRoot 'dist')
if ($LASTEXITCODE -ne 0) { throw "publish failed: $LASTEXITCODE" }
Write-Host "artifact: $(Join-Path $repoRoot 'dist\AceScheduler.exe')"
Get-Item (Join-Path $repoRoot 'dist\AceScheduler.exe') | Select-Object Name, Length, LastWriteTime
