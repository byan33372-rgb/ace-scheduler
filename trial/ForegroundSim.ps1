# 前台模拟器: 钉在 core0 的普通优先级 CPU 负载, 代表"用户正在用的前台程序"
# 与被限速进程同核, 保证调度争抢必然发生 (生产版策略为低优先级+小核, 见 README)
param([string]$LogFile, [string]$ControlFile)
$ErrorActionPreference = 'Continue'
$me = Get-Process -Id $PID
$me.ProcessorAffinity = [IntPtr]1
$me.PriorityClass = 'Normal'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$ticks = 0; $winStart = $sw.Elapsed.TotalSeconds
while ($true) {
  $cmd = ''
  try { $cmd = [System.IO.File]::ReadAllText($ControlFile) } catch {}
  if ($cmd.Trim() -eq 'stop') { break }
  $x = 1.000001
  for ($i = 1; $i -le 300000; $i++) { $x = [math]::Sqrt($x + 0.000001) }
  $ticks++
  if ($sw.Elapsed.TotalSeconds - $winStart -ge 1) {
    "{0}`t{1}" -f (Get-Date).ToUniversalTime().ToString('o'), $ticks | Add-Content $LogFile
    $ticks = 0; $winStart = $sw.Elapsed.TotalSeconds
  }
}
