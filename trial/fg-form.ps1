param([string]$StopFile='D:\fieldtest\field.stop', [string]$StateFile='D:\fieldtest\fg-state.txt')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class SH { [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int st); }'
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle='None'; $f.StartPosition='Manual'
$f.Location = New-Object System.Drawing.Point(0,0)
$f.Size=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Size
$f.TopMost=$true; $f.BackColor=[System.Drawing.Color]::DarkRed
$f.Add_Shown({ $f.Activate() })
$t = New-Object System.Windows.Forms.Timer
$t.Interval=1000
$t.Add_Tick({
  $s=0; [SH]::SHQueryUserNotificationState([ref]$s) | Out-Null
  Add-Content $StateFile ("{0}`t{1}" -f (Get-Date).ToUniversalTime().ToString('o'), $s)
  if (Test-Path $StopFile) { $t.Stop(); $f.Close() }
})
$t.Start()
[void]$f.Show()
[System.Windows.Forms.Application]::Run($f)
