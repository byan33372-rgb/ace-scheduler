$ErrorActionPreference='Continue'
Set-Location 'D:\fieldtest'
Remove-Item field.stop, fg-state.txt -ErrorAction SilentlyContinue
Set-Content sguard.ctl 'run'
$holder = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','D:\fieldtest\fg-form.ps1'
Start-Sleep 3
$sg = Start-Process 'D:\fieldtest\SGuard64.exe' -PassThru -ArgumentList 'D:\fieldtest\target.bin','D:\fieldtest\sguard.ctl'
$stateDir = "$env:LOCALAPPDATA\mvxxcb-restrain"
Set-Content "$stateDir\enabled.txt" '1'
$app = Start-Process 'D:\Projects\ace-scheduler\dist\mvxxcb-restrain.exe' -PassThru
"sg1 pid=$($sg.Id) app pid=$($app.Id)"
Start-Sleep 28
Stop-Process -Id $sg.Id -Force
"scanner1 killed mid-throttle"
Start-Sleep 55
$sg2 = Start-Process 'D:\fieldtest\SGuard64.exe' -PassThru -ArgumentList 'D:\fieldtest\target.bin','D:\fieldtest\sguard.ctl'
"sg2 pid=$($sg2.Id)"
Start-Sleep 16
Stop-Process -Id $sg2.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 50
Set-Content "$stateDir\enabled.txt" '0'
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
New-Item field.stop -ItemType File -Force | Out-Null
Start-Sleep 2
Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
'e2e2-done'
