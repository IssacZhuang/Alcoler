//! Kernel loop integration tests over fake ports. Timers run on tokio's
//! paused clock (`start_paused`), so timeout/backoff behavior is
//! deterministic and instant.

use std::collections::VecDeque;
use std::pin::Pin;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::task::{Context, Poll};

use alcoler_contracts::context::TokenCounter;
use alcoler_contracts::ids::{CommandId, ProviderId, SessionId, ToolCallId, TurnId};
use alcoler_contracts::message::{ContentBlock, Message, OutputBlock, Role};
use alcoler_contracts::model::{
    CompletionRequest, ModelProvider, ModelStreamEvent, ProviderError, ProviderErrorKind,
    StopReason, Usage,
};
use alcoler_contracts::permission::{PermissionDecision, PermissionGate, PermissionRequest};
use alcoler_contracts::storage::{
    BlobRef, BlobStore, RetainReport, RetentionPolicy, SessionMeta, SessionRecord, SessionSnapshot,
    SessionStore, SessionSummary, StoreError, StoreErrorKind, TurnProgress, TurnRecord,
};
use alcoler_contracts::time::{Clock, IdGenerator};
use alcoler_contracts::tool::{
    Tool, ToolExecContext, ToolOutput, ToolPolicy, ToolSpec, ToolStatus,
};
use alcoler_contracts::wire::{
    CancelScope, Command, CommandSource, EventSink, KernelEvent, UserInput, WireError,
};
use alcoler_contracts::{BoxFuture, BoxStream};
use alcoler_kernel::{Kernel, KernelConfig, KernelDeps};
use futures_util::Stream;
use serde_json::{Value, json};

// ---------- fakes ----------

struct TestClock;

impl Clock for TestClock {
    fn unix_ms(&self) -> i64 {
        1_700_000_000_000
    }

    fn monotonic_ms(&self) -> u64 {
        static ANCHOR: std::sync::OnceLock<tokio::time::Instant> = std::sync::OnceLock::new();
        ANCHOR
            .get_or_init(tokio::time::Instant::now)
            .elapsed()
            .as_millis() as u64
    }

    fn delay_ms(&self, ms: u64) -> BoxFuture<'static, ()> {
        Box::pin(tokio::time::sleep(std::time::Duration::from_millis(ms)))
    }
}

struct SeqIds(AtomicU64);

impl IdGenerator for SeqIds {
    fn next_id(&self) -> String {
        format!("id-{}", self.0.fetch_add(1, Ordering::SeqCst))
    }
}

enum Step {
    Event(ModelStreamEvent),
    Error(ProviderError),
    /// Never resolves: the stream stalls (network cut / endless stream).
    Hang,
}

type Script = Vec<Step>;

struct ScriptStream {
    queue: VecDeque<Result<ModelStreamEvent, ProviderError>>,
    hang: bool,
    drops: Arc<AtomicU64>,
}

impl Stream for ScriptStream {
    type Item = Result<ModelStreamEvent, ProviderError>;

    fn poll_next(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        let this = self.get_mut();
        if let Some(item) = this.queue.pop_front() {
            return Poll::Ready(Some(item));
        }
        if this.hang {
            Poll::Pending
        } else {
            Poll::Ready(None)
        }
    }
}

impl Drop for ScriptStream {
    fn drop(&mut self) {
        self.drops.fetch_add(1, Ordering::SeqCst);
    }
}

struct FakeProvider {
    id: ProviderId,
    requests: Arc<Mutex<Vec<CompletionRequest>>>,
    scripts: Mutex<VecDeque<Script>>,
    drops: Arc<AtomicU64>,
}

impl FakeProvider {
    fn new(scripts: Vec<Script>) -> (Self, Arc<Mutex<Vec<CompletionRequest>>>, Arc<AtomicU64>) {
        let requests = Arc::new(Mutex::new(Vec::new()));
        let drops = Arc::new(AtomicU64::new(0));
        (
            Self {
                id: ProviderId("fake".into()),
                requests: requests.clone(),
                scripts: Mutex::new(scripts.into()),
                drops: drops.clone(),
            },
            requests,
            drops,
        )
    }
}

impl ModelProvider for FakeProvider {
    fn provider_id(&self) -> &ProviderId {
        &self.id
    }

    fn stream(
        &self,
        req: CompletionRequest,
    ) -> BoxFuture<
        '_,
        Result<BoxStream<'static, Result<ModelStreamEvent, ProviderError>>, ProviderError>,
    > {
        Box::pin(async move {
            self.requests.lock().unwrap().push(req);
            let mut script = self.scripts.lock().unwrap().pop_front().unwrap_or_default();
            if script.is_empty() {
                script = vec![
                    Step::Event(ModelStreamEvent::Opened {
                        model: "fake".into(),
                    }),
                    Step::Event(ModelStreamEvent::Completed {
                        stop_reason: StopReason::EndTurn,
                        usage: Usage::default(),
                        reasoning_signature: None,
                    }),
                ];
            }
            let mut queue = VecDeque::new();
            let mut hang = false;
            for step in script {
                match step {
                    Step::Event(event) => queue.push_back(Ok(event)),
                    Step::Error(error) => queue.push_back(Err(error)),
                    Step::Hang => hang = true,
                }
            }
            Ok(Box::pin(ScriptStream {
                queue,
                hang,
                drops: self.drops.clone(),
            }) as BoxStream<'static, _>)
        })
    }
}

struct FakeGate {
    decisions: Mutex<VecDeque<PermissionDecision>>,
    asked: Arc<AtomicU64>,
}

impl FakeGate {
    fn allow() -> Self {
        Self {
            decisions: Mutex::new(VecDeque::new()),
            asked: Arc::new(AtomicU64::new(0)),
        }
    }
}

impl PermissionGate for FakeGate {
    fn decide<'a>(&'a self, _request: &'a PermissionRequest) -> BoxFuture<'a, PermissionDecision> {
        Box::pin(async move {
            self.asked.fetch_add(1, Ordering::SeqCst);
            self.decisions
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or(PermissionDecision::Allow)
        })
    }
}

enum Behavior {
    Text(&'static str),
    Big(usize),
    Hang,
}

struct FakeTool {
    spec: ToolSpec,
    behavior: Behavior,
    runs: Arc<AtomicU64>,
}

impl Tool for FakeTool {
    fn spec(&self) -> &ToolSpec {
        &self.spec
    }

    fn execute<'a>(&'a self, _input: Value, ctx: &'a ToolExecContext) -> BoxFuture<'a, ToolOutput> {
        Box::pin(async move {
            self.runs.fetch_add(1, Ordering::SeqCst);
            match &self.behavior {
                Behavior::Text(text) => {
                    let mut out = ToolOutput::ok(vec![OutputBlock::Text {
                        text: (*text).into(),
                    }]);
                    out.bytes_emitted = text.len() as u64;
                    out
                }
                Behavior::Big(size) => {
                    let text = "x".repeat(*size);
                    let mut out = ToolOutput::ok(vec![OutputBlock::Text { text }]);
                    out.bytes_emitted = *size as u64;
                    out
                }
                Behavior::Hang => {
                    ctx.cancel.cancelled().await;
                    ToolOutput {
                        status: ToolStatus::Cancelled,
                        content: Vec::new(),
                        bytes_emitted: 0,
                        truncated: false,
                    }
                }
            }
        })
    }
}

#[derive(Default)]
struct StoreState {
    sessions: Vec<SessionMeta>,
    turns: Vec<TurnRecord>,
    progress: Vec<TurnProgress>,
}

type BlobMap = std::collections::HashMap<(String, String), Vec<u8>>;

struct FakeStore(Arc<Mutex<StoreState>>);

struct FakeBlobs(Arc<Mutex<BlobMap>>);

struct FakeCounter;

impl TokenCounter for FakeCounter {
    fn count_text(&self, _model: &alcoler_contracts::model::ModelRef, text: &str) -> u64 {
        text.len() as u64 / 4
    }

    fn count_message(&self, model: &alcoler_contracts::model::ModelRef, message: &Message) -> u64 {
        let text: String = message
            .blocks
            .iter()
            .map(|block| match block {
                ContentBlock::Text { text } => text.clone(),
                ContentBlock::Reasoning { text, .. } => text.clone(),
                ContentBlock::ToolUse {
                    name, arguments, ..
                } => {
                    format!("{name}{arguments}")
                }
                ContentBlock::ToolResult { content, .. } => content
                    .iter()
                    .map(|block| match block {
                        OutputBlock::Text { text } => text.clone(),
                        OutputBlock::Json { value } => value.to_string(),
                    })
                    .collect(),
            })
            .collect();
        self.count_text(model, &text)
    }
}

struct FakeSink(Arc<Mutex<Vec<KernelEvent>>>);

impl EventSink for FakeSink {
    fn emit(&self, event: KernelEvent) -> BoxFuture<'_, Result<(), WireError>> {
        Box::pin(async move {
            self.0.lock().unwrap().push(event);
            Ok(())
        })
    }
}

struct ChannelSource {
    rx: tokio::sync::mpsc::Receiver<Command>,
}

impl CommandSource for ChannelSource {
    fn next_command(&mut self) -> BoxFuture<'_, Option<Command>> {
        Box::pin(self.rx.recv())
    }
}

impl SessionStore for FakeStore {
    fn open(&self) -> BoxFuture<'_, Result<(), StoreError>> {
        Box::pin(async { Ok(()) })
    }

    fn create_session<'a>(
        &'a self,
        meta: &'a SessionMeta,
    ) -> BoxFuture<'a, Result<(), StoreError>> {
        Box::pin(async move {
            let mut state = self.0.lock().unwrap();
            if state.sessions.iter().any(|m| m.id == meta.id) {
                return Err(StoreError {
                    kind: StoreErrorKind::Conflict,
                    message: "session exists".into(),
                });
            }
            state.sessions.push(meta.clone());
            Ok(())
        })
    }

    fn load_session<'a>(
        &'a self,
        id: &'a SessionId,
    ) -> BoxFuture<'a, Result<Option<SessionRecord>, StoreError>> {
        Box::pin(async move {
            let state = self.0.lock().unwrap();
            let Some(meta) = state.sessions.iter().find(|m| m.id == *id) else {
                return Ok(None);
            };
            let mut messages = Vec::new();
            let mut turns = 0;
            for record in state.turns.iter().filter(|r| r.session == *id) {
                turns = turns.max(record.turn.0);
                messages.extend(record.new_messages.clone());
            }
            Ok(Some(SessionRecord {
                meta: meta.clone(),
                snapshot: Some(SessionSnapshot {
                    messages,
                    turns_completed: turns,
                }),
            }))
        })
    }

    fn list_sessions(&self) -> BoxFuture<'_, Result<Vec<SessionSummary>, StoreError>> {
        Box::pin(async {
            let state = self.0.lock().unwrap();
            Ok(state
                .sessions
                .iter()
                .map(|meta| SessionSummary {
                    id: meta.id.clone(),
                    turns_completed: state.turns.iter().filter(|r| r.session == meta.id).count()
                        as u64,
                    updated_at_unix_ms: 0,
                })
                .collect())
        })
    }

    fn save_turn_progress<'a>(
        &'a self,
        progress: &'a TurnProgress,
    ) -> BoxFuture<'a, Result<(), StoreError>> {
        Box::pin(async move {
            self.0.lock().unwrap().progress.push(progress.clone());
            Ok(())
        })
    }

    fn append_turn(&self, record: TurnRecord) -> BoxFuture<'_, Result<(), StoreError>> {
        Box::pin(async move {
            let mut state = self.0.lock().unwrap();
            if state
                .turns
                .iter()
                .any(|r| r.session == record.session && r.turn == record.turn)
            {
                return Err(StoreError {
                    kind: StoreErrorKind::Conflict,
                    message: "turn exists".into(),
                });
            }
            state.turns.push(record);
            Ok(())
        })
    }

    fn replay_turns<'a>(
        &'a self,
        id: &'a SessionId,
        after: TurnId,
    ) -> BoxFuture<'a, Result<BoxStream<'static, Result<TurnRecord, StoreError>>, StoreError>> {
        Box::pin(async move {
            let state = self.0.lock().unwrap();
            let records: Vec<Result<TurnRecord, StoreError>> = state
                .turns
                .iter()
                .filter(|r| r.session == *id && r.turn > after)
                .cloned()
                .map(Ok)
                .collect();
            Ok(Box::pin(futures_util::stream::iter(records)) as BoxStream<'static, _>)
        })
    }

    fn apply_retention<'a>(
        &'a self,
        _policy: &'a RetentionPolicy,
    ) -> BoxFuture<'a, Result<RetainReport, StoreError>> {
        Box::pin(async { Ok(RetainReport::default()) })
    }

    fn close(&self) -> BoxFuture<'_, Result<(), StoreError>> {
        Box::pin(async { Ok(()) })
    }
}

impl BlobStore for FakeBlobs {
    fn put<'a>(
        &'a self,
        session: &'a SessionId,
        key: &'a str,
        bytes: Vec<u8>,
    ) -> BoxFuture<'a, Result<BlobRef, StoreError>> {
        Box::pin(async move {
            self.0
                .lock()
                .unwrap()
                .insert((session.0.clone(), key.to_string()), bytes);
            Ok(BlobRef {
                session: session.clone(),
                key: key.to_string(),
            })
        })
    }

    fn get<'a>(
        &'a self,
        session: &'a SessionId,
        key: &'a str,
    ) -> BoxFuture<'a, Result<Option<Vec<u8>>, StoreError>> {
        Box::pin(async move {
            Ok(self
                .0
                .lock()
                .unwrap()
                .get(&(session.0.clone(), key.to_string()))
                .cloned())
        })
    }
}

// ---------- harness ----------

struct Harness {
    cmd_tx: tokio::sync::mpsc::Sender<Command>,
    run: tokio::task::JoinHandle<()>,
    events: Arc<Mutex<Vec<KernelEvent>>>,
    store: Arc<Mutex<StoreState>>,
    requests: Arc<Mutex<Vec<CompletionRequest>>>,
    drops: Arc<AtomicU64>,
}

fn tool_spec(name: &str, policy: ToolPolicy) -> ToolSpec {
    ToolSpec {
        name: name.into(),
        description: format!("fake tool {name}"),
        input_schema: json!({
            "type": "object",
            "properties": { "x": { "type": "integer" } },
            "required": ["x"]
        }),
        policy,
    }
}

fn start_session() -> Command {
    start_session_with_budget(1_000_000)
}

fn start_session_with_budget(context_budget_tokens: u64) -> Command {
    Command::StartSession {
        session: SessionId("s1".into()),
        profile: alcoler_contracts::storage::SessionProfile {
            name: "standard".into(),
            model: alcoler_contracts::model::ModelRef {
                provider: "fake".into(),
                model: "m1".into(),
            },
            context_budget_tokens,
            max_concurrent_tools: 4,
            event_buffer_bound: 1024,
            permission_timeout_ms: 60_000,
        },
    }
}

fn submit(command: &str, text: &str) -> Command {
    Command::Submit {
        session: SessionId("s1".into()),
        command: CommandId(command.into()),
        input: UserInput { text: text.into() },
    }
}

fn cancel_session() -> Command {
    Command::Cancel {
        session: SessionId("s1".into()),
        scope: CancelScope::Session,
    }
}

fn harness(
    scripts: Vec<Script>,
    tools: Vec<Arc<FakeTool>>,
    gate: FakeGate,
    state: Arc<Mutex<StoreState>>,
    blobs: Arc<Mutex<BlobMap>>,
    config: KernelConfig,
) -> Harness {
    let (provider, requests, drops) = FakeProvider::new(scripts);
    let tool_handles: Vec<Arc<dyn Tool>> =
        tools.iter().map(|t| t.clone() as Arc<dyn Tool>).collect();
    let sink_state = Arc::new(Mutex::new(Vec::new()));
    let sink = Arc::new(FakeSink(sink_state.clone()));
    let kernel = Kernel::new(
        KernelDeps {
            store: Arc::new(FakeStore(state.clone())),
            blobs: Arc::new(FakeBlobs(blobs.clone())),
            provider: Arc::new(provider),
            tools: tool_handles,
            gate: Arc::new(gate),
            counter: Arc::new(FakeCounter),
            clock: Arc::new(TestClock),
            ids: Arc::new(SeqIds(AtomicU64::new(0))),
        },
        config,
    );
    let (cmd_tx, cmd_rx) = tokio::sync::mpsc::channel(64);
    let run = tokio::spawn(async move {
        kernel
            .run(Box::new(ChannelSource { rx: cmd_rx }), sink)
            .await;
    });
    Harness {
        cmd_tx,
        run,
        events: sink_state,
        store: state,
        requests,
        drops,
    }
}

async fn drive(h: &Harness, commands: Vec<Command>) {
    for command in commands {
        h.cmd_tx.send(command).await.unwrap();
    }
}

async fn wait_for<F: Fn(&[KernelEvent]) -> bool>(h: &Harness, predicate: F) {
    for _ in 0..20_000 {
        if predicate(&h.events.lock().unwrap()) {
            return;
        }
        tokio::time::sleep(std::time::Duration::from_millis(1)).await;
    }
    panic!(
        "condition not reached; events: {:#?}",
        h.events.lock().unwrap()
    );
}

async fn wait_turns(h: &Harness, count: usize) {
    wait_for(h, |events| {
        events
            .iter()
            .filter(|e| matches!(e, KernelEvent::TurnDone { .. }))
            .count()
            >= count
    })
    .await;
}

fn session_turns(h: &Harness) -> Vec<TurnRecord> {
    h.store
        .lock()
        .unwrap()
        .turns
        .iter()
        .filter(|r| r.session.0 == "s1")
        .cloned()
        .collect()
}

fn tool_uses(record: &TurnRecord) -> Vec<&ContentBlock> {
    record
        .new_messages
        .iter()
        .flat_map(|m| m.blocks.iter())
        .filter(|b| matches!(b, ContentBlock::ToolUse { .. }))
        .collect()
}

fn tool_results(record: &TurnRecord) -> Vec<&ContentBlock> {
    record
        .new_messages
        .iter()
        .flat_map(|m| m.blocks.iter())
        .filter(|b| matches!(b, ContentBlock::ToolResult { .. }))
        .collect()
}

fn text_script(text: &str) -> Script {
    vec![
        Step::Event(ModelStreamEvent::Opened {
            model: "fake".into(),
        }),
        Step::Event(ModelStreamEvent::TextDelta { delta: text.into() }),
        Step::Event(ModelStreamEvent::Completed {
            stop_reason: StopReason::EndTurn,
            usage: Usage::default(),
            reasoning_signature: None,
        }),
    ]
}

fn call_script(call_id: &str, tool: &str, args: &str) -> Script {
    vec![
        Step::Event(ModelStreamEvent::Opened {
            model: "fake".into(),
        }),
        Step::Event(ModelStreamEvent::ToolCallDelta {
            id: ToolCallId(call_id.into()),
            name: Some(tool.into()),
            arguments_delta: String::new(),
        }),
        Step::Event(ModelStreamEvent::ToolCallDelta {
            id: ToolCallId(call_id.into()),
            name: None,
            arguments_delta: args.into(),
        }),
        Step::Event(ModelStreamEvent::Completed {
            stop_reason: StopReason::ToolUse,
            usage: Usage::default(),
            reasoning_signature: None,
        }),
    ]
}

fn echo_tool(name: &str) -> Arc<FakeTool> {
    Arc::new(FakeTool {
        spec: tool_spec(name, ToolPolicy::default()),
        behavior: Behavior::Text("echo-output"),
        runs: Arc::new(AtomicU64::new(0)),
    })
}

// ---------- tests ----------

#[tokio::test(start_paused = true)]
async fn text_turn_happy_path() {
    let h = harness(
        vec![text_script("hello world")],
        vec![],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "hi")]).await;
    wait_turns(&h, 1).await;

    let turns = session_turns(&h);
    assert_eq!(turns.len(), 1);
    assert_eq!(turns[0].turn, TurnId(1));
    assert_eq!(turns[0].new_messages.len(), 2);
    assert_eq!(turns[0].new_messages[0].role, Role::User);
    assert_eq!(
        turns[0].new_messages[1],
        Message::assistant_text("hello world")
    );
    assert_eq!(turns[0].snapshot.turns_completed, 1);

    let events = h.events.lock().unwrap().clone();
    let types: Vec<&str> = events
        .iter()
        .map(|e| match e {
            KernelEvent::SessionStarted { .. } => "session_started",
            KernelEvent::TurnStarted { .. } => "turn_started",
            KernelEvent::ModelDelta { .. } => "model_delta",
            KernelEvent::TurnDone { .. } => "turn_done",
            _ => "other",
        })
        .collect();
    assert_eq!(
        types,
        vec![
            "session_started",
            "turn_started",
            "model_delta",
            "turn_done"
        ]
    );
    drop(events);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn tool_call_round_trip_pairs_result() {
    let h = harness(
        vec![
            call_script("call-1", "echo", "{\"x\":1}"),
            text_script("done after tool"),
        ],
        vec![echo_tool("echo")],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "run the tool")]).await;
    wait_turns(&h, 1).await;

    let turns = session_turns(&h);
    assert_eq!(turns.len(), 1);
    let uses = tool_uses(&turns[0]);
    let results = tool_results(&turns[0]);
    assert_eq!(uses.len(), 1);
    assert_eq!(results.len(), 1);
    match (&uses[0], &results[0]) {
        (
            ContentBlock::ToolUse { id, .. },
            ContentBlock::ToolResult {
                tool_use_id,
                is_error,
                content,
            },
        ) => {
            assert_eq!(id, tool_use_id);
            assert!(!is_error);
            assert_eq!(content.len(), 1);
        }
        other => panic!("unexpected blocks: {other:?}"),
    }

    // The second provider request carries the paired result back.
    let requests = h.requests.lock().unwrap().clone();
    assert_eq!(requests.len(), 2);
    assert!(
        requests[1]
            .messages
            .iter()
            .any(|m| m.blocks.iter().any(|b| matches!(
                b,
                ContentBlock::ToolResult { tool_use_id, .. } if tool_use_id.0 == "call-1"
            )))
    );
    drop(requests);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn cancel_during_stream_persists_partial_text() {
    let script: Script = vec![
        Step::Event(ModelStreamEvent::Opened {
            model: "fake".into(),
        }),
        Step::Event(ModelStreamEvent::TextDelta {
            delta: "partial answer".into(),
        }),
        Step::Hang,
    ];
    let h = harness(
        vec![script],
        vec![],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "hi")]).await;
    wait_for(&h, |events| {
        events
            .iter()
            .any(|e| matches!(e, KernelEvent::TurnStarted { .. }))
    })
    .await;
    tokio::time::sleep(std::time::Duration::from_millis(5)).await;

    let drops_before = h.drops.load(Ordering::SeqCst);
    h.cmd_tx.send(cancel_session()).await.unwrap();
    wait_turns(&h, 1).await;

    let turns = session_turns(&h);
    assert_eq!(turns.len(), 1);
    assert!(
        turns[0]
            .new_messages
            .iter()
            .any(|m| m == &Message::assistant_text("partial answer"))
    );
    assert!(tool_uses(&turns[0]).is_empty());
    // Dropping the stalled stream cancels the upstream request.
    assert!(h.drops.load(Ordering::SeqCst) > drops_before);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn cancel_during_tool_yields_paired_cancelled_result() {
    let hanging = Arc::new(FakeTool {
        spec: tool_spec("hang", ToolPolicy::default()),
        behavior: Behavior::Hang,
        runs: Arc::new(AtomicU64::new(0)),
    });
    let h = harness(
        vec![
            call_script("call-1", "hang", "{\"x\":1}"),
            text_script("unreachable"),
        ],
        vec![hanging],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "go")]).await;
    wait_for(&h, |events| {
        events
            .iter()
            .any(|e| matches!(e, KernelEvent::ToolCall { .. }))
    })
    .await;

    h.cmd_tx.send(cancel_session()).await.unwrap();
    wait_turns(&h, 1).await;

    let turns = session_turns(&h);
    assert_eq!(turns.len(), 1);
    let uses = tool_uses(&turns[0]);
    let results = tool_results(&turns[0]);
    assert_eq!(uses.len(), 1);
    assert_eq!(results.len(), 1);
    match &results[0] {
        ContentBlock::ToolResult {
            tool_use_id,
            is_error,
            ..
        } => {
            assert_eq!(tool_use_id.0, "call-1");
            assert!(is_error);
        }
        other => panic!("unexpected block: {other:?}"),
    }
    {
        let events = h.events.lock().unwrap().clone();
        assert!(events.iter().any(|e| matches!(
            e,
            KernelEvent::ToolResult { status: ToolStatus::Cancelled, call, .. } if call.0 == "call-1"
        )));
    }
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn tool_timeout_yields_paired_timed_out_result() {
    let policy = ToolPolicy {
        timeout_ms: 500,
        ..ToolPolicy::default()
    };
    let hanging = Arc::new(FakeTool {
        spec: tool_spec("hang", policy),
        behavior: Behavior::Hang,
        runs: Arc::new(AtomicU64::new(0)),
    });
    let h = harness(
        vec![
            call_script("call-1", "hang", "{\"x\":1}"),
            text_script("after timeout"),
        ],
        vec![hanging],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "go")]).await;
    wait_turns(&h, 1).await;

    let events = h.events.lock().unwrap().clone();
    assert!(events.iter().any(|e| matches!(
        e,
        KernelEvent::ToolResult { status: ToolStatus::TimedOut, call, .. } if call.0 == "call-1"
    )));
    drop(events);
    let turns = session_turns(&h);
    assert_eq!(tool_results(&turns[0]).len(), 1);
    // The timeout result is fed back to the model on the next request.
    let requests = h.requests.lock().unwrap().clone();
    assert_eq!(requests.len(), 2);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn permission_deny_then_session_allow() {
    let policy = ToolPolicy {
        risk: alcoler_contracts::tool::RiskLevel::Medium,
        approval: alcoler_contracts::tool::ApprovalPolicy::ByRisk,
        ..ToolPolicy::default()
    };
    let echo = Arc::new(FakeTool {
        spec: tool_spec("guarded", policy),
        behavior: Behavior::Text("guarded-output"),
        runs: Arc::new(AtomicU64::new(0)),
    });
    let gate = FakeGate::allow();
    gate.decisions.lock().unwrap().extend([
        PermissionDecision::Deny {
            reason: Some("not today".into()),
        },
        PermissionDecision::AllowSession {
            tool: "guarded".into(),
        },
    ]);
    let asked = gate.asked.clone();
    let h = harness(
        vec![
            call_script("call-1", "guarded", "{\"x\":1}"),
            text_script("denied"),
            call_script("call-2", "guarded", "{\"x\":2}"),
            text_script("allowed"),
            call_script("call-3", "guarded", "{\"x\":3}"),
            text_script("allowlisted"),
        ],
        vec![echo.clone()],
        gate,
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(
        &h,
        vec![
            start_session(),
            submit("c1", "first"),
            submit("c2", "second"),
            submit("c3", "third"),
        ],
    )
    .await;
    wait_turns(&h, 3).await;

    let turns = session_turns(&h);
    assert_eq!(turns.len(), 3);
    match tool_results(&turns[0])[0] {
        ContentBlock::ToolResult {
            is_error, content, ..
        } => {
            assert!(is_error);
            let text = match &content[0] {
                OutputBlock::Text { text } => text.clone(),
                other => panic!("unexpected block: {other:?}"),
            };
            assert!(text.contains("permission_denied"));
        }
        other => panic!("unexpected block: {other:?}"),
    }
    match tool_results(&turns[1])[0] {
        ContentBlock::ToolResult { is_error, .. } => assert!(!is_error),
        other => panic!("unexpected block: {other:?}"),
    }
    // Turn 1 denied (one ask); turn 2 granted AllowSession (second ask);
    // turn 3 hits the session allowlist and never asks again.
    assert_eq!(asked.load(Ordering::SeqCst), 2);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn retryable_clean_error_retries_transparently() {
    let h = harness(
        vec![
            vec![Step::Error(ProviderError {
                kind: ProviderErrorKind::Network,
                retryable: true,
                emitted_output: false,
                message: "connection reset".into(),
            })],
            text_script("recovered"),
        ],
        vec![],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "hi")]).await;
    wait_turns(&h, 1).await;

    assert_eq!(h.requests.lock().unwrap().len(), 2);
    let turns = session_turns(&h);
    assert!(
        turns[0]
            .new_messages
            .iter()
            .any(|m| m == &Message::assistant_text("recovered"))
    );
    let events = h.events.lock().unwrap().clone();
    assert!(
        !events
            .iter()
            .any(|e| matches!(e, KernelEvent::Error { .. }))
    );
    drop(events);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn emitted_output_blocks_retry_and_surfaces_error() {
    let h = harness(
        vec![vec![
            Step::Event(ModelStreamEvent::Opened {
                model: "fake".into(),
            }),
            Step::Event(ModelStreamEvent::TextDelta {
                delta: "partial".into(),
            }),
            Step::Error(ProviderError {
                kind: ProviderErrorKind::Network,
                retryable: true,
                emitted_output: true,
                message: "cut mid-stream".into(),
            }),
        ]],
        vec![],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "hi")]).await;
    wait_turns(&h, 1).await;

    assert_eq!(h.requests.lock().unwrap().len(), 1);
    let turns = session_turns(&h);
    assert!(
        turns[0]
            .new_messages
            .iter()
            .any(|m| m == &Message::assistant_text("partial"))
    );
    let events = h.events.lock().unwrap().clone();
    assert!(events.iter().any(|e| matches!(
        e,
        KernelEvent::Error { code, .. } if code == "provider_network"
    )));
    drop(events);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn duplicate_command_is_dropped() {
    let h = harness(
        vec![text_script("once")],
        vec![],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(
        &h,
        vec![start_session(), submit("c1", "hi"), submit("c1", "hi")],
    )
    .await;
    wait_turns(&h, 1).await;
    tokio::time::sleep(std::time::Duration::from_millis(5)).await;

    assert_eq!(h.requests.lock().unwrap().len(), 1);
    assert_eq!(session_turns(&h).len(), 1);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn unknown_tool_gets_paired_error_result() {
    let h = harness(
        vec![
            call_script("call-1", "nope", "{\"x\":1}"),
            text_script("seen the error"),
        ],
        vec![echo_tool("echo")],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "go")]).await;
    wait_turns(&h, 1).await;

    let turns = session_turns(&h);
    match tool_results(&turns[0])[0] {
        ContentBlock::ToolResult {
            is_error, content, ..
        } => {
            assert!(is_error);
            let text = match &content[0] {
                OutputBlock::Text { text } => text.clone(),
                other => panic!("unexpected block: {other:?}"),
            };
            assert!(text.contains("unknown_tool"));
        }
        other => panic!("unexpected block: {other:?}"),
    }
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn stop_turn_on_success_cancels_remaining_calls() {
    let stopper_policy = ToolPolicy {
        stop_turn_on_success: true,
        ..ToolPolicy::default()
    };
    let stopper = Arc::new(FakeTool {
        spec: tool_spec("stopper", stopper_policy),
        behavior: Behavior::Text("stopped"),
        runs: Arc::new(AtomicU64::new(0)),
    });
    let follower_runs = Arc::new(AtomicU64::new(0));
    // Serial-only: a separate group after the stopper's, so it must be
    // cancelled instead of executed.
    let follower_policy = ToolPolicy {
        concurrency: alcoler_contracts::tool::Concurrency::SerialOnly,
        ..ToolPolicy::default()
    };
    let follower = Arc::new(FakeTool {
        spec: tool_spec("follower", follower_policy),
        behavior: Behavior::Text("never"),
        runs: follower_runs.clone(),
    });
    let two_calls: Script = vec![
        Step::Event(ModelStreamEvent::Opened {
            model: "fake".into(),
        }),
        Step::Event(ModelStreamEvent::ToolCallDelta {
            id: ToolCallId("call-1".into()),
            name: Some("stopper".into()),
            arguments_delta: "{\"x\":1}".into(),
        }),
        Step::Event(ModelStreamEvent::ToolCallDelta {
            id: ToolCallId("call-2".into()),
            name: Some("follower".into()),
            arguments_delta: "{\"x\":2}".into(),
        }),
        Step::Event(ModelStreamEvent::Completed {
            stop_reason: StopReason::ToolUse,
            usage: Usage::default(),
            reasoning_signature: None,
        }),
    ];
    let h = harness(
        vec![two_calls],
        vec![stopper, follower],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "go")]).await;
    wait_turns(&h, 1).await;

    assert_eq!(follower_runs.load(Ordering::SeqCst), 0);
    let turns = session_turns(&h);
    let results = tool_results(&turns[0]);
    assert_eq!(results.len(), 2);
    let events = h.events.lock().unwrap().clone();
    assert!(events.iter().any(|e| matches!(
        e,
        KernelEvent::ToolResult { status: ToolStatus::Cancelled, call, .. } if call.0 == "call-2"
    )));
    drop(events);
    // Turn ended: no second model request.
    assert_eq!(h.requests.lock().unwrap().len(), 1);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn oversized_output_truncated_with_blob_envelope() {
    let policy = ToolPolicy {
        output_budget_bytes: 1024,
        ..ToolPolicy::default()
    };
    let big = Arc::new(FakeTool {
        spec: tool_spec("big", policy),
        behavior: Behavior::Big(10_000),
        runs: Arc::new(AtomicU64::new(0)),
    });
    let blobs = Arc::new(Mutex::new(std::collections::HashMap::new()));
    let h = harness(
        vec![
            call_script("call-1", "big", "{\"x\":1}"),
            text_script("seen the preview"),
        ],
        vec![big],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        blobs.clone(),
        KernelConfig::default(),
    );

    drive(&h, vec![start_session(), submit("c1", "go")]).await;
    wait_turns(&h, 1).await;

    let turns = session_turns(&h);
    match tool_results(&turns[0])[0] {
        ContentBlock::ToolResult { content, .. } => {
            let text = match &content[0] {
                OutputBlock::Text { text } => text.clone(),
                other => panic!("unexpected block: {other:?}"),
            };
            assert!(text.starts_with('x'));
            assert!(text.len() <= 1024);
            let note = match &content[1] {
                OutputBlock::Text { text } => text.clone(),
                other => panic!("unexpected block: {other:?}"),
            };
            assert!(note.contains("blob"));
        }
        other => panic!("unexpected block: {other:?}"),
    }
    let events = h.events.lock().unwrap().clone();
    assert!(events.iter().any(|e| matches!(
        e,
        KernelEvent::ToolResult { bytes, .. } if *bytes == 10_000
    )));
    drop(events);
    assert_eq!(blobs.lock().unwrap().len(), 1);
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn queued_submissions_run_sequential_turns() {
    let h = harness(
        vec![text_script("answer-a"), text_script("answer-b")],
        vec![],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );

    drive(
        &h,
        vec![start_session(), submit("c1", "a"), submit("c2", "b")],
    )
    .await;
    wait_turns(&h, 2).await;

    let turns = session_turns(&h);
    assert_eq!(turns.len(), 2);
    assert_eq!(turns[0].turn, TurnId(1));
    assert_eq!(turns[1].turn, TurnId(2));
    // Turn 2's request history includes turn 1's exchange.
    let requests = h.requests.lock().unwrap().clone();
    assert!(
        requests[1]
            .messages
            .iter()
            .any(|m| m == &Message::assistant_text("answer-a"))
    );
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn compaction_clears_old_tool_results() {
    let config = KernelConfig {
        keep_tool_results: 0,
        ..KernelConfig::default()
    };
    let big = Arc::new(FakeTool {
        spec: tool_spec("big", ToolPolicy::default()),
        behavior: Behavior::Big(2000),
        runs: Arc::new(AtomicU64::new(0)),
    });
    // Tiny context budget: turn 1 stores a ~500-token tool result; turn 2
    // must clear it before its model request.
    let h = harness(
        vec![
            call_script("call-1", "big", "{\"x\":1}"),
            text_script("first done"),
            text_script("second done"),
        ],
        vec![big],
        FakeGate::allow(),
        Arc::new(Mutex::new(StoreState::default())),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        config,
    );

    drive(
        &h,
        vec![
            start_session_with_budget(64),
            submit("c1", "go"),
            submit("c2", "again"),
        ],
    )
    .await;
    wait_turns(&h, 2).await;

    let events = h.events.lock().unwrap().clone();
    assert!(
        events
            .iter()
            .any(|e| matches!(e, KernelEvent::Compacted { .. }))
    );
    drop(events);
    let turns = session_turns(&h);
    // The persisted snapshot carries the cleared placeholder, not the bulk.
    assert!(turns[1].snapshot.messages.iter().any(|m| {
        m.blocks.iter().any(|b| {
            matches!(
                b,
                ContentBlock::ToolResult { content, .. }
                    if matches!(&content[0], OutputBlock::Text { text } if text.contains("cleared"))
            )
        })
    }));
    h.cmd_tx.send(Command::Shutdown).await.unwrap();
    h.run.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn recovery_replays_history_into_next_turn() {
    let state = Arc::new(Mutex::new(StoreState::default()));
    let first = harness(
        vec![text_script("persisted answer")],
        vec![],
        FakeGate::allow(),
        state.clone(),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );
    drive(&first, vec![start_session(), submit("c1", "hi")]).await;
    wait_turns(&first, 1).await;
    first.cmd_tx.send(Command::Shutdown).await.unwrap();
    first.run.await.unwrap();

    // Second process over the same store: StartSession conflicts, recovers.
    let second = harness(
        vec![text_script("after restart")],
        vec![],
        FakeGate::allow(),
        state.clone(),
        Arc::new(Mutex::new(std::collections::HashMap::new())),
        KernelConfig::default(),
    );
    drive(&second, vec![start_session(), submit("c2", "continue")]).await;
    wait_turns(&second, 1).await;

    let events = second.events.lock().unwrap().clone();
    assert!(events.iter().any(|e| matches!(
        e,
        KernelEvent::SessionRecovered { from_turn, .. } if from_turn == &TurnId(1)
    )));
    drop(events);
    let turns = session_turns(&second);
    assert_eq!(turns.len(), 2);
    assert_eq!(turns[1].turn, TurnId(2));
    let requests = second.requests.lock().unwrap().clone();
    assert!(
        requests[0]
            .messages
            .iter()
            .any(|m| m == &Message::assistant_text("persisted answer"))
    );
    drop(requests);
    second.cmd_tx.send(Command::Shutdown).await.unwrap();
    second.run.await.unwrap();
}
