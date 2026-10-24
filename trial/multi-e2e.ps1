$ErrorActionPreference='Continue'
Set-Location 'D:\fieldtest'
function Get-IO($pids) {
  $r = @{}
  foreach ($p in $pids) {
    try { $w = Get-CimInstance Win32_Process -Filter "ProcessId=$p" -Property ReadTransferCount; if ($w) { $r[$p] = [double]$w.ReadTransferCount } } catch {}
  }
  return $r
}
function Rate-Table($a, $b, $secs) {
  $o = @()
  foreach ($p in $a.Keys) {
    if ($b.ContainsKey($p)) {
      $mbps = [math]::Round((($b[$p] - $a[$p]) / 1MB) / $secs, 0)
      $o += "pid=$p ${mbps}MB/s"
    }
  }
  return ($o -join '  ')
}
Remove-Item field.stop -ErrorAction SilentlyContinue
$stateDir = "$env:LOCALAPPDATA\mvxxcb-restrain"
Set-Content "$stateDir\enabled.txt" '1'
Set-Content "$stateDir\foreground-processes.txt" "WindowsTerminal`r`n"
Set-Content sguard.ctl 'run'

# launch 5 scanners
$sg = 1..5 | ForEach-Object { Start-Process 'D:\fieldtest\SGuard64.exe' -PassThru -ArgumentList 'D:\fieldtest\target.bin','D:\fieldtest\sguard.ctl' }
$pids = $sg.Id
"scanners: $($pids -join ',')"

# P0 baseline (no limiter running)
Start-Sleep 3
$a = Get-IO $pids; Start-Sleep 5; $b = Get-IO $pids
"P0 baseline (5s): " + (Rate-Table $a $b 5)

# start the real app
$app = Start-Process 'D:\Projects\ace-scheduler\dist\mvxxcb-restrain.exe' -PassThru
$appWs1 = [math]::Round($app.WorkingSet64/1MB,1)
Start-Sleep 15

# P1 all throttled?
$a = Get-IO $pids; Start-Sleep 5; $b = Get-IO $pids
"P1 throttled (t+15~20): " + (Rate-Table $a $b 5)

# kill #3 and #5 mid-throttle
Stop-Process -Id $pids[2] -Force; Stop-Process -Id $pids[4] -Force
"killed: $($pids[2]), $($pids[4]) (mid-throttle)"
Start-Sleep 25
$alive = @($pids[0], $pids[1], $pids[3])

# P2 survivors still throttled, dead ones detected
$a = Get-IO $alive; Start-Sleep 5; $b = Get-IO $alive
"P2 survivors (t+40~45): " + (Rate-Table $a $b 5)

# clear identity list -> hot reload releases everything
Set-Content "$stateDir\foreground-processes.txt" "# cleared`r`n"
Start-Sleep 15
$a = Get-IO $alive; Start-Sleep 5; $b = Get-IO $alive
"P3 released (t+60~65): " + (Rate-Table $a $b 5)

$appWs2 = [math]::Round($app.WorkingSet64/1MB,1)
foreach ($p in $alive) { Stop-Process -Id $p -Force -ErrorAction SilentlyContinue }
Start-Sleep 8
Set-Content "$stateDir\enabled.txt" '0'
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
"app WS: t+15s=${appWs1}MB  t+75s=${appWs2}MB"
'multi-e2e-done'
