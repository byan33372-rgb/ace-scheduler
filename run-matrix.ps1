# Run the closed-loop validation N times and report statistics.
param([int]$Runs = 5)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dir
$results = @()

for ($i = 1; $i -le $Runs; $i++) {
  Write-Host "----- run $i/$Runs -----"
  try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'run-trial-cs.ps1') 2>&1 |
      Where-Object { $_ -match 'OVERALL|FAIL' }
  } catch {
    Write-Host "run ${i} threw: $($_.Exception.Message)"
  }

  $rep = Get-Content (Join-Path $dir 'trial-report-cs.json') -Raw | ConvertFrom-Json
  $ph  = Get-Content (Join-Path $dir 'phases.json') -Raw | ConvertFrom-Json
  $lim = Get-Content (Join-Path $dir 'limiter.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }

  $p2s = [datetime]$ph.p2_start
  $p3s = [datetime]$ph.p3_start
  $throttles = @($lim | Where-Object { $_.evt -eq 'burst-detected' -or $_.evt -eq 'probe-scanning' })
  $firstThrottle = $throttles | Select-Object -First 1
  $firstRelease = $lim | Where-Object { $_.evt -eq 'scan-ended' } | Select-Object -First 1

  $engage = -1.0
  if ($firstThrottle) { $engage = [math]::Round(([datetime]$firstThrottle.ts - $p2s).TotalSeconds, 1) }
  $release = -1.0
  if ($firstRelease) { $release = [math]::Round(([datetime]$firstRelease.ts - $p3s).TotalSeconds, 1) }

  $m1 = [double]$rep.phaseMBs.p1
  $m2 = [double]$rep.phaseMBs.p2
  $ratio = if ($m1 -gt 0) { [math]::Round($m2 / $m1, 3) } else { -1 }

  $results += [pscustomobject]@{
    run     = $i
    pass    = [bool]$rep.allPass
    ratio   = $ratio
    m1      = $m1
    m2      = $m2
    m4      = [double]$rep.phaseMBs.p4
    applies = $throttles.Count
    releases = @($lim | Where-Object { $_.evt -eq 'scan-ended' }).Count
    engageSec = $engage
    releaseSec = $release
  }
}

$results | Format-Table -AutoSize

$passCount = @($results | Where-Object pass).Count
$ratios = @($results | ForEach-Object ratio | Where-Object { $_ -gt 0 })
$engages = @($results | ForEach-Object engageSec | Where-Object { $_ -ge 0 })
$releases = @($results | ForEach-Object releaseSec | Where-Object { $_ -ge 0 })

function Stats([double[]]$v) {
  $m = ($v | Measure-Object -Average -Minimum -Maximum)
  'min={0} mean={1} max={2}' -f [math]::Round($m.Minimum, 3), [math]::Round($m.Average, 3), [math]::Round($m.Maximum, 3)
}

Write-Host "===== matrix summary ($Runs runs) ====="
Write-Host "pass rate: $passCount/$Runs"
Write-Host "throttle ratio (m2/m1): $(Stats $ratios)"
Write-Host "engage latency s (P2 start -> first throttle): $(Stats ([double[]]$engages))"
Write-Host "release latency s (P3 start -> scan-ended): $(Stats ([double[]]$releases))"

$summary = @{ runs = $Runs; passCount = $passCount; results = $results } | ConvertTo-Json -Depth 4
$summary | Set-Content (Join-Path $dir 'test-matrix-results.json')
Write-Host 'saved: test-matrix-results.json'
