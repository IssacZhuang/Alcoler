# E3-UI 前端架构测量结果（WebView 三变体 N=3 + Blitz 能力 spike）

> **文档状态：实验结果存档。** 三 WebView 变体为 N=3 轮转交错、单机（Ryzen 5 5600X / 32GB / Win11 / WebView2 155.0.4283.62）；Blitz spike 为 N=1 能力验证件，仅方向性有效。数据回答的是"前端架构如何决定 UI 内存行为"，**不构成对讨论报告候选 A/B 的门禁判定**（E3 外壳对比四框架、E5 全系统组合仍按实验设计文档执行）。

- 日期：2026-10-19
- 工具与工件：`experiments/e3-ui/`（场景服务器、三个前端变体、Tauri 壳、采样/运行/聚合 harness；正式数据 `results/official/`，本目录不入库，复现命令见文末）
- 动机：用户观察到类 ChatGPT 桌面应用（Rust 内核 + Electron/React UI）内存高、会话反复切换时飙升；且 JS 引擎 gc 后不向 OS 归还堆。本实验拆解两个问题：
  1. **会话切换的内存飙升有多少来自前端架构（JS 堆驻留），有多少来自渲染器本身（与 JS 无关的高水位）？**
  2. **不用 JS 的 web 技术渲染器（Blitz：Stylo CSS + Taffy + Vello，无 JS 引擎、无浏览器进程树）当前是否可用？**

## 1. 实验设置

### 1.1 被测变体（同一壳、同一场景、同一 DOM 结构）

| 变体 | 前端架构 | 会话数据位置 |
|---|---|---|
| react-resident | React 19，启动时全量拉取 16 会话完整历史（400 条/会话）驻留 JS 状态，切换零网络 | JS 堆（Map） |
| react-windowed | React 19，每次切换仅拉取最近 50 条窗口，旧数组丢弃 | JS 堆（瞬时） |
| flat | 无框架 HTML-over-the-wire：~15KB 微驱动，切换 = fetch 服务端渲染的 HTML fragment + `innerHTML` 替换 | 页面近乎无状态 |

- 壳：Tauri 2 最小壳（单窗口 1280×800，`--js-flags=--expose-gc`），宿主进程 3.7MB，二进制 8.6MB；三变体仅 `E3_URL` 不同，保证唯一变量是前端架构。
- 场景（确定性，种子 20261019）：16 会话 × 400 条消息（600–3400 字符，中英混排，每 5 条一段代码块）→ 加载后静置 3s → **50 次会话切换**（500ms 间隔）→ 显式 `gc()` → **30s 流式追加**（20 事件/s SSE）→ 静置 45s。
- 口径：进程树 Private Bytes 之和（1Hz 全进程采样，离线按 ppid 重建树）；JS 堆 `performance.memory.usedJSHeapSize`（1Hz beacon）；切换延迟 swapMs（中位/p95）。
- 检查点：after_load（加载后静置末）/ pre_gc（50 次切换后）/ post_gc / post_stream / final（静置末）；另记 peak(switch)。

### 1.2 壳空载锚点

同一壳加载空白健康检查页静置 45s（N=2 一致）：**树合计 151.1MB** = gpu 64.8 + browser 37.5 + renderer 19.0 + 其他 26.0（含 crashpad/utility/conhost）+ host 3.8。**GPU 进程占壳基座 43%**——WebView 桌面应用的"地板"由 Chromium 进程模型决定，与前端架构无关。

## 2. 结果：三 WebView 变体（N=3 中位）

### 2.1 主结论一：JS 堆大小完全由架构决定

| 变体 | 堆 after_load | 堆 final | 切换期间堆增长 |
|---|---|---|---|
| react-resident | 27.5 MB | 28.2 MB | +0.4~0.5 MB（驻留不变形） |
| react-windowed | 2.4 MB | 2.6 MB | +0.2~0.8 MB（瞬时） |
| flat | 0.9 MB | 0.7 MB | **−0.2 MB（零驻留）** |

16 会话全量驻留 = 27.5MB 恒定堆；窗口化降到 2–3MB；HTML-over-the-wire 降到 <1MB 且切换期间不增长。**"会话历史放 JS 状态"是可完全消除的堆来源**；消除后堆不再是问题，gc 归还与否也随之无关紧要。

### 2.2 进程树检查点（MB，N=3 中位）

| 检查点 | react-resident | react-windowed | flat |
|---|---|---|---|
| after_load | 201.3 | 161.1 | **159.4** |
| pre_gc（切换后） | 300.7 | 233.4 | 233.6 |
| post_gc | 231.8 | 195.2 | **184.2** |
| post_stream | 225.1 | 190.4 | 192.0 |
| final | 213.8 | 182.1 | **179.3** |
| peak(switch) | 308.7 | 238.1 | 234.6 |
| 切换树增长 | 101.7 | 71.6 | 74.2 |
| 全程树增长（final−after_load） | 12.5 | 20.7 | 19.8 |
| swapMs 中位/p95 | 0 / 0 | 4 / 5 | 4 / 6 |

### 2.3 主结论二：切换飙升存在第二个来源，与 JS 无关、所有变体共担

即使 flat 堆纹丝不动，50 次切换期间树仍 +74MB（窗口化 +72、驻留 +102）：高水位出现在 renderer（after_load 27.8 → 切换后 ~48-56）与 browser/gpu 进程（各 +5~7MB），来源是 DOM 反复销毁重建/HTML 解析的分配器高水位。`gc()` 只回收 JS 堆侧约 25~50MB 树内存；净残留（final−after_load）约 **+20MB/次重度切换场景**，属有界高水位而非线性泄漏。

即：**类 ChatGPT 的"切换即飙升"= JS 堆驻留（可消除，本实验量化为 −27MB 基线+线性扩展）+ 渲染器高水位（不可避免，~75MB 峰值 / ~20MB 残留）**。前者靠架构，后者只能靠减少 DOM churn（如复用容器、差量更新）缓解。

### 2.4 全量驻留的代价与收益不成比例

- 代价：renderer 基线 65.9MB vs 30.1/27.8MB（**+36~38MB**），且随会话数/历史长度线性扩展（16×400 条时堆 27.5MB、renderer 已 +38MB；真实产品会话更长更密）。
- 收益：切换延迟 0ms vs 4ms（本地 fetch + 渲染）。**4ms 不构成缓存全部会话的理由**；服务端才是会话数据的家（与内核权威状态原则一致）。

### 2.5 WebView2 路线的地板与天花板

- 地板（空壳）：151MB；加载 16 会话窗口后：159–161MB；重度使用一轮后 final：179–182MB；峰值：~235MB。
- 该地板与 Tauri/Electron 选择无关地由系统 WebView 进程模型决定（gpu 进程独占 65MB）。Electron 自带 Chromium 的对比数值留待 E3 外壳四框架正式对比。

## 3. Blitz spike：不用 JS 的 web 技术渲染器（N=1，能力验证）

被测：`dioxus 0.7` 的 `native` feature（dioxus-native 0.7.10 = blitz-dom 0.2.4 + blitz-shell 0.2.3 + Stylo + Taffy + Vello + AccessKit），单进程、无 JS 引擎、无浏览器进程树。spike 内容：16 会话侧栏 + 标题/中英混排段落/列表/代码块 + 50 条消息 + 30s×20 事件/s 流式追加 + textarea。源码 `experiments/e3-ui/blitz-spike/`。

### 3.1 能力结果（截图经视觉模型两次定点核验）

- ✅ 会话列表、代码块（等宽+深色底）、列表、50 条消息、流式追加 600/600 全部完成、输入框+按钮存在、中文段落整体可读。
- ❌ **中英混排文本中的全角标点（：与。）渲染为豆腐块**（如 `shaping：内核` 的冒号）；纯中文串内标点正常。属混排 run 字体回退缺陷（Parley/字体栈），对中文产品是实际风险。
- ❌ **textarea 的 `placeholder` 不渲染**。代码佐证：blitz-dom 0.2.4 全库无 placeholder 属性实现，仅 `stylo.rs:430` 的 `NonTSPseudoClass::PlaceholderShown => false` 桩。
- ⚠️ Markdown 代理块可用（p/pre 级），真实 Markdown 组件生态空白，需自建或移植。

### 3.2 IME（代码级检查，未做交互验证）

接线链完整：winit `WindowEvent::Ime` → `BlitzImeEvent{Enabled,Disabled,Preedit,Commit}`（blitz-shell `convert_events.rs:29`）→ focus 节点 editor 的 `set_compose`/`clear_compose`（预编辑串）与 `insert_or_replace_selection` + Input 事件派发（blitz-dom `events/ime.rs:26-40`）。即合成串与提交路径均已实现；**候选窗定位、实际中文输入手感仍需人工验证**（`cd experiments/e3-ui/blitz-spike && cargo run --release` 后手动输入）。

### 3.3 内存（单进程 Private Bytes）

| 场景 | 轨迹 |
|---|---|
| idle（无流式，150s） | 7.8MB（启动）→ ~45s 爬升至 **~427MB 平台** → 150s 末仍 426MB（走平，有界） |
| idle（无流式，45s 复测） | 100.5 → 427.2MB，与上一致 |
| 流式（600 事件/30s + 45s 静置，75s） | 101 → 峰值 672 → 末值 616MB |

静态窗口即爬到 427MB 且不回落。已排除应用侧驱动：spike 无 canvas/CSS 动画，blitz-shell 仅在 `is_animating()`（= 有 canvas 或活动 CSS 动画）时才自我续期重绘（`window.rs:227`）；增长来自渲染栈自身启动后的分配高水位（Vello/wgpu/字体栈的哪一层未定位，超出 spike 范围）。**结论：blitz 0.2.x 当前形态的内存行为比 WebView2 全家桶（idle 151MB）差 ~2.8×，不可用作主路径**；架构优势（单进程、无 JS、Rust 内核同栈）保留在案，待上游成熟后复测。

## 4. 对选型的含义（数据 → 输入，不构成决议）

1. **UI 内存问题主要不是"用什么渲染"，而是"状态放哪"**：JS 堆驻留（−27MB 且线性扩展）与 DOM churn 高水位（~75MB 峰值/次）都已量化；窗口化/HTML-over-the-wire 把前者清零。
2. **WebView2 + 无状态前端是当前数据下的最优方向**：16 会话重度场景 final 179MB（含 151MB 壳地板），峰值可控、无泄漏迹象。壳地板（尤其 gpu 65MB）是接受 WebView 路线必须接受的常数；Electron 对照数值由 E3 正式四框架对比补齐。
3. **"用 web 技术但不用 JS"的替代渲染器当前不可用**（Blitz：混排标点豆腐块、无 placeholder、idle 427MB）；但 JS 堆问题已由架构手段解决，替代渲染器的必要性下降。
4. flat 与 react-windowed 在内存上几乎等同（final 179.3 vs 182.1MB）；选择依据转为开发效率与差量更新能力（React 可做细粒度 diff 降 DOM churn；flat 需自建），留给 E4/E5。

## 5. 已知局限

- N=3：方向性有效，不足以触发实验设计文档第九节门禁；Blitz 部分 N=1。
- renderer 高水位的根因（Blink 分配器行为）未直接观测，由进程分摊与堆对照推断。
- 场景为确定性回放：无滚动、图片、虚拟化列表、真实 IME 交互；50 次切换间隔固定 500ms。
- 单版本 WebView2（155.0.4283.62）；flat 的 fragment 以 innerHTML 注入，实验件未做 XSS 加固。
- Blitz 截图核验借助视觉模型（两次定点一致），像素级渲染细节可能有遗漏。

## 6. 复现

> **注：实验代码（`experiments/e3-ui/` 全部源码与原始数据）已于报告归档后删除，以下命令仅作方法存档，无法直接执行。**

```bash
cd experiments/e3-ui
# 正式三变体 N=3 轮转交错（约 25 分钟）
node harness/runner.mjs --variants resident,windowed,flat --reps 3
node harness/aggregate.mjs            # → results/official/report.md
# 壳空载锚点 / Blitz spike
node harness/shell-idle.mjs           # 静置 45s
node harness/spike-run.mjs --secs 75  # 流式；E3_NOSTREAM=1 前缀 = idle 对照
```

前置：`server/`（零依赖 Node 24）、`ui/react-src`（esbuild 打包到 `ui/react/`）、`shell/`（cargo build --release 生成 e3ui-shell.exe）、`blitz-spike/`（cargo build --release）。详见 `experiments/e3-ui/README.md`。
