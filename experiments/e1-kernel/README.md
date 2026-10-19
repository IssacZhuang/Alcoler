# E1：内核语言对比实验

对应 `Docs/tech-stack-comparison-experiment-design-2026-10-19.md` 的 E1 + E0 所需子集。比较多种语言/运行时形态实现的**最小 Agent 内核**在相同确定性工作负载下的资源与性能。

**七个被测变体：**

| 变体 | 内核 | 传输 | 说明 |
|---|---|---|---|
| node | `kernels/node` | TCP/SSE | Node 24，node:sqlite + fetch |
| bun | `kernels/node`（同源） | TCP/SSE | Bun 运行同一份 JS（bun:sqlite 回退），JSC vs V8 |
| go | `kernels/go` | TCP/SSE | goroutine + modernc.org/sqlite |
| rust | `kernels/rust` | TCP/SSE | std 线程 + 手写 SSE 客户端 + rusqlite bundled |
| dotnet | `kernels/dotnet` | TCP/SSE | .NET 8 Task + Microsoft.Data.Sqlite（JIT 宿主） |
| rustfeed | `kernels/rust-feed` | stdin feed | 与 wasm 同源编译为 native，作为 wasm 的 A/B 基线 |
| wasm | `kernels/rust-feed` | stdin feed | wasm32-wasip1，wasmtime 49 运行（WASM 沙箱形态） |

feed 传输是 WASM 变体的适配：WASI p1 无 TCP，模型事件由 driver 按同样节奏写入 stdin（`model_event` 命令）；WASI 无子进程且无 C 工具链编 SQLite，故 feed 内核不含 exec 工具、持久化为 JSONL 追加。这些约束差异是 WASM 形态的选型数据，不是测量误差。rustfeed 与 rustfeed-wasm 同一份源码两个目标，A/B 差值即 WASM 运行时开销。

Browser Use 不在本实验范围：内核只定义工具/插件调用边界（`tool_call` / `tool_result`），浏览器能力未来作为 built-in plugin 走同一边界接入，与内核解耦。

## 目录

```text
provider/        确定性 SSE mock provider（Node，零依赖）
tools/sleeper/   e1-sleeper.exe 助手（受控子进程，exec 工具的被测目标）
kernels/node     Node 内核（node:sqlite + fetch）
kernels/go       Go 内核（goroutine + modernc.org/sqlite 纯 Go 驱动）
kernels/rust     Rust 内核（std 线程 + 手写 TCP/SSE 客户端 + winsqlite3）
kernels/dotnet   .NET 8 内核（Task + Microsoft.Data.Sqlite）
harness/driver.mjs    协议驱动器：spawn 内核、发命令、计时、取消注入
harness/conform.mjs   契约一致性测试（黑盒，语言中立）
harness/runner.mjs    测量运行器：交错执行、内存采样、TTFT 对齐、报告
harness/sampler.ps1   进程级内存采样（ts,pid,ppid,name,private,ws @1Hz）
results/         每次运行一个目录：runs.csv / report.md / sampler.csv / provider.log / events/
```

## 契约 v1.1（黑盒）

命令（stdin，JSONL）：

```json
{"cmd":"start_session","id":"s0","profile":{"turns":60,"eventsPerTurn":150,"intervalMs":5,"budgetTokens":12000,"provider":"http://127.0.0.1:8990"}}
{"cmd":"cancel","session":"s0","turn":20}
{"cmd":"gc"}
{"cmd":"shutdown"}
```

事件（stdout，JSONL，`ts` 为宿主 epoch 毫秒）：`ready`、`session_started`、`turn_started`、`model_delta{seq,bytes}`、`tool_call`、`tool_result{status,durationMs}`、`compacted{kept_tokens}`、`turn_done`、`session_done`、`gc_done{forced}`、`error`。`shutdown` 后 2.5 秒内进程树退出、退出码 0。

内核固定实现：SSE 流转发、fs.read/exec 工具（元数据+权限桩+取消+进程树清理）、按 token 预算裁剪最旧工具结果、SQLite WAL 每 turn 落盘、1/4/16 并发会话。

## 构建一次

```bash
cd experiments/e1-kernel
go build -o bin/e1-sleeper.exe ./tools/sleeper        # 在 tools/sleeper 模块内执行
cd kernels/go && go mod tidy && go build -o e1kernel.exe . && cd ../..
cd kernels/rust && cargo build --release && cd ../..   # .cargo/config.toml 已固定 CC=gcc 与 CFLAGS
cd kernels/rust-feed && cargo build --release && cargo build --release --target wasm32-wasip1 && cd ../..
cd kernels/dotnet && dotnet build -c Release && cd ../..
# 前置：bun 在 PATH（npm i -g bun）；bin/wasmtime.exe（GitHub releases 下载 x86_64-windows）
#       rustup target add wasm32-wasip1
```

## 运行

```bash
# 一致性（黑盒契约）测试，逐变体或 all（node,bun,go,rust,dotnet,rustfeed,wasm）
node harness/conform.mjs all

# 测量（smoke 仅 1 会话；medium 1/4/16 会话；standard 为设计文档默认剖面，单轮约 20 分钟/会话档）
node harness/runner.mjs --profile medium --langs node,bun,go,rust,dotnet,rustfeed,wasm --reps 1
node harness/runner.mjs --profile standard --reps 7   # 完整统计口径，隔夜跑
```

结果在 `results/<profile>-<时间戳>/`：`report.md`（汇总表）、`runs.csv`（每行一次运行）、`sampler.csv`（全进程采样）、`events/`（非 delta 事件流 + 每 run 的 `*.meta.json` 检查点元数据）。

测量结束后可随时离线重算（不重跑内核）：

```bash
node harness/aggregate.mjs results/<目录>
```

## 口径与注意事项

- 内存主口径 = 进程树 **Private Bytes 之和**（无共享页重复计数）；Working Set 仅供参考。
- 检查点：idle（启动静置后）、settle（任务结束+1.5s）、settle2（`gc` 命令后）。Rust 无强制 GC/堆 trim，`gc_done.forced=false`，settle2 与 settle1 接近是预期行为。
- TTFT / stream-lag 用 provider 日志与内核事件的同机时钟差计算。
- 取消注入：每第 20 个 turn（exec turn）在 `tool_call` 后 100ms 发 `cancel`，期望 `exec` 结果 status=cancelled。
- 孤儿检查：按进程名查 `e1-sleeper.exe`。
- feed 变体（rustfeed/wasm）的 TTFT/流式滞后 = driver 写入 stdin 时刻与内核 `model_delta` 时刻之差（含管道），与 TCP 变体的 provider 对齐口径**不可直接互比**，只在 feed 变体族内部比较。
- wasm 变体的 ready 时间含 wasmtime 启动与模块编译；内存为 wasmtime 进程树（模块线性内存计入其私有内存）。
- Rust SQLite 为 rusqlite `bundled`（sqlite3.c 由本机 MinGW gcc 编译，见 `kernels/rust/.cargo/config.toml`）；Node/Bun 用各自内置 SQLite（node:sqlite / bun:sqlite）；Go 用 modernc 纯 Go 移植；.NET 用 Microsoft.Data.Sqlite（捆绑原生 SQLite）；feed 变体持久化为 JSONL（WASI 无 C 工具链）。驱动形态本身也是被测实现的一部分，报告需注明。
- .NET 以 `dotnet <dll>` JIT 宿主运行（未做 AOT），代表默认部署形态；如需 AOT 对比另行加测。
