# AceScheduler 试用版（沙盒验证通过）

降低腾讯 ACE 反作弊（SGuard）对电脑性能影响的外部工具原型。四阶段闭环验证 **5/5 PASS**（2026-10-14）。

## 核心机制

```
IDLE --(连续2次采样 读速率>=BurstMBs)--> THROTTLED（Idle优先级+core0）
THROTTLED --(限速满 ProbeSec 秒)--> PROBE（放开一个采样窗口）
PROBE --(速率>=BurstMBs=仍在扫描)--> 回 THROTTLED
PROBE --(速率低=扫描真结束)--> IDLE（恢复 Normal+全核）
```

探测-释放（probe-release）是本设计的关键：单纯按速率判断"扫描结束"会被限速自身的饿死效应骗到（IO 掉零 ≠ 扫完），每 5 秒放开一个采样窗核实。

## 文件

- `AceScheduler.Trial.ps1` — 限速器状态机（试用核心）
- `DummyScanner.ps1` — 模拟扫描器（读+SHA256，模拟 SGuard 行为）
- `ForegroundSim.ps1` — 前台争抢模拟器（Normal 优先级钉 core0）
- `run-trial.ps1` — 四阶段编排器 + 判定报告
- `trial-report.json` — 最近一次验证报告

## 复跑

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\run-trial.ps1
```

沙盒边界：只操作本目录脚本创建的子进程；不需要管理员；不触碰 ACE/游戏进程；限速手段仅优先级+亲和性（与社区工具同红线：不注入、不断网、不改 ACE 文件）。

## 已知与生产版差距

- 目标进程由 PID 指定（生产版按服务/进程名发现，7045 事件触发）
- 前台感知用固定争抢模拟（生产版按前台窗口判定冲突）
- 吞吐度量在编排器侧（生产版可下放到限速器自身采样）
