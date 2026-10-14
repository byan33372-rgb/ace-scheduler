# ACE 扫盘机制分析（基于本机二进制取证，2026-10-13）

对《无畏契约》（国服）与《明日方舟：终末地》共用的腾讯 ACE 反作弊扫盘功能的静态分析结论。分析机器：Windows 11 家庭中文版，单块 1TB NVMe SSD（SK hynix PC801）。

## 1. 组件部署

| 位置 | 内容 |
|---|---|
| `C:\Program Files\AntiCheatExpert\` | `ACE-BASE.sys`、`ACE-CORE*.sys`×3、`ACE-Service64.exe`、`SGuard\x64\`（`SGuard64.exe`、`SGuardSvc64.exe`、`Plugins\`：`ACE-ATS64.dll`、`ACE-CSI64.dll`、`ACE-DFS64.dll`、`ACE-DRV64.dll`、`ACE-SSC-DRV64.sys`） |
| 无畏契约 `...\Binaries\Win64\AntiCheatExpert\` | `ACE-DFS64.dll`（26MB，Disk/File Scan 插件）、`ACE-ATS64.dll`、`ACE-GAME.sys`、加密规则库 `ids.dat`(4.1MB)、`ACE-FAI.dat`(946KB) |
| 终末地 `games\Endfield Game\AntiCheatExpert\` | `ACE-Base64.dll`(62MB)、`ACE-CSI64.dll`、`ACE-Service64.exe`、`ACE-CORE.sys`×4 |

注册表：2 个按需启动服务（`AntiCheatExpert Service`=SGuardSvc64，LocalSystem；`AntiCheatExpert Protection`=ACE-Service64）+ 7 个内核驱动（ACE-ADVT / ACE-BASE / ACE-CORE10405/20405/30405 / ACE-GAME / ace-game-0）。

## 2. 扫盘 API（PE 导入表取证）

- **SGuard64.exe**（354 导入）：`GetLogicalDriveStringsA` → `GetDriveTypeW` → `FindFirstFileExW/FindNextFile` → `ReadFile`/`MapViewOfFile`；带 `SetThreadPriority`/`SetThreadAffinityMask`/`SwitchToThread` 自限速
- **ACE-DFS64.dll**：同套枚举 + `DeviceIoControl` + SetupAPI；内含 `LogicalDisk`/`HardDisk`/`SystemDisk` 字符串
- **终末地侧** `ACE-Service64.exe`/`ACE-CSI64.dll`/`ACE-Base64.dll`（679 导入）：同样的全盘枚举能力
- **ACE-BASE.sys**（270 导入）：`ZwCreateFile/ZwReadFile/ZwQueryInformationFile/IoCreateFile/IoVolumeDeviceToDosName` + `PsCreateSystemThread` + `KeSetPriorityThread`

## 3. 流程证据（字符串提取）

- `SCAN TABLE %s%s%s` —— 扫描结果入 SQLite（内嵌完整 SQLite 特征串）
- 内嵌 OpenSSL（`HMAC-SHA1`/`RSA-SHA1`/`AES-*`）—— 文件哈希比对
- 内嵌 libcurl + 域名 `anticheatexpert.com` / `down.anticheatexpert.com` —— 上报与规则拉取
- PDB 泄露路径 `C:\data\landun\workspace\CommonComponent\ACE-Guard\1.compile_source\...\SGuard64.pdb`（腾讯蓝盾 CI）
- 规则库加密：`ids.dat` 实测为带密码 Zip；`sguard.dat`/`SGuard.rc` 为加密二进制

## 4. 运行窗口（事件日志 + 文件时间戳）

- 游戏启动时连续安装/装载服务与驱动（7045 事件簇）；游戏退出后 SGuard 继续扫一段时间并自行退出（`ace-drc.dat` 在下机时刻落盘上报，之后进程清零）

## 5. 硬盘危害评估

**对 SSD 寿命的物理损耗 ≈ 可忽略。** 扫盘以读为主，TBW 只计写入；ACE 每会话落盘仅 KB–MB 级（`ace-drc.dat` 2.4MB 等）。实测机器 SMART Healthy、30 天系统日志无磁盘硬件错误。社区流传的"扫盘坏盘"更可能是内核驱动兼容性（蓝屏）个案与"读磨损"两个概念的混淆——真正值得在意的是：扫描期 IO/CPU 争抢（卡顿）、文件清单/哈希上报的隐私面、内核驱动稳定性。

## 6. 分析局限

- 本机无反编译引擎（Ghidra/IDA 未配置），流程描述基于导入表、格式字符串、配置结构、事件日志与公开资料交叉验证，未反编译扫描循环本体
- 规则库加密，扫描目标清单（黑名单路径/哈希）无法本地确认
- 蓝屏 dump 需管理员权限读取，未验证 2026-10-10/11 两次蓝屏与 ACE 驱动的关联

## 7. 公开参考

- [一分钟教你限制腾讯游戏ACE扫盘](https://www.cnblogs.com/ChenAI-TGF/p/19318828)
- [对ACE扫盘、硬盘损坏、掉盘或系统损坏的分析（B站）](https://www.bilibili.com/opus/1131815051781472306)
- [ACE Helper（GitHub）](https://github.com/NanCheng-L/ace-helper) · [FuckACE（GitHub）](https://github.com/shshouse/FuckACE)
- [腾讯游戏安全中心](https://gamesafe.qq.com/article/1178.shtml)
