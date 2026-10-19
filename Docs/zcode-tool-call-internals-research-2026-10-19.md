# ZCode 工具调用机制研究报告

> **文档状态：已完成的研究笔记。** 基线仓库 `C:\Projects\ZCode`（v3.14.3，只读研究，未修改）与本机运行数据（`~/.zcode`）的源码级分析，为内核技术选型实验（`tech-stack-comparison-experiment-design-2026-10-19.md` 的 E1/E4）提供工具子系统设计参考。

- 研究日期：2026-10-19
- 基线版本：zcode 3.14.3（pnpm monorepo，TypeScript/Node）
- 研究方法：源码精读 + 三路并行探索（工具清单 / 调用链路 / MCP 与插件）+ 本机会话数据抽样交叉验证

## 一、总体架构：工具系统的四层分工

工具子系统横跨四个包，职责边界清晰：

| 层 | 位置 | 职责 |
| --- | --- | --- |
| contracts | `apps/zcode-cli/packages/contracts/src/tools/` | 每个工具的 zod 输入 schema + JSON Schema 转换；工具契约类型（权限/预算/超时/取消/追踪的声明结构） |
| core | `apps/zcode-cli/packages/core/src/tool/` | ToolEntry 注册表、调度器、执行器管线（校验→hook→权限→超时→handler）、结果序列化与预算 |
| adapters | `apps/zcode-cli/packages/adapters/src/model/` | 与 Vercel AI SDK 的桥接：内部 `ModelToolContract` ↔ OpenAI / Anthropic / openai-compatible 三类 wire format；流式 tool_call 解析 |
| runtime | `apps/zcode-cli/packages/core/src/runtime/` | turn 循环、消息历史、SQLite 会话持久化、MCP 初始化接线 |

关键设计决策：**MCP 工具与内置工具共用同一个 `ToolEntry` 接口、同一个 registry、同一个 executor**——外部工具不是特殊路径，而是被包装成同构条目（见第六节）。

## 二、工具如何定义

### 2.1 核心类型

`ToolMetadata`（`core/src/tool/types.ts:67`）——每个工具的能力与安全元数据：

```ts
interface ToolMetadata {
  name: string;
  description?: string;
  modelInstructions?: readonly string[];  // 拼成 description 尾部的 "Usage:" 块
  allowedInPlanMode?: boolean;
  readOnly: boolean;
  destructive: boolean;
  concurrentSafe: boolean;
  requiresUserInteraction?: boolean;
  timeoutMs?: number;
  maxOutputBytes?: number;
  sideEffectScope: "none"|"workspace"|"git"|"network"|"system"|"session"|"userInteraction";
  riskLevel: RiskLevel;
  needsApproval: boolean;
  providerVisible?: boolean;   // false = 模型不可见（如 Task 别名）
  stopTurnOnSuccess?: boolean; // 终态工具：submit_result 成功即结束 turn
  mcpPresentation?: { serverName; toolName; ... };  // 仅 UI 投影
}
```

`ToolEntry` 在 metadata 之外挂扩展钩子（均为可选，按需实现）：

- `resolveModelContract`——**每次模型调用时**动态投影 description/schema（Read 借此在宿主有 PDF 能力时切换 schema 与超时）
- `validateInput` / `resolveInput`——校验与归一化（在 hook 与权限之前）
- `resolvePermissionCapability`——运行时改写权限（Bash 借此把只读命令从 needsApproval 降级为放行）
- `prepareApproval`——审批 UI 的预览收窄
- `formatModelContent`——结构化结果块（Read 的 text/image block）

### 2.2 参数 schema：zod 单一来源

- 每个工具在 `contracts/src/tools/<name>.ts` 用 zod 定义输入，`contracts/src/tools/json-schema.ts:23` 的 `toToolJsonSchema()`（`zodToJsonSchema`，`$refStrategy:"none"`，目标 jsonSchema7）生成 provider 中立 JSON Schema。
- 手写校验器 `core/src/tool/json-schema.ts:28 validateJsonSchemaValue()` 在执行入口复检模型给的参数。
- MCP 工具不走 zod，直接透传 server 声明的 JSON Schema（缺失时兜底 `{type:"object", additionalProperties:true}`）。

### 2.3 注册机制：集中注册 + 端口门控

- 唯一内置清单：`core/src/tool/handlers/index.ts:72-133` 的 `builtInTools: ToolEntry[]` 数组。
- `registerBuiltInTools()`（:191-265）按 `RegisterBuiltInToolsOptions` 的功能门过滤：`includeSkill/includeAgent/includeAutomation/includeDynamicWorkflow/includeNodeRepl/includeSendMessage…`，门控条件是运行时端口（`skillPort`、`automationPort`、`subagentPort`）是否在场——**能力由宿主注入的端口决定，而不是硬编码**。
- 注册接线：`core/src/runtime/helpers/runtime-tools.ts:46`；registry 实现在 `core/src/tool/registry.ts`（name→entry Map + 别名 Map，`Task`→`Agent`、`ApplyPatch`→`Write/Edit` 的 hook 兼容映射）。
- handlers 组织：一工具一文件（`handlers/read.ts`、`bash.ts`…），复杂工具带同前缀卫星文件（bash-readonly-policy-\* 约 25 个）；统一 handler 签名 `(input, context) => Promise<TOutput>`，业务失败返回 `{result:false, errorCode, message}` 而非抛异常。

## 三、内置工具清单（模型可见全集）

主力工具 20 个 + workflow 族 10 个（灰度门控）+ 自动化/闲时 6 个。按注册顺序的完整清单（名称 → 关键元数据）：

| 分组 | 工具 | 关键点 |
| --- | --- | --- |
| 文件 | Read / Write / Edit | Read 注入 read-file-state（file_unchanged 去重、Edit 前置 freshness 校验）；Write/Edit needsApproval=true、riskLevel=medium |
| 执行/搜索 | Bash / Glob / Grep | Bash 是元数据最重的工具：sideEffectScope=system、riskLevel=high、超时可被调用覆盖、只读命令运行时降权；Glob/Grep 在 embedded-search 开启时隐藏 |
| Web | WebFetch / WebSearch | WebFetch 15 分钟缓存 + egress guard + 预批 URL；WebSearch 的 description 是 getter，每次读取重算"当前月份"防长驻进程过期，并可映射为 provider-native web_search |
| 任务管理 | TodoRead / TodoWrite / ExitPlanMode / AskUserQuestion | 后两者 `requiresUserInteraction:true`；AskUserQuestion 是 sideEffectScope=userInteraction 的唯一实例 |
| 后台任务 | TaskOutput / TaskStop | TaskOutput `timeout:{kind:"none"}`（阻塞等待）；带弃用别名（shell_id）兼容 |
| 子代理 | Agent（+ Task 隐藏别名）/ Skill / ReadSessionContext / SendMessage / RespondToCoordinator / submit_result / escalate | Agent 描述由 profile 清单动态烘焙；submit_result `stopTurnOnSuccess:true`；escalate 刻意非终态 |
| 定时/闲时 | CronCreate/List/Update/Delete、OffPeakCreate/List | 端口门控；CronCreate needsApproval=true |
| 动态工作流 | CreateWorkflow、AmendWorkflow、SaveWorkflow、EvalWorkflowSnippet、ListWorkflowRuns、GetWorkflowRun、ResumeWorkflowRun、ResolveWorkflowQuestion、ListSavedWorkflows、ListModels | CreateWorkflow/AmendWorkflow `alwaysAsk:true`——任何权限模式（含 yolo）都必须确认 |

描述文本的四种生成模式值得注意（对 E4 的 schema 设计有参考价值）：

1. 纯静态常量（Write/Edit/Glob/Grep…）
2. 注册期工厂烘焙（Bash 注入实际超时数值、Agent 注入 profile 清单）
3. 每次读取动态求值（WebSearch 的 getter）
4. 每次模型调用投影（Read 的 PDF 能力切换，`resolveModelContract`）

## 四、端到端调用链路

一次工具调用的完整数据流（关键文件:行号均相对 `apps/zcode-cli/packages/`）：

### 4.1 请求组装（tools 如何发给模型）

1. `core/src/tool/registry.ts:104 toContracts()` 把 ToolEntry 投影为 `ModelToolContract[]`（过滤 `providerVisible:false`，`toolDescriptionForProvider` 把 modelInstructions 拼成 "Usage:" 块）。
2. `core/src/runtime/methods/config.ts:136 getTools()`：embedded-search 过滤 + `provider-visible-order.ts:34` 固定工具顺序 + `model-contract.ts:20` 按当前模型投影 schema/description，结果缓存。
3. `adapters/src/model/tool-transform.ts:20 toAiSdkTools()` 转成 AI SDK `ToolSet`；Anthropic 首方走 strict schema；MCP 本地 `$ref` 由 `hoistLocalJsonSchemaRefsToDefs()` 搬进 `$defs`。
4. provider wire format 三类适配（`model-execution.ts:260`）：OpenAI responses / Anthropic / openai-compatible，统一经 Vercel AI SDK。

### 4.2 流式 tool_call 解析（增量累积 + 容错）

- `adapters/src/model/runner-normalization.ts:62` 把 AI SDK 的 `tool-input-start/delta/end`、`tool-call` chunk 归一为内部事件；**执行参数只取 final tool-call 的 input，不从 delta 猜**。
- `streaming-tool-call-assembler.ts` 按 toolCallId 维护流式累积，final 到达时去重合并。
- **参数 JSON 容错**（`tool-input-normalization.ts:9`）：字符串 input 去 BOM 后 `JSON.parse`，解析失败或 null **不抛错，降级为 `{}`**，交给 schema 校验产生业务错误——参数错误不会升级为整个模型请求失败。
- 流中提前执行：`streaming-tool-coordinator.ts:54` 对 readOnly + concurrentSafe + sideEffect=none 的工具在流未结束时即执行（降低首工具延迟）。

### 4.3 调度与执行

- **Scheduler（纯计划，不执行）** `core/src/tool/scheduler.ts:50`：并行判定 `canRunInParallel()`（destructive→串行；concurrentSafe 显式优先；readOnly 或 sideEffect=none→可并行）→ 拓扑排序（dependsOn 依赖 + 环检测）→ `groupByParallel()` 按依赖层级分组，组内超 `maxConcurrency`（默认 10）再切片。
- **Executor（执行）** `core/src/tool/executor/batch-runner.ts:56`：**组间串行、组内 `Promise.all` 并行**；某结果带 `stopTurnAfterResult` 时后续组合成 `ToolCancelled`。
- **单次执行管线** `executor/call-runner.ts:79`：registry 查找（miss→ToolNotFound 事件）→ zod 校验 → JSON Schema 复检 → `entry.validateInput` → `entry.resolveInput` 归一化 → PreToolUse hook → 权限裁决 → 超时包裹下执行 handler。
- **取消传播**：turn 的 abortSignal 经 `linkAbortSignal()` 链到每次调用；abort 被 catch 收口为 `ToolCancelled` result，**保证每个 tool_call 必有配对结果**（消息历史完整性约束）。

### 4.4 权限裁决

`core/src/permission/service.ts:93 checkPermission()` 判定顺序（先命中先出）：

1. plan-mode 交接策略
2. `requiresUserInteraction` → ask
3. `alwaysAsk`（硬 deny 优先，其次会话级 allow）
4. yolo 模式放行
5. disallowedTools deny
6. 项目规则 deny → ask → allow（subject 支持 bash command/url/file_path 通配符）
7. WebFetch 预批 URL、workflow 草稿免确认
8. build/edit 模式默认（readOnly 放行，critical/high risk → ask）

ask 的收口：`executor/approval-gate.ts` → `permission/broker.ts:34 ManualPermissionBroker.requestPermission()` 挂起 Promise 等宿主审批回调（hook 链与 broker 竞速，防"确认窗先渲染、deferred 未注册导致点击被丢"）。结果 allow/deny/escalate/**modify**（modify 重校验输入后可能重新裁决）。

### 4.5 结果格式化、预算与回传

- `executor/result-serialization.ts:31 serializeOutput()`：`formatModelContent`（结构化块）或 JSON.stringify → 默认预算 100KB（maxInlineBytes/maxModelBytes）。
- 超预算两种策略：`truncate` 截断并附 `[Tool output truncated by resultBudget: ...]` 后缀；`artifact` 全文落盘 artifactStore，模型收到的替换为 `<persisted-output>` 信封（`result-persistence-format.ts`，含 2000 字符预览 + "Output too large (N KB). Full output saved to: <path>"）。**工具结果本体和给模型看的投影是分离的**。
- 回传：`agent/message-history.ts:379` 以 role:"tool" + toolCallId 记入历史 → 下一轮 `provider-request-messages.ts:48` 投影 → `toAiSdkMessages()` 转成 AI SDK `tool-result` part。

### 4.6 持久化（SQLite，非 jsonl）

- 工具 part 三态落盘：pending（`state.raw = JSON.stringify({tool, input})`）→ running → completed/error（`state.output` = 序列化文本，metadata 含 serialization 摘要 + read-file-state 快照供 resume）。
- tool_call ↔ tool_result 通过 part 的 callID + assistant messageId 关联；`StreamingToolLedgerUpdated` 事件记录流式工具账目与恢复锚点（崩溃后配对恢复）。
- 底层：`adapters/src/storage/session-store/sqlite-session-store.ts:622 savePart()`。

## 五、参数与结果的真实样例（本机数据）

### 5.1 模型侧（Anthropic 消息格式，`~/.zcode/agent/sessions/*.jsonl`）

tool_call（模型发起）：

```json
{"type": "tool_use", "id": "call_b9c7e0f76f634b3891ce07e1", "name": "Glob",
 "input": {"pattern": "**/*superpowers*"}}
```

配对 tool_result（回给模型）：

```json
{"type": "tool_result", "tool_use_id": "call_b9c7e0f76f634b3891ce07e1",
 "content": "C:\\Projects\\Game\\Docs\\superpowers\n...", "is_error": false}
```

### 5.2 发给模型的工具声明（`~/.zcode/cli/rollout/model-io-*.jsonl`，含 MCP 工具）

```json
{"name": "mcp__renderdoc__open_capture",
 "description": "Open a RenderDoc capture (.rdc) file for analysis...",
 "input_schema": {"type":"object","properties":{"filepath":{"type":"string"}},
   "required":["filepath"]}}
```

### 5.3 两级截断标记（真实输出）

- resultBudget 层：`[tool_result truncated for model context: 9766 chars omitted from Glob output...]`
- 工具自截断层：`... (253 more lines not shown; output was truncated. Call Grep again with a narrower pattern/path/glob...)`——**工具自身提示模型如何缩小查询，而不只是默默砍掉**。

### 5.4 调度事件（`~/.zcode/cli/agents/**/transcript.jsonl`）

```json
{"type":"tool_call_scheduled",
 "payload":{"toolCallId":"call_9b86...","toolName":"Bash",
   "input":{"command":"ls \"C:\\Projects\\Game\\Alco\"","description":"..."},
   "parallelGroupIndex":0,"canRunParallel":false,
   "schedule":{"parallelGroups":[["call_9b86...","call_5f34...","call_5b2f...","call_eb45..."]],
               "executionOrder":[...]}}}
```

## 六、MCP 工具接入

### 6.1 接入路径

- 三种传输（`contracts/src/interfaces/mcp.port.ts`）：stdio（自定义 `ProcessTreeStdioClientTransport`，支持进程树回收）/ http（StreamableHTTP，可 OAuth）/ sse（legacy）。
- 发现：连接后 `client.listTools()` → 每个 tool 生成 descriptor → `core/src/mcp/name.ts` 命名为 `mcp__<sanitized(server)>__<sanitized(tool)>`（非 `[a-zA-Z0-9_-]` 替换为 `_`）。插件 MCP server 名带命名空间 `plugin:<plugin>:<server>`。
- `core/src/mcp/index.ts:62 createMcpToolEntry()` 把 descriptor 包装成**与内置工具同构的 ToolEntry**：riskLevel 从 annotations 推导（readOnlyHint→low、destructiveHint→high、默认 medium）；`formatModelContent` 把 content blocks（text/image/audio/resource）投影为模型可见内容，`isError` 时加 `MCP tool returned an error:` 前缀。
- 与内置工具的差异仅在 ToolEntry 字段取值：permission 统一 `"mcp"`、needsApproval 恒 true、`cancellation: bestEffort`、`trace: required + propagateToAdapters`。

### 6.2 超时与故障处理

- 超时层级：server 配置 `timeoutMs`（默认 30s，本机 renderdoc 配 600s）> per-tool descriptor.timeoutMs；`allowCallOverride:false`。
- `callTool` 故障链：connecting 中→限时不无限等；已断连→调用前自动重连一次（stdio 子进程死亡自愈）；OAuth 过期→交互式恢复后重试一次；in-band 错误（HTTP 200 + isError）打 warn 并附 `x-request-id`。
- 连接失败分类枚举（process_start_failed / network_unreachable / tool_list_failed / protocol_negotiation_failed / oauth_authorization_failed…）供上层决策。

## 七、插件机制（与工具系统的关系）

- manifest：`.zcode-plugin/plugin.json`，五类组件 `agent | command | skill | hook | mcp`；MCP 是插件贡献**可调用工具的唯一途径**，skill/command/hook 都不新增 tool（skill 是 SKILL.md 内容注入，hook 挂 PreToolUse 等事件在统一 executor 的 hook 阶段跑）。
- 插件 MCP 的 server 名命名空间化（`plugin:browser-use:default`），支持 `${ZCODE_PLUGIN_ROOT}` 等模板变量，`ZCODE_PLUGIN_ID` 由宿主权威覆写防伪造。
- 实证：browser-use 插件只贡献 2 个 skill；`mcp__node_repl__js` 工具来自宿主在 browser-use 启用时动态注入的内置 stdio MCP server（node-repl-host）。

## 八、对内核选型实验（E1/E4）的启示

这份分析中可直接被实验设计吸收的机制清单：

1. **E1 功能清单 2（工具调度）的参照实现**：并行判定不靠调用点特判，而是工具自声明（concurrentSafe/destructive/sideEffectScope）+ scheduler 拓扑分组 + executor 组间串行组内并行。四个语言 spike 可直接复用这套元数据模型（附录 A 的 tool_call 事件已有 parallelGroups 字段可对照）。
2. **E1 功能清单 3（工具元数据与权限桩）的参照实现**：裁决顺序（deny→ask→allow，alwaysAsk 硬门）+ 审批回调挂起 Promise + allow/deny/escalate/modify 四态结果，与 D5（审批超时默认拒绝）的任务书直接对应。
3. **E1 功能清单 4（上下文截断）的参照实现**：resultBudget 两级策略（truncate 截断 vs artifact 落盘 + `<persisted-output>` 预览信封）+ "工具自截断并提示模型缩小查询"——比单纯砍字节更保语义。
4. **E1 功能清单 5（SQLite 持久化）的参照实现**：工具 part 三态（pending/running/completed）+ callID 配对 + 流式账本恢复锚点，与 D3（kill -9 恢复、配对完整）直接对应。
5. **参数容错原则**：模型输出的参数 JSON 解析失败降级 `{}` 交 schema 校验报业务错误，绝不升级为请求失败——四个 spike 应继承此行为（D4 的"流式结束语义不丢"同源）。
6. **取消语义**：abort 收口为 `ToolCancelled` 配对结果，保证消息历史 tool_call/tool_result 永远配对——D1 的验收用例应包含这一点。
7. **外部工具同构包装**：MCP 工具被包装成与内置工具相同的接口走同一执行管线，无独立 executor——若新内核采用此设计，插件/外部工具就是注册问题而非架构问题。

## 九、佐证文件索引

| 主题 | 路径 |
| --- | --- |
| 工具类型/元数据 | `apps/zcode-cli/packages/core/src/tool/types.ts` |
| 契约声明（权限/预算/超时） | `apps/zcode-cli/packages/contracts/src/tools/contract.ts` |
| 注册表与投影 | `core/src/tool/registry.ts`、`handlers/index.ts` |
| 调度器 | `core/src/tool/scheduler.ts` |
| 执行管线 | `core/src/tool/executor/{impl,batch-runner,call-runner,result-serialization,permission-flow,approval-gate,timeout}.ts` |
| 权限服务 | `core/src/permission/{service,broker,rule-matching}.ts` |
| AI SDK 桥接与流式解析 | `adapters/src/model/{tool-transform,tool-input-normalization,streaming-tool-call-assembler,runner-normalization}.ts` |
| MCP | `core/src/mcp/{index,name}.ts`、`adapters/src/mcp/index.ts`、`contracts/src/interfaces/mcp.port.ts` |
| 真实数据 | `~/.zcode/agent/sessions/*.jsonl`（消息转录）、`~/.zcode/cli/rollout/model-io-*.jsonl`（模型 IO 原文）、`~/.zcode/cli/log/zcode-*.jsonl`（结构化事件） |

---

**结语：** 工具调用子系统是 ZCode 中分层最规整的部分——contracts 声明能力、core 调度执行、adapters 适配协议、runtime 管生命周期，MCP/插件都收敛到同一个 ToolEntry 接口。E1 的四个语言 spike 若继承这套接口形态（尤其元数据驱动的调度与权限、结果预算两策略、配对完整性约束），可直接复用本报告的验收语义。
