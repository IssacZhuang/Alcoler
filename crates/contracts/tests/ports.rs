//! Port implementability and object-safety checks + wire JSON shape
//! regressions.
//!
//! These tests pull in no async runtime: the stubs complete immediately and
//! are driven by a hand-rolled minimal `block_on` (noop waker + busy-wait),
//! proving the contract layer is truly runtime-agnostic.

use std::marker::PhantomData;
use std::pin::Pin;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::task::{Context, Poll, Waker};

use alcoler_contracts::context::TokenCounter;
use alcoler_contracts::ids::{CommandId, ProviderId, SessionId, ToolCallId, TurnId};
use alcoler_contracts::message::{ContentBlock, Message, OutputBlock, Role};
use alcoler_contracts::model::{CompletionRequest, ModelProvider, ModelStreamEvent, ProviderError};
use alcoler_contracts::permission::{PermissionDecision, PermissionGate, PermissionRequest};
use alcoler_contracts::storage::{
    BlobRef, BlobStore, RetainReport, RetentionPolicy, SessionMeta, SessionRecord, SessionStore,
    SessionSummary, StoreError, TurnProgress, TurnRecord,
};
use alcoler_contracts::time::{CancelToken, Clock, IdGenerator};
use alcoler_contracts::tool::{
    Tool, ToolExecContext, ToolOutput, ToolPolicy, ToolSpec, ToolStatus,
};
use alcoler_contracts::wire::{
    CancelScope, Command, CommandSource, EventSink, KernelEvent, UserInput, WireError,
};
use alcoler_contracts::{BoxFuture, BoxStream};
use futures_core::Stream;

fn block_on<F: Future>(fut: F) -> F::Output {
    let mut fut = std::pin::pin!(fut);
    let waker = Waker::noop();
    let mut cx = Context::from_waker(waker);
    loop {
        match fut.as_mut().poll(&mut cx) {
            Poll::Ready(v) => return v,
            Poll::Pending => std::thread::yield_now(),
        }
    }
}

// ---------- Stubs: one per port ----------

struct EmptyStream<T>(PhantomData<fn() -> T>);

impl<T> Stream for EmptyStream<T> {
    type Item = T;
    fn poll_next(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<Option<T>> {
        Poll::Ready(None)
    }
}

struct FakeProvider {
    id: ProviderId,
}

impl ModelProvider for FakeProvider {
    fn provider_id(&self) -> &ProviderId {
        &self.id
    }

    fn stream(
        &self,
        _req: CompletionRequest,
    ) -> BoxFuture<
        '_,
        Result<BoxStream<'static, Result<ModelStreamEvent, ProviderError>>, ProviderError>,
    > {
        Box::pin(async {
            let s = EmptyStream::<Result<ModelStreamEvent, ProviderError>>(PhantomData);
            Ok(Box::pin(s) as BoxStream<'static, _>)
        })
    }
}

struct FakeTool {
    spec: ToolSpec,
}

impl Tool for FakeTool {
    fn spec(&self) -> &ToolSpec {
        &self.spec
    }

    fn execute<'a>(
        &'a self,
        _input: serde_json::Value,
        _ctx: &'a ToolExecContext,
    ) -> BoxFuture<'a, ToolOutput> {
        Box::pin(async {
            let mut out = ToolOutput::ok(vec![OutputBlock::Text {
                text: "done".into(),
            }]);
            out.bytes_emitted = 4;
            out
        })
    }
}

struct FakeGate;

impl PermissionGate for FakeGate {
    fn decide<'a>(&'a self, _request: &'a PermissionRequest) -> BoxFuture<'a, PermissionDecision> {
        Box::pin(async { PermissionDecision::Allow })
    }
}

struct FakeStore;

impl SessionStore for FakeStore {
    fn open(&self) -> BoxFuture<'_, Result<(), StoreError>> {
        Box::pin(async { Ok(()) })
    }
    fn create_session<'a>(
        &'a self,
        _meta: &'a SessionMeta,
    ) -> BoxFuture<'a, Result<(), StoreError>> {
        Box::pin(async { Ok(()) })
    }
    fn load_session<'a>(
        &'a self,
        _id: &'a SessionId,
    ) -> BoxFuture<'a, Result<Option<SessionRecord>, StoreError>> {
        Box::pin(async { Ok(None) })
    }
    fn list_sessions(&self) -> BoxFuture<'_, Result<Vec<SessionSummary>, StoreError>> {
        Box::pin(async { Ok(Vec::new()) })
    }
    fn save_turn_progress<'a>(
        &'a self,
        _progress: &'a TurnProgress,
    ) -> BoxFuture<'a, Result<(), StoreError>> {
        Box::pin(async { Ok(()) })
    }
    fn append_turn(&self, record: TurnRecord) -> BoxFuture<'_, Result<(), StoreError>> {
        Box::pin(async {
            let _ = record;
            Ok(())
        })
    }
    fn replay_turns<'a>(
        &'a self,
        _id: &'a SessionId,
        _after: TurnId,
    ) -> BoxFuture<'a, Result<BoxStream<'static, Result<TurnRecord, StoreError>>, StoreError>> {
        Box::pin(async {
            let s = EmptyStream::<Result<TurnRecord, StoreError>>(PhantomData);
            Ok(Box::pin(s) as BoxStream<'static, _>)
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

struct FakeBlobs;

impl BlobStore for FakeBlobs {
    fn put<'a>(
        &'a self,
        session: &'a SessionId,
        key: &'a str,
        _bytes: Vec<u8>,
    ) -> BoxFuture<'a, Result<BlobRef, StoreError>> {
        Box::pin(async {
            Ok(BlobRef {
                session: session.clone(),
                key: key.to_string(),
            })
        })
    }
    fn get<'a>(
        &'a self,
        _session: &'a SessionId,
        _key: &'a str,
    ) -> BoxFuture<'a, Result<Option<Vec<u8>>, StoreError>> {
        Box::pin(async { Ok(None) })
    }
}

struct FakeCounter;

impl TokenCounter for FakeCounter {
    fn count_text(&self, _model: &alcoler_contracts::model::ModelRef, text: &str) -> u64 {
        text.len() as u64 / 4
    }
    fn count_message(&self, model: &alcoler_contracts::model::ModelRef, message: &Message) -> u64 {
        let text: String = message
            .blocks
            .iter()
            .filter_map(|b| match b {
                ContentBlock::Text { text } => Some(text.as_str()),
                _ => None,
            })
            .collect();
        self.count_text(model, &text)
    }
}

struct FakeClock;

impl Clock for FakeClock {
    fn unix_ms(&self) -> i64 {
        1_700_000_000_000
    }
    fn monotonic_ms(&self) -> u64 {
        42
    }
    fn delay_ms(&self, _ms: u64) -> BoxFuture<'static, ()> {
        Box::pin(async {})
    }
}

struct FakeIds(AtomicU64);

impl IdGenerator for FakeIds {
    fn next_id(&self) -> String {
        format!("id-{}", self.0.fetch_add(1, Ordering::SeqCst))
    }
}

struct FakeCancel(AtomicU64);

impl CancelToken for FakeCancel {
    fn cancel(&self) {
        self.0.fetch_add(1, Ordering::SeqCst);
    }
    fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::SeqCst) > 0
    }
    fn cancelled(&self) -> BoxFuture<'static, ()> {
        Box::pin(async {})
    }
}

struct FakeSink;

impl EventSink for FakeSink {
    fn emit(&self, _event: KernelEvent) -> BoxFuture<'_, Result<(), WireError>> {
        Box::pin(async { Ok(()) })
    }
}

struct FakeSource;

impl CommandSource for FakeSource {
    fn next_command(&mut self) -> BoxFuture<'_, Option<Command>> {
        Box::pin(async { None })
    }
}

// ---------- Tests ----------

fn fake_spec() -> ToolSpec {
    ToolSpec {
        name: "read".into(),
        description: "Read a file".into(),
        input_schema: serde_json::json!({
            "type": "object",
            "properties": { "path": { "type": "string" } },
            "required": ["path"]
        }),
        policy: ToolPolicy::default(),
    }
}

/// All ports assemble as trait objects (object safety), and the composition
/// root can run one minimal call chain with no runtime dependency.
#[test]
fn ports_are_object_safe_and_composable() {
    let provider: Arc<dyn ModelProvider> = Arc::new(FakeProvider {
        id: ProviderId("mock".into()),
    });
    let tools: Vec<Arc<dyn Tool>> = vec![Arc::new(FakeTool { spec: fake_spec() })];
    let gate: Arc<dyn PermissionGate> = Arc::new(FakeGate);
    let store: Arc<dyn SessionStore> = Arc::new(FakeStore);
    let blobs: Arc<dyn BlobStore> = Arc::new(FakeBlobs);
    let counter: Arc<dyn TokenCounter> = Arc::new(FakeCounter);
    let clock: Arc<dyn Clock> = Arc::new(FakeClock);
    let ids: Arc<dyn IdGenerator> = Arc::new(FakeIds(AtomicU64::new(0)));
    let cancel: Arc<dyn CancelToken> = Arc::new(FakeCancel(AtomicU64::new(0)));
    let sink: Arc<dyn EventSink> = Arc::new(FakeSink);
    let mut source: Box<dyn CommandSource> = Box::new(FakeSource);

    // Metadata-driven scheduling decisions run directly on port data.
    let spec = tools[0].spec();
    assert_eq!(
        spec.policy.effect,
        alcoler_contracts::tool::ToolEffect::ReadOnly
    );
    assert!(spec.policy.approval == alcoler_contracts::tool::ApprovalPolicy::Never);

    // One minimal call chain: source closed → gate allows → tool executes →
    // event sink → persistence.
    assert!(block_on(source.next_command()).is_none());
    let decision = block_on(gate.decide(&PermissionRequest {
        session: SessionId("s1".into()),
        turn: TurnId(1),
        tool: "read".into(),
        effect: spec.policy.effect,
        effect_scope: spec.policy.effect_scope,
        risk: spec.policy.risk,
        input_digest: "path=…".into(),
    }));
    assert_eq!(decision, PermissionDecision::Allow);

    let ctx = ToolExecContext {
        session: SessionId("s1".into()),
        turn: TurnId(1),
        call: ToolCallId("call_1".into()),
        deadline_monotonic_ms: None,
        cancel: cancel.clone(),
    };
    let out = block_on(tools[0].execute(serde_json::json!({ "path": "a.rs" }), &ctx));
    assert_eq!(out.status, ToolStatus::Ok);
    assert_eq!(out.bytes_emitted, 4);

    assert_eq!(
        block_on(sink.emit(KernelEvent::TurnDone {
            session: SessionId("s1".into()),
            turn: TurnId(1),
            ts_ms: clock.monotonic_ms(),
        })),
        Ok(())
    );
    assert!(
        block_on(store.append_turn(TurnRecord {
            session: SessionId("s1".into()),
            turn: TurnId(1),
            new_messages: vec![Message::new(
                Role::Assistant,
                vec![ContentBlock::ToolUse {
                    id: ToolCallId("call_1".into()),
                    name: "read".into(),
                    arguments: serde_json::json!({ "path": "a.rs" }),
                }],
            )],
            events: Vec::new(),
            snapshot: alcoler_contracts::storage::SessionSnapshot {
                messages: Vec::new(),
                turns_completed: 1,
            },
        }))
        .is_ok()
    );

    // Provider stream ends right after opening (stub); cancel token fires.
    cancel.cancel();
    assert!(cancel.is_cancelled());
    let stream = block_on(provider.stream(CompletionRequest {
        model: alcoler_contracts::model::ModelRef {
            provider: "mock".into(),
            model: "m1".into(),
        },
        system: None,
        messages: vec![Message::user_text("hi")],
        tools: vec![fake_spec()],
        tool_choice: alcoler_contracts::model::ToolChoice::Auto,
        cache_breakpoints: Vec::new(),
        max_output_tokens: None,
        temperature: None,
        stop_sequences: Vec::new(),
    }))
    .unwrap();
    let mut stream = stream;
    let mut cx = Context::from_waker(Waker::noop());
    assert_eq!(stream.as_mut().poll_next(&mut cx), Poll::Ready(None));

    // Blob write, id generation, and token counting.
    let blob = block_on(blobs.put(&SessionId("s1".into()), &ids.next_id(), b"x".to_vec())).unwrap();
    assert_eq!(blob.key, "id-0");
    let model_ref = alcoler_contracts::model::ModelRef {
        provider: "mock".into(),
        model: "m1".into(),
    };
    assert_eq!(counter.count_text(&model_ref, "abcdabcd"), 2);
}

/// Event JSON shapes are name-compatible with the E1 black-box contract
/// (type tags and key fields).
#[test]
fn kernel_event_shapes_match_e1_contract() {
    let s1 = SessionId("s1".into());

    let cases: Vec<(KernelEvent, &str, &str)> = vec![
        (
            KernelEvent::SessionStarted {
                session: s1.clone(),
                ts_ms: 1,
            },
            r#""type":"session_started""#,
            r#""session":"s1""#,
        ),
        (
            KernelEvent::TurnStarted {
                session: s1.clone(),
                turn: TurnId(1),
                ts_ms: 1,
            },
            r#""type":"turn_started""#,
            r#""turn":1"#,
        ),
        (
            KernelEvent::ModelDelta {
                session: s1.clone(),
                turn: TurnId(1),
                seq: 0,
                text: String::new(),
                bytes: 2048,
                ts_ms: 1,
            },
            r#""type":"model_delta""#,
            r#""bytes":2048"#,
        ),
        (
            KernelEvent::ToolCall {
                session: s1.clone(),
                turn: TurnId(1),
                call: ToolCallId("call_9".into()),
                tool: "exec".into(),
                input: serde_json::json!({ "cmd": "ls" }),
                ts_ms: 1,
            },
            r#""type":"tool_call""#,
            r#""tool":"exec""#,
        ),
        (
            KernelEvent::ToolResult {
                session: s1.clone(),
                turn: TurnId(1),
                call: ToolCallId("call_9".into()),
                status: ToolStatus::Ok,
                bytes: 4096,
                ts_ms: 1,
            },
            r#""type":"tool_result""#,
            r#""bytes":4096"#,
        ),
        (
            KernelEvent::Compacted {
                session: s1.clone(),
                kept_tokens: 12000,
                ts_ms: 1,
            },
            r#""type":"compacted""#,
            r#""kept_tokens":12000"#,
        ),
        (
            KernelEvent::TurnDone {
                session: s1.clone(),
                turn: TurnId(1),
                ts_ms: 1,
            },
            r#""type":"turn_done""#,
            r#""turn":1"#,
        ),
        (
            KernelEvent::Error {
                session: Some(s1.clone()),
                code: "E1".into(),
                fatal: false,
                ts_ms: 1,
            },
            r#""type":"error""#,
            r#""fatal":false"#,
        ),
    ];

    for (event, want_tag, want_field) in cases {
        let json = serde_json::to_string(&event).unwrap();
        assert!(json.contains(want_tag), "tag missing in {json}");
        assert!(json.contains(want_field), "field missing in {json}");
        // Lossless round trip (persistence and IPC share the same shape).
        let back: KernelEvent = serde_json::from_str(&json).unwrap();
        assert_eq!(back, event);
    }

    // Failure-status shape of tool_result (external tag + snake_case).
    let failed = KernelEvent::ToolResult {
        session: s1,
        turn: TurnId(2),
        call: ToolCallId("call_x".into()),
        status: ToolStatus::TimedOut,
        bytes: 0,
        ts_ms: 1,
    };
    let json = serde_json::to_string(&failed).unwrap();
    assert!(json.contains(r#""status":"timed_out""#), "got {json}");
}

/// Command shapes and the key fields for dedup/cancel.
#[test]
fn command_shapes_round_trip() {
    let json = r#"{
        "cmd": "submit",
        "session": "s1",
        "command": "cmd-7",
        "input": { "text": "fix the build error" }
    }"#;
    let cmd: Command = serde_json::from_str(json).unwrap();
    assert_eq!(
        cmd,
        Command::Submit {
            session: SessionId("s1".into()),
            command: CommandId("cmd-7".into()),
            input: UserInput {
                text: "fix the build error".into()
            },
        }
    );

    let cancel: Command =
        serde_json::from_str(r#"{ "cmd": "cancel", "session": "s1", "scope": { "turn": 42 } }"#)
            .unwrap();
    assert_eq!(
        cancel,
        Command::Cancel {
            session: SessionId("s1".into()),
            scope: CancelScope::Turn(TurnId(42)),
        }
    );

    let shutdown: Command = serde_json::from_str(r#"{ "cmd": "shutdown" }"#).unwrap();
    assert_eq!(shutdown, Command::Shutdown);
}

/// Pairing integrity: tool_use / tool_result id pairing survives
/// serialization.
#[test]
fn tool_use_tool_result_pairing_survives_serialization() {
    let call = ToolCallId("call_b9c7".into());
    let history = vec![
        Message::new(
            Role::Assistant,
            vec![ContentBlock::ToolUse {
                id: call.clone(),
                name: "Glob".into(),
                arguments: serde_json::json!({ "pattern": "**/*.rs" }),
            }],
        ),
        Message::new(
            Role::User,
            vec![ContentBlock::ToolResult {
                tool_use_id: call.clone(),
                is_error: false,
                content: vec![OutputBlock::Text {
                    text: "crates/contracts/src/lib.rs".into(),
                }],
            }],
        ),
    ];
    let json = serde_json::to_string(&history).unwrap();
    let back: Vec<Message> = serde_json::from_str(&json).unwrap();
    assert_eq!(back, history);
    // Pairing is directly checkable by consumers.
    let uses: Vec<&ToolCallId> = back[0]
        .blocks
        .iter()
        .filter_map(|b| match b {
            ContentBlock::ToolUse { id, .. } => Some(id),
            _ => None,
        })
        .collect();
    let results: Vec<&ToolCallId> = back[1]
        .blocks
        .iter()
        .filter_map(|b| match b {
            ContentBlock::ToolResult { tool_use_id, .. } => Some(tool_use_id),
            _ => None,
        })
        .collect();
    assert_eq!(uses, results);
}
