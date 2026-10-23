# One-time elevated setup: registers a logon Scheduled Task that starts the
# scheduler with HIGHEST privileges. Elevation is what allows adjusting the
# priority of SYSTEM-owned scanner processes - without it every throttle
# attempt fails with OpenProcess access denied.
# Run from an ELEVATED PowerShell:  .\register-task.ps1 [-Exe <path>] [-Uninstall]
param(
  [string]$Exe = (Join-Path $env:LOCALAPPDATA 'mvxxcb-restrain\mvxxcb-restrain.exe'),
  [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$taskName = 'mvxxcb-restrain'
if ($Uninstall) {
  Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
  Write-Host "task '$taskName' removed"
  exit
}
if (-not (Test-Path $Exe)) { throw "exe not found: $Exe (install first via install.ps1)" }
$action = New-ScheduledTaskAction -Execute $Exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -RunLevel Highest
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
Write-Host "scheduled task '$taskName' registered: starts $Exe at logon with highest privileges"
