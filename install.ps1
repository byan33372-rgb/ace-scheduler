# Per-user installer for mvxxcb-restrain.
# Copies the exe to %LOCALAPPDATA%\mvxxcb-restrain and registers a Startup
# shortcut so the scheduler is available after every logon.
# The tool itself is demand-driven: it only acts while scanner processes exist.
param()
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'mvxxcb-restrain.exe'
if (-not (Test-Path $src)) { throw "exe not found next to install.ps1: $src" }

$dstDir = Join-Path $env:LOCALAPPDATA 'mvxxcb-restrain'
$dst = Join-Path $dstDir 'mvxxcb-restrain.exe'
New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
Copy-Item $src $dst -Force

$startup = [Environment]::GetFolderPath('Startup')
$lnkPath = Join-Path $startup 'mvxxcb-restrain.lnk'
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($lnkPath)
$lnk.TargetPath = $dst
$lnk.Description = 'mvxxcb-restrain: schedule background scanner resource usage'
$lnk.Save()

Write-Host "installed: $dst"
Write-Host "startup shortcut: $lnkPath"
Write-Host 'To adjust SYSTEM-owned processes the scheduler must run elevated:'
Write-Host '  run register-task.ps1 once from an elevated PowerShell (replaces the shortcut with a highest-privilege logon task).'
Write-Host 'To remove: delete the shortcut and the %LOCALAPPDATA%\mvxxcb-restrain folder (and remove the scheduled task if registered).'
