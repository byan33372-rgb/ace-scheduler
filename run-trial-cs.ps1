# AceScheduler C# engine closed-loop orchestrator (v2, six phases)
# P1 baseline -> P2 burst+foreground busy (expect throttle) -> P3 scan stops
# (expect probe release) -> P4 burst again, fg busy (expect re-throttle)
# -> P5 foreground goes idle mid-scan (expect passthrough: release while the
# scan continues) -> P6 scan stops (expect quiet convergence to IDLE).
# v0.3 addition: the --foreground control file feeds the engine's
# foreground-busy signal (production uses ForegroundMonitor fullscreen detection).
param()
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
$dummyLog = Join-Path $dir 'dummy.log'
$simLog   = Join-Path $dir 'sim.log'
$limLog   = Join-Path $dir 'limiter.jsonl'
$dummyCtl = Join-Path $dir 'dummy.ctl'
$simCtl   = Join-Path $dir 'sim.ctl'
$limCtl   = Join-Path $dir 'limiter.ctl'
$fgCtl    = Join-Path $dir 'fg.ctl'
$phasesF  = Join-Path $dir 'phases.json'
$reportF  = Join-Path $dir 'trial-report-cs.json'
if (-not (Test-Path $appExe)) { throw "exe missing: run .\build.ps1 first" }
Remove-Item $dummyLog, $simLog, $limLog, $phasesF, $reportF -ErrorAction SilentlyContinue

$me = $PID
$zombies = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue | Where-Object {
  $_.ProcessId -ne $me -and $_.CommandLine -match 'DummyScanner\.ps1|ForegroundSim\.ps1|AceScheduler\.Trial\.ps1'
})
$zombiesExe = @(Get-Process AceScheduler -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $me })
foreach ($z in $zombies) { try { Stop-Process -Id $z.ProcessId -Force -ErrorAction Stop } catch {} }
foreach ($z in $zombiesExe) { try { Stop-Process -Id $z.Id -Force -ErrorAction Stop } catch {} }
if ($zombies.Count + $zombiesExe.Count -gt 0) { Write-Host "zombie helpers killed: $($zombies.Count + $zombiesExe.Count)" }

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
function Get-ReadBytes([int]$procId) {
  return [double](Get-CimInstance Win32_Process -Filter "ProcessId=$procId").ReadTransferCount
}

$dummy = Start-ChildProc 'trial\DummyScanner.ps1' @('-TargetFile', "`"$bin`"", '-LogFile', "`"$dummyLog`"", '-ControlFile', "`"$dummyCtl`"")
Start-Sleep 1
$t1s = UtcStamp; $b0 = Get-ReadBytes $dummy.Id
Set-Control $dummyCtl 'run'
Start-Sleep 8                        # P1 baseline
$t1e = UtcStamp; $b1 = Get-ReadBytes $dummy.Id
$sim = Start-ChildProc 'trial\ForegroundSim.ps1' @('-LogFile', "`"$simLog`"", '-ControlFile', "`"$simCtl`"")
$lim = Start-Process -FilePath $appExe -ArgumentList @(
  '--selftest', '--target-pid', [string]$dummy.Id,
  '--control', "`"$limCtl`"", '--log', "`"$limLog`"",
  '--foreground', "`"$fgCtl`"",
  '--burst-mbs', '5', '--probe-sec', '5', '--poll-ms', '500'
) -PassThru -WindowStyle Hidden
$t2s = UtcStamp; $b2 = $b1
Start-Sleep 14                       # P2 burst + fg busy -> throttle
$t2e = UtcStamp; $b3 = Get-ReadBytes $dummy.Id
Set-Control $dummyCtl 'pause'
$t3s = UtcStamp
Start-Sleep 8                        # P3 scan stopped -> probe release
$chk = Get-Process -Id $dummy.Id -ErrorAction SilentlyContinue
$rcPriority = ''; $rcAffinity = ''
if ($chk) { $rcPriority = [string]$chk.PriorityClass; $rcAffinity = $chk.ProcessorAffinity.ToString() }
Start-Sleep 2
$t3e = UtcStamp; $b4 = Get-ReadBytes $dummy.Id
Set-Control $dummyCtl 'run'
$t4s = UtcStamp; $b5 = $b4
Start-Sleep 10                       # P4 burst again, fg busy -> re-throttle
$t4e = UtcStamp; $b6 = Get-ReadBytes $dummy.Id
Set-Control $fgCtl 'idle'            # v0.3: foreground goes idle mid-scan
$t5s = UtcStamp; $b7 = $b6
Start-Sleep 12                       # P5 expect passthrough (release, scan continues)
$t5e = UtcStamp; $b8 = Get-ReadBytes $dummy.Id
Set-Control $dummyCtl 'pause'
$t6s = UtcStamp
Start-Sleep 6                        # P6 quiet -> BurstIdle converges to Idle
$chk2 = Get-Process -Id $dummy.Id -ErrorAction SilentlyContinue
$rc2Priority = ''; $rc2Affinity = ''
if ($chk2) { $rc2Priority = [string]$chk2.PriorityClass; $rc2Affinity = $chk2.ProcessorAffinity.ToString() }
Start-Sleep 2
$t6e = UtcStamp; $b9 = Get-ReadBytes $dummy.Id
Set-Control $dummyCtl 'stop'; Set-Control $simCtl 'stop'; Set-Control $limCtl 'stop'
Start-Sleep 2
foreach ($p in @($dummy, $sim, $lim)) { if ($p -and -not $p.HasExited) { try { $p.Kill() } catch {} } }

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
$p5bi     = @($limLines | Where-Object { $_.evt -eq 'sample' -and $_.state -eq 'BurstIdle' -and (UtcParse $_.ts) -ge (UtcParse $t5s) -and (UtcParse $_.ts) -le (UtcParse $t5e) })
$p5pass   = @($limLines | Where-Object { $_.evt -eq 'passthrough' -and (UtcParse $_.ts) -ge (UtcParse $t5s) -and (UtcParse $_.ts) -le (UtcParse $t5e) })
$simRows  = @(Get-Content $simLog -ErrorAction SilentlyContinue)

$ratio = $null
if ($m1 -gt 0) { $ratio = [math]::Round($m2 / $m1, 3) }
$ratio5 = $null
if ($m1 -gt 0) { $ratio5 = [math]::Round($m5 / $m1, 3) }
$applies = $bursts.Count + $reprobes.Count
$checks = [ordered]@{}
$checks.c1_throttle_applied = @{ pass = ($applies -ge 2); detail = "throttle applies=$applies (burst=$($bursts.Count), probe-re=$($reprobes.Count)) (expect >=2)" }
$checks.c2_throttle_effect = @{ pass = ($null -ne $ratio -and $ratio -lt 0.5); detail = "P1=$m1 MB/s, P2=$m2 MB/s, ratio=$ratio (expect <0.5)" }
$releaseOk = ($releases.Count -ge 1 -and $rcPriority -eq 'Normal' -and $rcAffinity -eq $fullMask)
$checks.c3_auto_release    = @{ pass = $releaseOk; detail = "release events=$($releases.Count), P3+8s: priority=$rcPriority affinity=$rcAffinity (full=$fullMask)" }
$checks.c4_rethrottle      = @{ pass = ($p4t.Count -ge 2); detail = "THROTTLED samples in P4=$($p4t.Count), P4 throughput=$m4 MB/s" }
$checks.c5_foreground_ok   = @{ pass = ($simRows.Count -ge 5); detail = "foreground sim samples=$($simRows.Count) (expect >=5)" }
$c6ok = ($p5bi.Count -ge 1 -and $null -ne $ratio5 -and $ratio5 -gt 0.5)
$checks.c6_idle_passthrough = @{ pass = $c6ok; detail = "BurstIdle samples in P5=$($p5bi.Count), passthrough events=$($p5pass.Count), P5 throughput=$m5 MB/s, ratio5=$ratio5 (expect >0.5)" }
$c7ok = ($rc2Priority -eq 'Normal' -and $rc2Affinity -eq $fullMask)
$checks.c7_final_restore    = @{ pass = $c7ok; detail = "P6 end: priority=$rc2Priority affinity=$rc2Affinity (full=$fullMask)" }
$allPass = $true
foreach ($k in $checks.Keys) { if (-not $checks[$k].pass) { $allPass = $false } }
$report = @{ allPass = $allPass; engine = 'csharp-v3'; checks = $checks; restore = @{ p3 = @{ priority = $rcPriority; affinity = $rcAffinity }; p6 = @{ priority = $rc2Priority; affinity = $rc2Affinity }; fullMask = $fullMask }; phaseMBs = @{ p1 = $m1; p2 = $m2; p4 = $m4; p5 = $m5 } }
$report | ConvertTo-Json -Depth 5 | Set-Content $reportF

Write-Host '===== AceScheduler C# engine closed-loop validation (v3, foreground-gated) ====='
foreach ($k in $checks.Keys) {
  $tag = 'FAIL'; if ($checks[$k].pass) { $tag = 'PASS' }
  Write-Host "[$tag] $k : $($checks[$k].detail)"
}
Write-Host "OVERALL: $allPass"
Remove-Item $bin -ErrorAction SilentlyContinue
