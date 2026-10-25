# mvxxcb-restrain

Windows 用户态资源调度工具。针对后台安全扫描类进程的磁盘读取行为，在扫描活动与前台程序发生资源竞争时，通过调整该进程的 CPU 优先级、IO 优先级与处理器亲和性降低其调度权重；确认扫描结束后，将三项属性按原始快照完整恢复。不注入代码、不修改目标进程文件、不干预网络行为，被调度进程的功能完整性不受影响。

## 架构

单进程，两组线程：

- **工作线程**（500ms 周期）：目标发现、存活判定、IO 采样、状态机驱动、监测数据落盘。全部探测工作在后台线程完成，UI 线程只承担渲染。
- **UI 线程**：WinForms 托盘 + 控制面板（ON/OFF 开关、运行权限与状态显示）。跨线程 UI 更新经 `BeginInvoke` 编组。

组件职责（`src/AceScheduler/`）：

| 组件 | 职责 |
|---|---|
| `SchedulerStateMachine` | 四态调度状态机，全部计时基于采样时间戳的墙钟差分 |
| `ProcessThrottle` | 资源属性读改写与快照管理，自管最小权限句柄 |
| `IoSampler` | 累计读取字节数采样，快慢双路径 |
| `TargetDiscovery` | 按进程名发现扫描进程（不变量，不维护游戏列表） |
| `ForegroundMonitor` | 前台全屏判定（调度门控信号） |
| `SessionMonitor` | 会话时间线与总结报告，只读观测 |

## 调度状态机

```
IDLE ──连续 N 次采样 读速率 ≥ BurstMBs──▶ 前台忙 ▶ THROTTLED
                                     └─▶ 前台空闲 ▶ BURST_IDLE（不干预）
BURST_IDLE ──前台转忙──▶ THROTTLED
           └─速率静默──▶ IDLE（本次从未干预）
THROTTLED ──持续 ProbeSec──▶ PROBE（放开一个采样窗口）
PROBE ──速率仍高 × 前台忙──▶ THROTTLED
     ──速率仍高 × 前台空闲──▶ BURST_IDLE
     ──速率已低──▶ IDLE
```

PROBE 状态解决饥饿反馈回路：限速本身会将目标读速率压至接近零，仅凭速率无法区分"被限速压低"与"扫描自然结束"，周期性放开一个采样窗口核实是唯一可靠判据。限速施加失败（权限拒绝等）不会进入 THROTTLED：错误上报至 `LastError`、写入日志与时间线，退避 `RetryAfterFailSeconds` 后重试。

## 资源调度手段

限速 = 三项属性同时调整；解除 = 按进入前快照写回。

| 属性 | API | 限速值 |
|---|---|---|
| CPU 优先级 | `SetPriorityClass` | `IDLE (0x40)` |
| IO 优先级 | `NtSetInformationProcess(ProcessIoPriority)` | `VeryLow (0)` |
| 亲和性 | `SetProcessAffinityMask` | 单核 |

句柄以 `PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION` 打开，用后即关，不持有 .NET `Process` 对象。快照含三项属性与进程创建时间（`GetProcessTimes`），恢复后逐项读回校验（含可查询时的 IO 优先级）。≥64 逻辑处理器时亲和性掩码取全 1（规避 C# 移位按 `&63` 取模产生的 0 掩码）。

施加采用 all-or-nothing：任一属性写失败即回滚已施加项。IO 优先级仅在快照成功捕获原值时才设置，保证永远可恢复。

### 快照持久化与崩溃恢复

快照同步落盘至 `throttle-journal.json`（pid、进程名、创建时间、三项原值）。启动时回放：存活的、创建时间匹配的、且**真实进程镜像名（`QueryFullProcessImageName` 实时读取）命中扫描进程名单**的条目执行恢复；其余按结果分类——恢复成功/确认进程消失/pid 被复用/镜像名不在名单的条目移除，访问被拒或恢复部分失败的条目**保留至下次启动重试**，处理完毕后原子写回。journal 条目在读取时按不可信输入校验：创建时间必须非空、优先级必须在合法值集合、亲和性必须落在系统掩码内、IO 优先级限 0–4、进程名必须在名单内，上限 64 条 / 64KB，非法条目逐条记日志丢弃；文件损坏或超限时隔离为 `.corrupt-<时间戳>` 后从空开始。journal 写入失败时按 fail-closed 处理：拒绝施加新的限速，避免产生无恢复记录的不可恢复状态。

### pid 复用防护

快照与收养记录均携带进程创建时间。收养时记录；存活复核（`NullStreak` 达阈值时及每 2 秒的周期性身份探测）比对 `StartTime`，不匹配按目标死亡处理；限速复用旧快照前、解除写入前、丢弃前清理时均校验身份，不匹配则拒绝写入且不将旧快照应用于新进程，并持续拒绝直至该目标被重新收养。

IO 优先级是存储栈调度提示而非硬约束；对 SYSTEM 级或带受限 DACL 的目标进程，三项写操作均需管理员权限。

## 目标发现与存活判定

发现：`Process.GetProcessesByName` 匹配扫描进程名集合，去重后收养，仅提取 pid 与进程名即释放 Process 对象。

存活：完全基于采样器——连续 10 个采样周期读不到 IO 计数判定消失（约 5s）。不使用 `Process.Refresh()/HasExited`：对 SYSTEM 级进程二者会抛拒绝访问异常，实测曾导致每小时 582 次的误判收养/丢弃抖动。僵尸进程（强杀后仍被持有句柄）与旧值（实测约 2 分钟）两类假存活已修复：前者每次采样同时校验 `GetExitCodeProcess`，退出码非 `STILL_ACTIVE` 直接判死；后者仅当快路径以拒绝访问失败时才采信 WMI。NullStreak 达阈值后经 `Process.GetProcessById` 复核存活并比对创建时间：进程存活即采样器故障，保留目标与快照；确认死亡或 pid 被复用才走清理路径，清理时先尝试恢复，访问被拒或部分失败时快照保留在 journal 中待重试。另每 2 秒对每个目标做周期性身份探测，IO 可读的复用 pid 不依赖采样失败即被发现。同 pid 消失后 15 秒内不重复收养；从未成功采到过样本的目标按观测被拒退避 10 分钟并提示提权。

## 采样器

`IoSampler` 优先走 `GetProcessIoCounters`（亚毫秒，句柄仅需 `PROCESS_QUERY_LIMITED_INFORMATION`），其失败错误码用于目标状态分类：拒绝访问=存活受保护，回退 WMI `Win32_Process.ReadTransferCount`（约 1.3s/次）；其他错误（如 pid 已回收）直接判无样本，不采信 WMI 陈旧值。状态机节律全部以采样时间戳差分计算，不依赖采样次数。

## 监测与会话报告

`SessionMonitor` 以 JSONL 记录会话时间线：扫描进程出现/消失、速率样本（突发值 + 每 10s 心跳）、观测拒绝、调度事件、前台全屏进出、游戏进程启停。扫描进程静默 2 分钟后自动生成中文总结：运行权限、游戏后扫描尾巴时长与期间读取量、前台全屏时间占比、限速次数与累计时长、各进程读取量/峰值速率/采样健康度、磁盘 SMART 状态。监测为纯只读，不影响调度行为。

## 验证

沙盒闭环（`run-trial-cs.ps1` / `run-trial-multi.ps1`）：模拟扫描进程 + 前台争抢进程 + 生产引擎，六阶段协议（基线 / 突发限速 / 结束解除 / 再突发 / 空闲放行 / 静默收敛），单目标 7 项判定、3 目标 8 项判定。当前基线：限速比 0.36–0.41（限速期吞吐 / 基线吞吐），空闲放行恢复 91–92%，接管延迟 1.7s，解除延迟 3.6–4.5s，5 轮矩阵全过。`--selftest` 无头模式复用生产引擎，验证契约与试用版一致。

真机实测结论（1 小时会话 + 定向回归）：非提权运行时对 SYSTEM 级扫描进程的观测与调度均被 DACL 拒绝；判活抖动已修复（假目标稳定追踪，零抖动；限速中强杀目标，5 秒内判死并清理快照，恢复失败按 5 秒退避重试不刷日志）。阈值 `BurstMBs=5` 尚待真实读速率曲线校准。

## 已知限制

- IO 优先级为调度提示，NVMe 队列不饱和时效果打折；CPU 优先级与亲和性作为兜底
- 非提权进程无法观测/调度 SYSTEM 级目标（以日志与 UI 明示，非静默失败）
- `BurstMBs`、`ProbeSeconds` 等参数暂为默认值，未提供运行时配置
- 前台判定为混合信号：① 身份信号——前台窗口所属进程名命中用户可编辑的前台进程清单（`%LOCALAPPDATA%\mvxxcb-restrain\foreground-processes.txt`，每行一个进程名，支持 `#` 注释，修改保存后热生效）时视为前台忙；② 几何兜底——shell 官方 `SHQueryUserNotificationState`（`QUNS_BUSY`/`QUNS_PRESENTATION_MODE`）。身份信号与窗口模式、DPI、多显示器无关，覆盖 shell 不判为全屏的无边框窗口化程序；几何兜底保证清单为空时仍有全屏保护。弃用了 `GetWindowRect` 与屏幕尺寸比较的旧法：DWM 隐形边框使任何最大化窗口通过该判定，导致空闲放行状态几乎不可达。清单出厂为空，代码与文档不内置任何具体程序名

## 构建与运行

```powershell
# 构建（需 .NET 8 SDK；自包含单文件，约 72MB）
./build.ps1

# 沙盒闭环验证（复现上表数据）
./run-trial-cs.ps1          # 单目标六阶段
./run-trial-multi.ps1       # 3 目标并发

# 安装（per-user，LOCALAPPDATA + 登录自启）
./install.ps1

# 提权任务（管理员 PowerShell 运行一次；调度 SYSTEM 级进程的前提）
./register-task.ps1         # -Uninstall 移除
```

## 许可与免责

MIT License。本工具仅调用公开操作系统接口调整资源分配，不修改、不干扰任何第三方软件的功能与检测行为；与任何游戏厂商及安全厂商无关。调整第三方进程优先级存在理论与账号风险，使用后果由使用者自行承担，仅供学习研究。
