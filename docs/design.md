# AceScheduler 设计文档

## 1. 问题定位

ACE（AntiCheatExpert）的扫盘由 SGuard 子系统实现：`SGuard64.exe`（LocalSystem 服务，游戏退出后仍运行）+ 游戏内 `ACE-Service64.exe` + 5 个内核驱动。扫描路线是经典的全盘枚举：`GetLogicalDriveStrings → GetDriveType → FindFirstFileEx 递归 → ReadFile/MapViewOfFile → MD5/SHA1 哈希 → SQLite 比对（加密规则库）→ curl 上报`。


社区既有工具（ace-helper、FuckACE）的共性短板：

1. **优先级是伪杠杆**——SGuard 自己已 `SetThreadPriority` 低速运行，再压一档对 IO 密集扫描几乎无感；
2. **恒定压制延长尾巴**——压得越死它赖在盘上越久；
3. **3 秒轮询 + 游戏名白名单**——反应慢、维护累；
4. **黑箱**——用户看不到效果。

## 2. 核心设计

### 2.1 目标函数反转

不是"让 ACE 慢"，是"**前台永远不输**"：

- 扫描突发 **×** 前台有程序需要资源 → 限速（Idle 优先级 + 小核绑定 + 可选 EcoQoS）
- 扫描突发 **×** 桌面空闲 → 解除全部限制，让它跑完赶紧走
- 可选反向限流：扫描期暂停 OneDrive/Steam 下载等自家 IO 大户（管理自己的应用，零风险）

### 2.2 三态状态机与探测-释放

```
IDLE ──连续 2 次采样 读速率≥BurstMBs──▶ THROTTLED
THROTTLED ──限速满 ProbeSec──▶ PROBE（放开一个采样窗口）
PROBE ──速率≥BurstMBs──▶ THROTTLED（还在扫）
PROBE ──速率低──▶ IDLE（真扫完了，恢复 Normal+全核）
```

**为什么需要 PROBE**：限速把扫描器饿死后，其 IO 速率趋近于零——单看速率会把"被饿死"当成"扫完了"。沙盒实测：被限速进程 3 秒只分到 31ms CPU，但探测窗一放开速率立刻回弹。每 ProbeSec（默认 5s）放开一个采样窗核实，是区分二者的唯一可靠手段。

**实现教训（沙盒验证得出，生产版必须遵守）**：

- 阶段吞吐必须由**观察方**采样 `ReadTransferCount` 差分计算——被饿死的目标不再写自己的日志，"取目标自报数据"在完全饿死时天然失明；
- Windows PowerShell/`[datetime]` 强转会按本地时区解释 ISO 时间戳，`Parse(…, RoundtripKind)` 保留 UTC，混用差 8 小时（zh-CN 环境）；
- CIM 采样在负载下每 tick 实测 ~1.3s，任何按 tick 计数的节律都要换成按真实秒数累计。

### 2.3 目标发现：按不变量，不按游戏名单

ACE 每次游戏启动都会向 SCM 重装服务（事件 ID **7045**，"服务已安装在系统中"，服务名 `AntiCheatExpert *`）。这提供了两个自动化锚点：

- **发现**：服务名 / 驱动服务键（`ACE-*`）/ 任意进程加载 `ACE-Base64.dll`，新 ACE 游戏自动覆盖；
- **触发**：任务计划程序订阅 7045 事件，ACE 出现的毫秒级时刻唤醒工具，会话结束自退出——**零常驻**，不需要后台轮询。

### 2.4 结束信号

- 游戏进程树退出 + 服务状态翻转 = 内核保护解除窗口；
- `C:\ProgramData\AntiCheatExpert\ace-drc.dat` 的写入是会话结束上报的落盘点（实测与下机时间吻合），可作为"扫描收工"的高精度信号。

## 3. 安全红线（与 ace-helper / FuckACE 同线）

✅ 只用公开 API 调整资源分配：优先级 / 亲和性 / IO 优先级 / EcoQoS / CPU Sets
❌ 不注入 SGuard、不 Hook、不阻断驱动加载、不断网、不篡改上报、不改 ACE 文件

越线 = 从"资源调度"变成"妨碍反作弊运行"，是封号条款的核心打击面。

## 4. 生产版路线（目标：可安装 exe）

| 版本 | 内容 | 验证方式 |
|---|---|---|
| v0.1 ✅ | PowerShell 试用版：状态机 + 探测防振荡 + 四阶段闭环 | `trial/run-trial.ps1`，5/5 PASS |
| v0.2 | C# 核心：IoSampler（WMI/ETW）+ StateMachine 移植 + 目标发现（服务名/进程名） | 单元化四阶段沙盒复用 |
| v0.3 | 7045 事件触发（任务计划程序）+ 前台窗口冲突判定 + 托盘 UI | 手动 + 事件注入测试 |
| v0.4 | `dotnet publish` 自包含单文件 exe + 安装器（Inno Setup / winget manifest） | 干净机安装卸载 |
| v0.5 | 可观测：扫描进度估算、上传端点被动记录、磁盘健康对账 | 长期使用数据 |

### 构建说明（v0.4）

```powershell
# 需 .NET 8 SDK（无管理员：dotnet-install.ps1 装到 %LOCALAPPDATA%\Microsoft\dotnet）
cd src/AceScheduler
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# 产物 ~70MB 单文件；若目标机有 .NET 8 桌面运行时可改 framework-dependent（~1MB）
```

杀软误报是此类工具的最大分发障碍：中性命名 + 发布 SHA-256 校验是底线，根治需代码签名（Azure Trusted Signing ~$10/月）。
