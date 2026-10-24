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

句柄以 `PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION` 打开，用后即关，不持有 .NET `Process` 对象。快照含三项属性，恢复后读回校验。≥64 逻辑处理器时亲和性掩码取全 1（规避 C# 移位按 `&63` 取模产生的 0 掩码）。

IO 优先级是存储栈调度提示而非硬约束；对 SYSTEM 级或带受限 DACL 的目标进程，三项写操作均需管理员权限。

## 目标发现与存活判定

发现：`Process.GetProcessesByName` 匹配扫描进程名集合，去重后收养，仅提取 pid 与进程名即释放 Process 对象。

存活：完全基于采样器——连续 10 个采样周期读不到 IO 计数判定消失（约 5s）。不使用 `Process.Refresh()/HasExited`：对 SYSTEM 级进程二者会抛出拒绝访问异常，实测曾导致每小时 582 次的误判收养/丢弃抖动。真机回归发现两类"假存活"并已修复：被强杀但仍被父进程持有句柄的僵尸进程会持续应答 `OpenProcess` 并返回冻结的 IO 计数（速率 0 无法区分死亡与空闲），现在每次采样同时校验 `GetExitCodeProcess`，退出码非 `STILL_ACTIVE` 直接判死；WMI 对已消亡 pid 会返回陈旧值（实测可长达 2 分钟），现在仅当快路径以"拒绝访问"失败（目标存活但受保护）时才采信 WMI。限速中死亡的目标无法恢复原始参数，会显式记录日志并丢弃快照，避免 pid 复用污染。同 pid 消失后 15 秒内不重复收养；从未成功采到过样本的目标按"观测被拒"退避 10 分钟并提示提权。

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
- 前台全屏判定使用 shell 官方 `SHQueryUserNotificationState`（`QUNS_BUSY`/`QUNS_PRESENTATION_MODE`）。此前按 `GetWindowRect` 与屏幕尺寸比较会把任何最大化窗口误判为全屏（DWM 隐形边框使矩形每边大约 8px），导致空闲放行状态几乎不可达，已弃用。该接口对无边框窗口化游戏的判定跟随 shell 启发式，个别窗口化运行的游戏可能不被判为全屏（此时按桌面空闲策略放行扫描）

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
