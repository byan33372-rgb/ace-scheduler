# AceScheduler trial core (v3)
# States:
#   IDLE      -> 2 consecutive burst samples => THROTTLED (PriorityClass=Idle + affinity core0)
#   THROTTLED -> after ProbeSec of throttled wall time => release for ONE sample (PROBE)
#   PROBE     -> rate >= BurstMBs = scan continues => THROTTLED ; rate low = scan ended => IDLE
# The probe prevents the starvation feedback loop: throttling collapses the
# scanner's IO rate, which by rate alone would fake a "scan ended" signal.
# Probe cadence is wall-clock (accumulated sample dt), NOT tick count: the CIM
# sample loop runs ~1.3s/tick on a loaded box, so tick-counting drifted 2.5x.
# Policy line: only schedule resources (priority/affinity). No injection, no
# blocking, no ACE file access.
param(
  [int]$TargetPid,
  [string]$ControlFile,
  [string]$LogFile,
  [double]$BurstMBs = 5,
  [double]$ProbeSec = 5,
  [int]$PollMs = 500
)
function Write-Log([hashtable]$o) {
  $o.ts = (Get-Date).ToUniversalTime().ToString('o')
  ($o | ConvertTo-Json -Compress) | Add-Content -Path $LogFile
}
$procCount = [int]$env:NUMBER_OF_PROCESSORS
$fullMask = [IntPtr]([long]([math]::Pow(2, $procCount) - 1))
$state = 'IDLE'
$burstRun = 0
$throttleSecs = 0.0
$lastRead = $null; $lastTs = $null
Write-Log @{ evt = 'start'; target = $TargetPid; burstMBs = $BurstMBs; probeSec = $ProbeSec }
while ($true) {
  Start-Sleep -Milliseconds $PollMs
  if ((Get-Content $ControlFile -ErrorAction SilentlyContinue) -eq 'stop') { break }
  $p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
  if (-not $p) { Write-Log @{ evt = 'target-gone' }; break }
  $io = Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid" -ErrorAction SilentlyContinue
  if (-not $io) { continue }
  $now = Get-Date
  if ($null -ne $lastRead) {
    $dt = ($now - $lastTs).TotalSeconds
    if ($dt -gt 0.2) {
      $mbps = [math]::Round(([double]$io.ReadTransferCount - $lastRead) / 1MB / $dt, 1)
      if ($state -eq 'IDLE') {
        if ($mbps -ge $BurstMBs) { $burstRun++ } else { $burstRun = 0 }
        if ($burstRun -ge 2) {
          try {
            $p.PriorityClass = 'Idle'
            $p.ProcessorAffinity = [IntPtr]1
            $state = 'THROTTLED'; $throttleSecs = 0.0
            Write-Log @{ evt = 'burst-detected'; mbps = $mbps; action = 'throttle: Idle+core0' }
          } catch { Write-Log @{ evt = 'throttle-failed'; err = $_.Exception.Message } }
        }
      } elseif ($state -eq 'THROTTLED') {
        $throttleSecs += $dt
        if ($throttleSecs -ge $ProbeSec) {
          try {
            $p2 = Get-Process -Id $TargetPid
            $p2.PriorityClass = 'Normal'
            $p2.ProcessorAffinity = $fullMask
            $state = 'PROBE'
            Write-Log @{ evt = 'probe-start'; throttledFor = [math]::Round($throttleSecs, 1) }
          } catch { Write-Log @{ evt = 'probe-release-failed'; err = $_.Exception.Message } }
        }
      } elseif ($state -eq 'PROBE') {
        if ($mbps -ge $BurstMBs) {
          try {
            $p3 = Get-Process -Id $TargetPid
            $p3.PriorityClass = 'Idle'
            $p3.ProcessorAffinity = [IntPtr]1
            $state = 'THROTTLED'; $throttleSecs = 0.0
            Write-Log @{ evt = 'probe-scanning'; mbps = $mbps; action = 're-throttle' }
          } catch { Write-Log @{ evt = 'rethrottle-failed'; err = $_.Exception.Message } }
        } else {
          $state = 'IDLE'; $burstRun = 0
          Write-Log @{ evt = 'scan-ended'; mbps = $mbps; action = 'restore: Normal+allcore' }
        }
      }
      Write-Log @{ evt = 'sample'; state = $state; mbps = $mbps }
    }
  }
  $lastRead = [double]$io.ReadTransferCount
  $lastTs = $now
}
Write-Log @{ evt = 'stop'; finalState = $state }
