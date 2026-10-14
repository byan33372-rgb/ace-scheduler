# 模拟扫描器: 循环读文件+SHA256哈希, 模拟 SGuard64 的"枚举+读+哈希"行为
# 由控制文件驱动: 内容为 'run' 时突发读写盘, 其他值时休眠(模拟扫描间歇/结束)
param([string]$TargetFile, [string]$LogFile, [string]$ControlFile)
$ErrorActionPreference = 'Stop'
$buf = New-Object byte[] 4194304
$fs = [System.IO.File]::Open($TargetFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
$sha = [System.Security.Cryptography.SHA256]::Create()
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$bytes = 0; $winStart = $sw.Elapsed.TotalSeconds
while ($true) {
  $cmd = ''
  try {
    $fs2 = [System.IO.File]::Open($ControlFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $cmd = (New-Object System.IO.StreamReader($fs2)).ReadToEnd()
    $fs2.Dispose()
  } catch {}
  if ($cmd.Trim() -ne 'run') { Start-Sleep -Milliseconds 200; continue }
  $n = $fs.Read($buf, 0, $buf.Length)
  if ($n -le 0) { [void]$fs.Seek(0, [System.IO.SeekOrigin]::Begin); continue }
  [void]$sha.ComputeHash($buf, 0, $n)
  $bytes += $n
  if ($sw.Elapsed.TotalSeconds - $winStart -ge 1) {
    $mbps = [math]::Round($bytes / 1MB / ($sw.Elapsed.TotalSeconds - $winStart), 1)
    "{0}`t{1}" -f (Get-Date).ToUniversalTime().ToString('o'), $mbps | Add-Content $LogFile
    $bytes = 0; $winStart = $sw.Elapsed.TotalSeconds
  }
}
