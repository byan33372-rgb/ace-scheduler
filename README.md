# AceScheduler

**调度式降低腾讯 ACE 反作弊（SGuard）扫描对前台性能的影响。** 只做资源调度：不注入、不断网、不改任何反作弊文件。

> Schedule-friendly throttling for Tencent ACE (SGuard) background scans.
> Resource scheduling only — no injection, no network blocking, no anti-cheat tampering.

## 这是什么

玩《无畏契约》《三角洲行动》《明日方舟：终末地》等使用 ACE（AntiCheatExpert）的游戏时，SGuard 扫描子系统会在游戏运行期间及退出后遍历磁盘文件做哈希比对，造成 IO/CPU 争抢和卡顿。

AceScheduler 的目标函数不是"让 ACE 越慢越好"，而是：

> **前台永远不输。** 只在"扫描突发 × 前台有程序需要资源"时让路；桌面空闲时解除全部限制，让扫描尽快跑完。

## 工作原理

三态状态机 + 探测-释放（probe-release）：

```
IDLE ──(连续 2 次采样 读速率 ≥ BurstMBs)──▶ THROTTLED（Idle 优先级 + 单核绑定）
THROTTLED ──(限速持续 ≥ ProbeSec)──▶ PROBE（放开一个采样窗口）
PROBE ──(速率仍高 = 还在扫)──▶ 回 THROTTLED
PROBE ──(速率已低 = 真扫完了)──▶ IDLE（恢复 Normal + 全核）
```

探测-释放解决一个关键陷阱：**限速本身会把扫描器的 IO 压到接近零，单看速率会把"被饿死"误判成"扫完了"**。周期性放开一个采样窗口核实，才能区分两者。

完整设计见 [docs/design.md](docs/design.md)，ACE 扫盘机制分析见 [docs/ace-scan-analysis.md](docs/ace-scan-analysis.md)。

## 当前状态

| 组件 | 状态 |
|---|---|
| `trial/` 试用版（PowerShell） | ✅ 四阶段闭环验证 5/5 PASS，限速比 0.465，饿死率实测 99% |
| `src/` 生产版（C# 单文件 exe） | 🚧 骨架已就位，见路线图 |

复跑试用版验证：

```powershell
cd trial
powershell -NoProfile -ExecutionPolicy Bypass -File .\run-trial.ps1
```

沙盒边界：只操作脚本自建的子进程，不需要管理员，不触碰 ACE/游戏进程。

## 路线图

- [x] **v0.1** 试用版：相位状态机 + 探测防振荡 + 闭环验证
- [ ] **v0.2** 目标发现自动化：按服务名/进程名发现 SGuard（不维护游戏名单），SCM 7045 事件（"服务已安装在系统中"）触发，零常驻
- [ ] **v0.3** 前台感知：全屏/前台窗口判定真实冲突；可选反向限流（扫描期暂停 OneDrive/Steam 下载等自家 IO 大户）
- [ ] **v0.4** 可安装 exe：C# WinForms 托盘 + 自包含单文件发布 + 安装器
- [ ] **v0.5** 可观测：扫描进度估算、上传端点被动记录、磁盘健康对账

## 红线（也是安全边界）

本工具与 ace-helper、FuckACE 等前人项目保持同一条线：

- ✅ 只用公开 API 调整资源分配（优先级 / 亲和性 / IO 优先级 / EcoQoS）
- ❌ 不注入 SGuard / 不 Hook 函数 / 不阻断驱动加载
- ❌ 不断网、不篡改上报、不改 ACE 任何文件

跨越这条线就从"资源调度"变成"妨碍反作弊运行"，是封号条款的核心打击面。

## 免责声明

本项目与腾讯及 ACE 反作弊系统无任何官方关联。修改第三方进程的优先级存在理论风险，作者不对任何账号、系统或数据损失负责，仅供学习研究。

## License

[MIT](LICENSE)
