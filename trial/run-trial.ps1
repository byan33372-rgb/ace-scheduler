# AceScheduler trial orchestrator (v4)
# P1 baseline -> P2 burst+contention (expect throttle) -> P3 burst stops
# (expect probe-based release) -> P4 burst again (expect re-throttle).
# v4 fix: phase throughput is measured by the orchestrator via ReadTransferCount
# deltas at phase boundaries. The v2/v3 metric (median of the scanner's own
# per-second log rows) is blind under full starvation: a starved scanner stops
# emitting rows, so the median only sees the pre-throttle seconds.
# Earlier fixes kept: UTC-normalized parsing, zombie cleanup, wall-clock probes.
# Sandbox: only touches child processes created here. No admin, no ACE access.
param()
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dir
$bin      = Join-Path $dir 'dummy.bin'
$dummyLog = Join-Path $dir 'dummy.log'
$simLog   = Join-Path $dir 'sim.log'
$limLog   = Join-Path $dir 'limiter.jsonl'
$dummyCtl = Join-Path $dir 'dummy.ctl'
$simCtl   = Join-Path $dir 'sim.ctl'
$limCtl   = Join-Path $dir 'limiter.ctl'
$phasesF  = Join-Path $dir 'phases.json'
$reportF  = Join-Path $dir 'trial-report.json'
Remove-Item $dummyLog, $simLog, $limLog, $phasesF, $reportF -ErrorAction SilentlyContinue

# kill helpers left behind by crashed earlier runs (they share ctl/log files)
$me = $PID
$zombies = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue | Where-Object {
  $_.ProcessId -ne $me -and $_.CommandLine -match 'DummyScanner\.ps1|ForegroundSim\.ps1|AceScheduler\.Trial\.ps1'
})
foreach ($z in $zombies) { try { Stop-Process -Id $z.ProcessId -Force -ErrorAction Stop } catch {} }
if ($zombies.Count -gt 0) { Write-Host "zombie helpers killed: $($zombies.Count)" }

'pause' | Set-Content $dummyCtl
'run'   | Set-Content $simCtl
'go'    | Set-Content $limCtl

$size = 256MB
if (-not (Test-Path $bin) -or (Get-Item $bin).Length -ne $size) {
  $fs = [System.IO.File]::Create($bin)
  $buf = New-Object byte[] 1048576
  (New-Object Random 1).NextBytes($buf)
  for ($i = 0; $i -lt 256; $i++) { $fs.Write($buf, 0, $buf.Length) }
  $fs.Close()
}
function Start-ChildProc([string]$script, [object[]]$cargs) {
  $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$dir\$script`"") + $cargs
  Start-Process -FilePath 'powershell.exe' -ArgumentList $all -PassThru -WindowStyle Hidden
}
function UtcStamp { return (Get-Date).ToUniversalTime().ToString('o') }
function UtcParse([string]$s) { return [datetime]::Parse($s, $null, [System.Globalization.DateTimeStyles]::RoundtripKind) }
function Get-ReadBytes([int]$procId) {
  return [double](Get-CimInstance Win32_Process -Filter "ProcessId=$procId").ReadTransferCount
}

$dummy = Start-ChildProc 'DummyScanner.ps1' @('-TargetFile', "`"$bin`"", '-LogFile', "`"$dummyLog`"", '-ControlFile', "`"$dummyCtl`"")
Start-Sleep 1
$t1s = UtcStamp; $b0 = Get-ReadBytes $dummy.Id
'run' | Set-Content $dummyCtl
Start-Sleep 8                        # P1 baseline, no limiter yet
$t1e = UtcStamp; $b1 = Get-ReadBytes $dummy.Id
$sim = Start-ChildProc 'ForegroundSim.ps1' @('-LogFile', "`"$simLog`"", '-ControlFile', "`"$simCtl`"")
$lim = Start-ChildProc 'AceScheduler.Trial.ps1' @('-TargetPid', $dummy.Id, '-ControlFile', "`"$limCtl`"", '-LogFile', "`"$limLog`"")
$t2s = UtcStamp; $b2 = $b1
Start-Sleep 14                       # P2 burst + contention, expect throttle
$t2e = UtcStamp; $b3 = Get-ReadBytes $dummy.Id
'pause' | Set-Content $dummyCtl
$t3s = UtcStamp
Start-Sleep 8                        # P3 scan stopped; probe should release within ~5s
$chk = Get-Process -Id $dummy.Id -ErrorAction SilentlyContinue
$rcPriority = ''; $rcAffinity = ''
if ($chk) { $rcPriority = [string]$chk.PriorityClass; $rcAffinity = $chk.ProcessorAffinity.ToString() }
Start-Sleep 2
$t3e = UtcStamp; $b4 = Get-ReadBytes $dummy.Id
'run' | Set-Content $dummyCtl
$t4s = UtcStamp; $b5 = $b4
Start-Sleep 10                       # P4 burst again, expect re-throttle
$t4e = UtcStamp; $b6 = Get-ReadBytes $dummy.Id
'stop' | Set-Content $dummyCtl; 'stop' | Set-Content $simCtl; 'stop' | Set-Content $limCtl
Start-Sleep 2
foreach ($p in @($dummy, $sim, $lim)) { if ($p -and -not $p.HasExited) { try { $p.Kill() } catch {} } }

$procCount = [int]$env:NUMBER_OF_PROCESSORS
$fullMask = ([long][math]::Pow(2, $procCount) - 1).ToString()
$phases = [ordered]@{ p1_start = $t1s; p1_end = $t1e; p2_start = $t2s; p2_end = $t2e; p3_start = $t3s; p3_end = $t3e; p4_start = $t4s; p4_end = $t4e }
$phases | ConvertTo-Json | Set-Content $phasesF

$m1 = [math]::Round(($b1 - $b0) / 1MB / 8, 1)
$m2 = [math]::Round(($b3 - $b2) / 1MB / 14, 1)
$m4 = [math]::Round(($b6 - $b5) / 1MB / 10, 1)
$limLines = @(Get-Content $limLog -ErrorAction SilentlyContinue | ForEach-Object { $_ | ConvertFrom-Json })
$bursts   = @($limLines | Where-Object { $_.evt -eq 'burst-detected' })
$reprobes = @($limLines | Where-Object { $_.evt -eq 'probe-scanning' })
$releases = @($limLines | Where-Object { $_.evt -eq 'scan-ended' })
$p4t      = @($limLines | Where-Object { $_.evt -eq 'sample' -and $_.state -eq 'THROTTLED' -and (UtcParse $_.ts) -ge (UtcParse $t4s) -and (UtcParse $_.ts) -le (UtcParse $t4e) })
$simRows  = @(Get-Content $simLog -ErrorAction SilentlyContinue)

$ratio = $null
if ($m1 -gt 0) { $ratio = [math]::Round($m2 / $m1, 3) }
$applies = $bursts.Count + $reprobes.Count
$checks = [ordered]@{}
$checks.c1_throttle_applied = @{ pass = ($applies -ge 2); detail = "throttle applies=$applies (burst=$($bursts.Count), probe-re=$($reprobes.Count)) (expect >=2)" }
$checks.c2_throttle_effect = @{ pass = ($null -ne $ratio -and $ratio -lt 0.5); detail = "P1=$m1 MB/s, P2=$m2 MB/s, ratio=$ratio (expect <0.5)" }
$releaseOk = ($releases.Count -ge 1 -and $rcPriority -eq 'Normal' -and $rcAffinity -eq $fullMask)
$checks.c3_auto_release    = @{ pass = $releaseOk; detail = "release events=$($releases.Count), P3+8s check: priority=$rcPriority affinity=$rcAffinity (full=$fullMask)" }
$checks.c4_rethrottle      = @{ pass = ($p4t.Count -ge 2); detail = "THROTTLED samples in P4=$($p4t.Count), P4 throughput=$m4 MB/s" }
$checks.c5_foreground_ok   = @{ pass = ($simRows.Count -ge 5); detail = "foreground sim samples=$($simRows.Count) (expect >=5)" }
$allPass = $true
foreach ($k in $checks.Keys) { if (-not $checks[$k].pass) { $allPass = $false } }
$report = @{ allPass = $allPass; checks = $checks; restore = @{ priority = $rcPriority; affinity = $rcAffinity; fullMask = $fullMask }; phaseMBs = @{ p1 = $m1; p2 = $m2; p4 = $m4 } }
$report | ConvertTo-Json -Depth 4 | Set-Content $reportF

Write-Host '===== AceScheduler trial sandbox validation ====='
foreach ($k in $checks.Keys) {
  $tag = 'FAIL'; if ($checks[$k].pass) { $tag = 'PASS' }
  Write-Host "[$tag] $k : $($checks[$k].detail)"
}
Write-Host "OVERALL: $allPass"
Remove-Item $bin -ErrorAction SilentlyContinue
