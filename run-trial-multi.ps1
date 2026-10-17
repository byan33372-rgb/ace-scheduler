# AceScheduler MULTI-TARGET comprehensive validation (v1)
# Spawns N dummy scanners (default 3) + 1 foreground simulator + ONE engine
# instance managing all N targets - mirroring real ACE (SGuard64/SguardSvc64/
# ACE-Service64 running together).
# Phases: P1 baseline -> P2 burst+fg busy (ALL throttled) -> P3 scan stops
# (release) -> P4 burst again (re-throttle, mid-phase readback) -> P5 fg idle
# (passthrough, all restored) -> P6 quiet (converge to Idle).
param([int]$ScannerCount = 3)
$ErrorActionPreference = 'Stop'
function Set-Control([string]$path, [string]$value) {
  # write via a stream opened with FileShare.ReadWrite so we never collide
  # with the control-file readers (they open with the same sharing mode)
  for ($attempt = 0; $attempt -lt 40; $attempt++) {
    try {
      $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
      $bytes = [System.Text.Encoding]::UTF8.GetBytes($value)
      $fs.Write($bytes, 0, $bytes.Length)
      $fs.Dispose()
      return
    } catch { Start-Sleep -Milliseconds 150 }
  }
  throw "cannot write control file: $path"
}
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dir
$appExe   = Join-Path $dir 'dist\AceScheduler.exe'
$trial    = Join-Path $dir 'trial'
$bin      = Join-Path $dir 'dummy.bin'
$dummyCtl = Join-Path $dir 'dummy.ctl'
$simCtl   = Join-Path $dir 'sim.ctl'
$limCtl   = Join-Path $dir 'limiter.ctl'
$fgCtl    = Join-Path $dir 'fg.ctl'
$limLog   = Join-Path $dir 'limiter.jsonl'
$simLog   = Join-Path $dir 'sim.log'
$phasesF  = Join-Path $dir 'phases.json'
$reportF  = Join-Path $dir 'trial-report-multi.json'
if (-not (Test-Path $appExe)) { throw "exe missing: run .\build.ps1 first" }
Remove-Item $limLog, $simLog, $phasesF, $reportF -ErrorAction SilentlyContinue
Get-ChildItem "$dir\dummy*.log" -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue

$me = $PID
$zombies = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue | Where-Object {
  $_.ProcessId -ne $me -and $_.CommandLine -match 'DummyScanner\.ps1|ForegroundSim\.ps1|AceScheduler\.Trial\.ps1'
})
$zombiesExe = @(Get-Process AceScheduler -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $me })
foreach ($z in $zombies) { try { Stop-Process -Id $z.ProcessId -Force -ErrorAction Stop } catch {} }
foreach ($z in $zombiesExe) { try { Stop-Process -Id $z.Id -Force -ErrorAction Stop } catch {} }

Set-Control $dummyCtl 'pause'
Set-Control $simCtl 'run'
Set-Control $limCtl 'go'
Set-Control $fgCtl 'busy'

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
function Get-SumReadBytes([int[]]$procIds) {
  $sum = 0.0
  foreach ($id in $procIds) { $sum += [double](Get-CimInstance Win32_Process -Filter "ProcessId=$id").ReadTransferCount }
  return $sum
}
function Get-TargetStates([int[]]$procIds) {
  $out = @()
  foreach ($id in $procIds) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p) { $out += [pscustomobject]@{ pid = $id; priority = [string]$p.PriorityClass; affinity = $p.ProcessorAffinity.ToString() } }
    else { $out += [pscustomobject]@{ pid = $id; priority = 'GONE'; affinity = '' } }
  }
  return $out
}

# start N scanners (shared control file, individual logs)
$dummies = @()
for ($i = 1; $i -le $ScannerCount; $i++) {
  $dummies += Start-ChildProc 'trial\DummyScanner.ps1' @('-TargetFile', "`"$bin`"", '-LogFile', "`"$dir\dummy$i.log`"", '-ControlFile', "`"$dummyCtl`"")
}
Start-Sleep 1
$pidList = @($dummies | ForEach-Object Id)
Write-Host "scanners: $($pidList -join ',')"

$t1s = UtcStamp; $b0 = Get-SumReadBytes $pidList
Set-Control $dummyCtl 'run'
Start-Sleep 8                        # P1 baseline
$t1e = UtcStamp; $b1 = Get-SumReadBytes $pidList
$sim = Start-ChildProc 'trial\ForegroundSim.ps1' @('-LogFile', "`"$simLog`"", '-ControlFile', "`"$simCtl`"")
$lim = Start-Process -FilePath $appExe -ArgumentList @(
  '--selftest', '--target-pids', ($pidList -join ','),
  '--control', "`"$limCtl`"", '--log', "`"$limLog`"", '--foreground', "`"$fgCtl`"",
  '--burst-mbs', '5', '--probe-sec', '5', '--poll-ms', '500'
) -PassThru -WindowStyle Hidden
$t2s = UtcStamp; $b2 = $b1
Start-Sleep 14                       # P2 burst + fg busy -> all throttled
$t2e = UtcStamp; $b3 = Get-SumReadBytes $pidList
# log-based multi-target evidence: distinct pids seen THROTTLED in P2
$limLines1 = @(Get-Content $limLog -ErrorAction SilentlyContinue | ForEach-Object { $_ | ConvertFrom-Json })
$throttledPids = @($limLines1 | Where-Object { $_.evt -eq 'sample' -and $_.state -eq 'Throttled' } | ForEach-Object { $_.pid } | Sort-Object -Unique)
Set-Control $dummyCtl 'pause'
$t3s = UtcStamp
Start-Sleep 8                        # P3 scan stopped -> release
$rb1 = Get-TargetStates $pidList
Start-Sleep 2
$t3e = UtcStamp; $b4 = Get-SumReadBytes $pidList
Set-Control $dummyCtl 'run'
$t4s = UtcStamp; $b5 = $b4
Start-Sleep 3                        # mid-P4: safely inside a throttle window
$rb2 = Get-TargetStates $pidList     # expect ALL Idle (re-throttled per target)
Start-Sleep 7
$t4e = UtcStamp; $b6 = Get-SumReadBytes $pidList
Set-Control $fgCtl 'idle'            # foreground goes idle mid-scan
$t5s = UtcStamp; $b7 = $b6
Start-Sleep 12                       # P5 passthrough -> all released
$t5e = UtcStamp; $b8 = Get-SumReadBytes $pidList
$rb3 = Get-TargetStates $pidList     # expect ALL Normal+full
Set-Control $dummyCtl 'pause'
$t6s = UtcStamp
Start-Sleep 6                        # P6 quiet convergence
$rb4 = Get-TargetStates $pidList     # expect ALL Normal+full (final)
Start-Sleep 2
$t6e = UtcStamp; $b9 = Get-SumReadBytes $pidList
Set-Control $dummyCtl 'stop'; Set-Control $simCtl 'stop'; Set-Control $limCtl 'stop'
Start-Sleep 2
foreach ($p in @($dummies) + @($sim, $lim)) { if ($p -and -not $p.HasExited) { try { $p.Kill() } catch {} } }

$procCount = [int]$env:NUMBER_OF_PROCESSORS
$fullMask = ([long][math]::Pow(2, $procCount) - 1).ToString()
$phases = [ordered]@{ p1_start = $t1s; p1_end = $t1e; p2_start = $t2s; p2_end = $t2e; p3_start = $t3s; p3_end = $t3e; p4_start = $t4s; p4_end = $t4e; p5_start = $t5s; p5_end = $t5e; p6_start = $t6s; p6_end = $t6e }
$phases | ConvertTo-Json | Set-Content $phasesF

$m1 = [math]::Round(($b1 - $b0) / 1MB / 8, 1)
$m2 = [math]::Round(($b3 - $b2) / 1MB / 14, 1)
$m4 = [math]::Round(($b6 - $b5) / 1MB / 10, 1)
$m5 = [math]::Round(($b8 - $b7) / 1MB / 12, 1)
$limLines = @(Get-Content $limLog -ErrorAction SilentlyContinue | ForEach-Object { $_ | ConvertFrom-Json })
$bursts   = @($limLines | Where-Object { $_.evt -eq 'burst-detected' })
$reprobes = @($limLines | Where-Object { $_.evt -eq 'probe-scanning' })
$releases = @($limLines | Where-Object { $_.evt -eq 'scan-ended' })
$p4t      = @($limLines | Where-Object { $_.evt -eq 'sample' -and $_.state -eq 'Throttled' -and (UtcParse $_.ts) -ge (UtcParse $t4s) -and (UtcParse $_.ts) -le (UtcParse $t4e) })
$p5biPids = @($limLines | Where-Object { $_.evt -eq 'sample' -and $_.state -eq 'BurstIdle' -and (UtcParse $_.ts) -ge (UtcParse $t5s) -and (UtcParse $_.ts) -le (UtcParse $t5e) } | ForEach-Object { $_.pid } | Sort-Object -Unique)
$p5bi     = $p5biPids.Count
$simRows  = @(Get-Content $simLog -ErrorAction SilentlyContinue)

$ratio  = if ($m1 -gt 0) { [math]::Round($m2 / $m1, 3) } else { $null }
$ratio5 = if ($m1 -gt 0) { [math]::Round($m5 / $m1, 3) } else { $null }
$applies = $bursts.Count + $reprobes.Count
$rb1AllNormal = @($rb1 | Where-Object { $_.priority -eq 'Normal' -and $_.affinity -eq $fullMask }).Count -eq $ScannerCount
$rb2AllIdle   = @($rb2 | Where-Object { $_.priority -eq 'Idle' -and $_.affinity -eq '1' }).Count -eq $ScannerCount
$rb3AllNormal = @($rb3 | Where-Object { $_.priority -eq 'Normal' -and $_.affinity -eq $fullMask }).Count -eq $ScannerCount
$rb4AllNormal = @($rb4 | Where-Object { $_.priority -eq 'Normal' -and $_.affinity -eq $fullMask }).Count -eq $ScannerCount

$checks = [ordered]@{}
$checks.c1_throttle_applied = @{ pass = ($applies -ge $ScannerCount); detail = "throttle applies=$applies across $ScannerCount engines (expect >=$ScannerCount)" }
$checks.c2_throttle_effect  = @{ pass = ($null -ne $ratio -and $ratio -lt 0.5); detail = "P1=$m1 MB/s, P2=$m2 MB/s (sum of $ScannerCount), ratio=$ratio (expect <0.5)" }
$c3ok = ($releases.Count -ge 1 -and $rb1AllNormal)
$checks.c3_auto_release     = @{ pass = $c3ok; detail = "release events=$($releases.Count), P3 readback all Normal+full=$rb1AllNormal" }
$checks.c4_rethrottle       = @{ pass = ($p4t.Count -ge 2); detail = "THROTTLED samples in P4=$($p4t.Count), P4 throughput=$m4 MB/s" }
$checks.c5_foreground_ok    = @{ pass = ($simRows.Count -ge 5); detail = "foreground sim samples=$($simRows.Count)" }
$c6ok = ($null -ne $ratio5 -and $ratio5 -gt 0.5 -and $p5bi -ge 1)
$checks.c6_idle_passthrough = @{ pass = $c6ok; detail = "P5: BurstIdle pids=$($p5biPids -join '+') ($p5bi/$ScannerCount), throughput=$m5 MB/s, ratio5=$ratio5 (expect >0.5)" }
$c7ok = ($rb4AllNormal)
$checks.c7_final_restore    = @{ pass = $c7ok; detail = "P6 final readback all Normal+full=$rb4AllNormal" }
$c8ok = ($throttledPids.Count -ge $ScannerCount -and $rb2AllIdle -and $rb3AllNormal)
$checks.c8_multi_target     = @{ pass = $c8ok; detail = "distinct THROTTLED pids in P2=$($throttledPids.Count)/$ScannerCount, mid-P4 readback all Idle=$rb2AllIdle, P5 readback all Normal=$rb3AllNormal" }
$allPass = $true
foreach ($k in $checks.Keys) { if (-not $checks[$k].pass) { $allPass = $false } }
$report = @{ allPass = $allPass; engine = 'csharp-v3-multi'; scanners = $ScannerCount; checks = $checks; readbacks = @{ p3 = $rb1; midP4 = $rb2; p5 = $rb3; final = $rb4 }; phaseMBs = @{ p1 = $m1; p2 = $m2; p4 = $m4; p5 = $m5 } }
$report | ConvertTo-Json -Depth 6 | Set-Content $reportF

Write-Host "===== AceScheduler MULTI-TARGET validation ($ScannerCount scanners) ====="
foreach ($k in $checks.Keys) {
  $tag = 'FAIL'; if ($checks[$k].pass) { $tag = 'PASS' }
  Write-Host "[$tag] $k : $($checks[$k].detail)"
}
Write-Host "OVERALL: $allPass"
Remove-Item $bin -ErrorAction SilentlyContinue
