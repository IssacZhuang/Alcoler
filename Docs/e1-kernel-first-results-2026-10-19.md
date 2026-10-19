# E1 内核测量首测结果（medium 剖面，单重复）

> **文档状态：初步测量数据存档。** 单重复（N=1）、单机（Ryzen 5 5600X / 32GB / Win11）、medium 剖面（60 turns × 150 事件 × 5ms + 每 turn 一次 fs.read + 隔 turn 一次 exec(500ms) + 20%… 见下）。数据方向性有效，**不足以触发实验设计文档第九节的门禁判定**；门禁需要 standard 剖面与 N≥3。

- 日期：2026-10-19（四内核首测）；2026-10-19 追加两项补充变体（见第 5、6 节，用户指定：[1] Rust→WASM、[2] Bun 对比 Node）
- 工具与工件：`experiments/e1-kernel/`（全部 7 个变体通过黑盒契约一致性测试；四内核首测数据 `results/official-medium/`，七变体数据 `results/official-medium-7v/`，本目录不入库，可随时用 `node harness/runner.mjs --profile medium --reps 1` 复现）
- 四个内核：Node 24（node:sqlite + fetch）、Go 1.23（goroutine + modernc/sqlite）、Rust 1.97（std 线程 + 手写 SSE 客户端 + rusqlite bundled）、.NET 8（Task + Microsoft.Data.Sqlite，JIT 宿主）
- 口径：进程树 Private Bytes 之和；检查点 = idle / 任务后 / gc 后 / 峰值；TTFT 等时延为同机时钟差

## 1. 稳态内存（private MB）

| 场景 | Node | Go | Rust | .NET |
|---|---|---|---|---|
| idle（1 会话） | 26.2 | 18.9 | **6.2** | 13.4 |
| idle（16 会话） | 39.0 | 21.3 | **7.2** | 13.3 |
| 任务后驻留（16 会话） | 145.0 | 28.7 | **7.8** | 41.4 |
| gc 后驻留（16 会话） | 144.3 | 25.7 | **7.8** | 41.4 |
| 峰值（16 会话） | 230.6 | 215.1 | 194.5 | 221.4 |

要点：

- **Rust 稳态一档领先**（6–8MB），且任务后不增长——无 GC、缓冲随 turn 释放。
- **Node/V8 的驻留最高且 gc 后不回落**（145MB）：`--expose-gc` 后仍不向 OS 归还，验证了讨论报告 5.3 对 Node 基础开销的顾虑在多会话下会被放大。
- **Go/.NET 任务后回落良好**（GC 生效），稳态分别为 ~26 / ~41MB。
- **16 会话峰值四者趋同（194–231MB）**：峰值由瞬时分配行为主导（每事件 JSON 解析/序列化），而非运行时底座大小。Rust 峰值与其余相当，主因是每 SSE 事件一次 serde_json Value 分配——这是实现选择，不是语言上限。
- Go 峰值跨重复波动极大（此前两轮同剖面分别为 558MB / 168MB / 215MB）：GOGC 放堆时机敏感，**峰值指标必须多重复取分位数**，单次不可排名。

## 2. 时延（ms）

| 指标 | Node | Go | Rust | .NET |
|---|---|---|---|---|
| 启动到 ready（中位） | 83 | 35 | 30 | 134 |
| TTFT / 流式滞后（中位~p95） | 0–2 | 0–1 | 0–1 | 0–1 |
| fs.read（中位） | 0.8 | 0 | 0.2 | 0.2 |
| exec 取消→结果 p95（16 会话） | 4 | 4 | 7 | **409** |

- 本地回环下流式转发开销四个内核都是 0–1ms，**无区分度**（与实验设计预期一致；真实网络/远端模型下的差异留待 E5）。
- **.NET 取消响应随并发恶化**（1/4/16 会话：85/225/409ms p95）：`WaitAsync(cts.Token)` 的续体在线程池排队。属于实现可修复项，但它恰是"取消传播语义实现成本"的一个数据点（讨论报告第八节继承行为 D1）。

## 3. 正确性卫生

四内核首测 12 次运行 + 七变体 21 次运行（含 feed 6 次复测）全部：退出码 0、无 error 事件、无孤儿 `e1-sleeper.exe` 进程、compaction 与取消语义符合契约（feed 变体按无 exec 口径）。七种实现（四语言 + Bun 复用 + Rust 双目标）走同一黑盒契约均可行。

## 4. 已知局限与下一步

- 单重复；Go 峰值波动证明必须 N≥3（standard 剖面 N=7）才能做门禁判定。
- 采样器偶发漏采活进程 pid（Get-Process 与 CIM 两次枚举竞态），聚合已用"检查点前回溯 ≤5s 找包含 pid 的最近快照"兜底；峰值仍可能被低估。
- TTFT/滞后在回环无区分度，若需微秒级需改造内核时间戳（当前 ms）。
- .NET 以 JIT 宿主运行，未测 Native AOT；AOT 会改善其启动与稳态，属后续可选加测项。
- 下一步（按实验设计文档）：`--profile standard --reps 7` 全量统计；E4 标准变更任务（在现有四内核 spike 上做 D1–D5）；E2/E3/E5 未启动。

## 5. 补充实验 B：Bun 对比 Node（同一份 JS 内核源码，2026-10-19）

`kernels/node/kernel.mjs` 不加修改同时跑在 Node 24 与 Bun 上（运行时分支仅两处：`node:sqlite`→`bun:sqlite` 回退、`global.gc()`→`Bun.gc(true)`）。数据：`results/official-medium-7v/`，1/4/16 会话，单重复，21 运行全部干净退出（退出码 0、无 error、无孤儿）。

### 5.1 稳态内存（private MB）

| 场景 | Node | Bun |
|---|---|---|
| idle（1 会话） | 35.1 | **252.5** |
| idle（16 会话） | 38.4 | **252.4** |
| 任务后驻留（16 会话） | 143.8 | **275.0** |
| gc 后驻留（16 会话） | 142.6 | **275.3** |
| 峰值（16 会话） | 256.6 | **421.7** |

### 5.2 时延（ms，与 Node 同级）

| 指标 | Node | Bun |
|---|---|---|
| 启动到 ready（中位） | 79 | 84 |
| TTFT / 流式滞后（中位） | 0–2 | 0–2 |
| exec 中位 / 取消→结果 p95 | 508 / 2–4 | 509 / 3–5 |
| fs.read（中位） | 0.9 | 0.2 |

要点：

- **Bun 的运行时底座比 Node 重一个量级**：idle 252MB vs 35MB，且 16 会话下驻留 275MB vs 143MB、峰值 422MB vs 257MB。JavaScriptCore + Bun 自带标准库的常驻成本是主要来源；对"内核作为长驻小进程"的目标形态是明显劣势。
- **执行速度与正确性无差异**：SSE 转发、SQLite 持久化、exec 取消、gc 契约全部与 Node 同级，两轮一致性测试均通过。Bun 不是能力问题，是 footprint 问题。
- 实现摩擦一处：Bun 的 `fetch` 对无 Content-Length/Transfer-Encoding 的 close-delimited 响应会挂起，mock provider 已改为预算体并显式设 Content-Length（有限回放下语义等价）。对真实远端模型流（SSE/chunked）通常不触发，但值得记录。

## 6. 补充实验 A：Rust→WASM（同一 Rust 源码，原生 vs wasm32-wasip1/wasmtime，2026-10-19）

`kernels/rust-feed` 一份源码双目标：`e1feed.exe`（原生）与 `e1feed.wasm`（wasmtime v49 运行）。feed 内核与 TCP 内核的差异：WASI p1 无 socket → 事件由驱动器按相同节奏从 stdin 喂入；无进程派生 → 无 exec 工具；无线程 → 单线程状态机；无 C 工具链 → 持久化为 JSONL 追加而非 SQLite。因此 **A/B 只在 rustfeed 与 wasm 之间做**（隔离 WASM 运行时开销），不与 TCP 四内核直接排名。

### 6.1 内存（private MB，进程树=单进程）

| 场景 | rustfeed（原生） | wasm（wasmtime） | 差 |
|---|---|---|---|
| idle（1 会话） | 5.96 | 9.75 | +3.8（×1.6） |
| idle（16 会话） | 5.96 | 9.80 | +3.8 |
| gc 后驻留（16 会话） | 6.18 | 9.75 | +3.6 |
| 峰值（16 会话） | 6.20 | 9.87 | +3.7 |

### 6.2 时延（ms）

| 指标 | rustfeed | wasm |
|---|---|---|
| 启动到 ready（中位） | 19–23 | 31–35（+11ms 实例化） |
| TTFT 中位 / p95 | 0–2 / 1–4 | 0 / 1 |
| 流式滞后中位 | 0 | 0 |
| fs.read（中位） | 0.1–0.2 | 0.1–0.2 |

要点：

- **WASM 开销是固定底座差，不是增长率**：+3.8MB 恒定，16 会话下 wasm 线性内存不增长（流式工作负载 + 每 turn 截断，驻留数据量有界）。wasmtime 引擎编译/JIT 常驻约占这 3.8MB。
- **执行时延零差异**：单线程状态机在 16 会话交错下 TTFT/滞后仍为 0–2ms——该工作负载下无线程不是瓶颈。
- **真正的代价在能力边界而非性能**：WASI p1 四项约束（socket/派生/线程/C 工具链）全部被实测证实。WASM 形态的内核要接入真实模型流与 exec 工具，必须由宿主提供 network 与 process 扩展（wasmtime 的 `wasi:http`/自定义 host function 或 component model）——这是架构耦合成本，不是微秒级开销。
- 持久化降级（JSONL 追加 vs SQLite）在此工作负载下无性能差别，但真实内核的会话恢复/查询需求未测。

### 6.3 测量方法备注

feed 变体的 TTFT 口径 = 驱动器写入首个 text_delta 的时刻 → 内核发出首个 model_delta 的时刻（管道传输），与 TCP 变体（provider 日志 join）口径不同，数值均为 0–2ms、无区分度，只用于确认"喂入传输本身不是瓶颈"。首版驱动器曾把每个 delta 相对本 turn 首发时刻的偏移都计入 TTFT（中位数恰好≈半个 turn 时长 1159ms），已修复为仅计首个 delta 并复测；内存/ready/正确性数据不受影响。

## 7. 追加局限

- Node 基线 idle 在两次正式运行间波动（26 vs 35MB，同机同参），单重复下跨轮比较需谨慎；Bun 的 252–266MB 则非常稳定。
- feed 变体无 exec/取消路径，取消语义（第八节 D1）在 WASM 形态下未测——依赖宿主扩展设计，留待 E5。
- wasmtime 为外置 CLI 宿主；嵌入宿主（如 Tauri wry / 自建 host）的实例化与内存口径可能不同，数字只代表"wasmtime CLI 进程树"。
