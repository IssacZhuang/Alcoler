//! Adapter unit tests: file store semantics and provider stream decoding
//! against recorded payloads (no network).

use std::sync::Arc;

use alcoler_adapters::model::anthropic::{AnthropicDecoder, build_body as anthropic_body};
use alcoler_adapters::model::openai::{OpenAiDecoder, build_body as openai_body};
use alcoler_adapters::model::sse::SseFrame;
use alcoler_adapters::storage::{FileBlobStore, FileSessionStore};
use alcoler_contracts::ids::{SessionId, ToolCallId, TurnId};
use alcoler_contracts::message::{ContentBlock, Message};
use alcoler_contracts::model::{
    CompletionRequest, ModelRef, ModelStreamEvent, StopReason, ToolChoice,
};
use alcoler_contracts::storage::{
    BlobStore, SessionMeta, SessionProfile, SessionStore, StoreErrorKind, TurnRecord,
};
use alcoler_contracts::tool::{ToolPolicy, ToolSpec};
use futures_util::StreamExt;
use serde_json::json;

// ---------- file store ----------

fn temp_root() -> std::path::PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "alcoler-test-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

fn meta(id: &str) -> SessionMeta {
    SessionMeta {
        id: SessionId(id.into()),
        profile: SessionProfile {
            name: "standard".into(),
            model: ModelRef {
                provider: "mock".into(),
                model: "m1".into(),
            },
            context_budget_tokens: 100_000,
            max_concurrent_tools: 4,
            event_buffer_bound: 1024,
            permission_timeout_ms: 60_000,
        },
        created_at_unix_ms: 1,
    }
}

fn turn_record(session: &str, turn: u64, text: &str) -> TurnRecord {
    TurnRecord {
        session: SessionId(session.into()),
        turn: TurnId(turn),
        new_messages: vec![Message::assistant_text(text)],
        events: Vec::new(),
        snapshot: alcoler_contracts::storage::SessionSnapshot {
            messages: vec![Message::assistant_text(text)],
            turns_completed: turn,
        },
    }
}

#[tokio::test]
async fn file_store_round_trip_and_conflict() {
    let root = temp_root();
    let store = FileSessionStore::new(&root);
    store.open().await.unwrap();

    store.create_session(&meta("s1")).await.unwrap();
    let conflict = store.create_session(&meta("s1")).await.unwrap_err();
    assert_eq!(conflict.kind, StoreErrorKind::Conflict);

    store
        .append_turn(turn_record("s1", 1, "one"))
        .await
        .unwrap();
    store
        .append_turn(turn_record("s1", 2, "two"))
        .await
        .unwrap();
    let conflict = store
        .append_turn(turn_record("s1", 1, "one again"))
        .await
        .unwrap_err();
    assert_eq!(conflict.kind, StoreErrorKind::Conflict);

    let record = store.load_session(&SessionId("s1".into())).await.unwrap();
    let record = record.expect("session exists");
    let snapshot = record.snapshot.expect("snapshot written");
    assert_eq!(snapshot.turns_completed, 2);
    assert_eq!(snapshot.messages.len(), 2);

    // Reopen from disk: the index rebuilds from the ledger.
    let reopened = FileSessionStore::new(&root);
    reopened.open().await.unwrap();
    let record = reopened
        .load_session(&SessionId("s1".into()))
        .await
        .unwrap()
        .expect("session exists");
    assert_eq!(record.snapshot.unwrap().turns_completed, 2);

    // Replay after turn 1 streams only turn 2.
    let mut stream = reopened
        .replay_turns(&SessionId("s1".into()), TurnId(1))
        .await
        .unwrap();
    let mut replayed = Vec::new();
    while let Some(item) = stream.next().await {
        replayed.push(item.unwrap());
    }
    assert_eq!(replayed.len(), 1);
    assert_eq!(replayed[0].turn, TurnId(2));

    let _ = std::fs::remove_dir_all(&root);
}

#[tokio::test]
async fn blob_store_round_trip() {
    let root = temp_root();
    let blobs = FileBlobStore::new(&root);
    let session = SessionId("s9".into());
    blobs
        .put(&session, "tool-output-call_1", b"full output".to_vec())
        .await
        .unwrap();
    let loaded = blobs.get(&session, "tool-output-call_1").await.unwrap();
    assert_eq!(loaded.as_deref(), Some(&b"full output"[..]));
    assert!(blobs.get(&session, "missing").await.unwrap().is_none());
    let _ = std::fs::remove_dir_all(&root);
}

// ---------- provider decoders ----------

fn sse_frame(event: Option<&str>, data: &str) -> SseFrame {
    SseFrame {
        event: event.map(str::to_string),
        data: data.to_string(),
    }
}

fn request_for_body_tests() -> CompletionRequest {
    CompletionRequest {
        model: ModelRef {
            provider: "anthropic".into(),
            model: "m1".into(),
        },
        system: Some("be brief".into()),
        messages: vec![
            Message::user_text("hi"),
            Message::assistant_text("hello"),
            Message::new(
                alcoler_contracts::message::Role::Assistant,
                vec![ContentBlock::ToolUse {
                    id: ToolCallId("call-1".into()),
                    name: "read_file".into(),
                    arguments: json!({"path": "a.txt"}),
                }],
            ),
            Message::new(
                alcoler_contracts::message::Role::User,
                vec![ContentBlock::ToolResult {
                    tool_use_id: ToolCallId("call-1".into()),
                    is_error: false,
                    content: vec![alcoler_contracts::message::OutputBlock::Text {
                        text: "contents".into(),
                    }],
                }],
            ),
        ],
        tools: vec![ToolSpec {
            name: "read_file".into(),
            description: "read".into(),
            input_schema: json!({"type": "object"}),
            policy: ToolPolicy::default(),
        }],
        tool_choice: ToolChoice::Auto,
        cache_breakpoints: Vec::new(),
        max_output_tokens: Some(1024),
        temperature: None,
        stop_sequences: vec![],
    }
}

#[test]
fn anthropic_body_maps_blocks_and_tools() {
    let body = anthropic_body(&request_for_body_tests());
    assert_eq!(body["system"], json!("be brief"));
    assert_eq!(body["max_tokens"], json!(1024));
    let messages = body["messages"].as_array().unwrap();
    assert_eq!(messages.len(), 3);
    assert_eq!(messages[0]["role"], json!("user"));
    // Consecutive assistant messages merge; text precedes the tool_use block.
    assert_eq!(messages[1]["role"], json!("assistant"));
    assert_eq!(messages[1]["content"][0]["type"], json!("text"));
    assert_eq!(messages[1]["content"][1]["type"], json!("tool_use"));
    assert_eq!(messages[1]["content"][1]["id"], json!("call-1"));
    // The tool result lands in a user message right after.
    assert_eq!(messages[2]["role"], json!("user"));
    assert_eq!(messages[2]["content"][0]["type"], json!("tool_result"));
    assert_eq!(messages[2]["content"][0]["tool_use_id"], json!("call-1"));
    let tools = body["tools"].as_array().unwrap();
    assert_eq!(tools[0]["name"], json!("read_file"));
    assert!(body["stream"].as_bool().unwrap());
}

#[test]
fn anthropic_decoder_streams_text_tool_and_completion() {
    let mut decoder = AnthropicDecoder::new("m1".into());
    let mut events = Vec::new();
    for (event, data) in [
        (
            Some("message_start"),
            r#"{"type":"message_start","message":{"model":"m1","usage":{"input_tokens":11,"cache_read_input_tokens":3,"cache_creation_input_tokens":2}}}"#,
        ),
        (
            Some("content_block_start"),
            r#"{"index":0,"content_block":{"type":"text"}}"#,
        ),
        (
            Some("content_block_delta"),
            r#"{"index":0,"delta":{"type":"text_delta","text":"Hello"}}"#,
        ),
        (
            Some("content_block_start"),
            r#"{"index":1,"content_block":{"type":"tool_use","id":"call-9","name":"read_file"}}"#,
        ),
        (
            Some("content_block_delta"),
            r#"{"index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}"#,
        ),
        (
            Some("content_block_delta"),
            r#"{"index":1,"delta":{"type":"input_json_delta","partial_json":"\"a.txt\"}"}}"#,
        ),
        (
            Some("message_delta"),
            r#"{"delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}"#,
        ),
        (Some("message_stop"), r#"{"type":"message_stop"}"#),
    ] {
        for step in decoder.feed(&sse_frame(event, data)) {
            events.push(step.unwrap());
        }
    }

    assert_eq!(events[0], ModelStreamEvent::Opened { model: "m1".into() });
    assert_eq!(
        events[1],
        ModelStreamEvent::TextDelta {
            delta: "Hello".into()
        }
    );
    assert_eq!(
        events[2],
        ModelStreamEvent::ToolCallDelta {
            id: ToolCallId("call-9".into()),
            name: Some("read_file".into()),
            arguments_delta: String::new(),
        }
    );
    assert_eq!(
        events[3],
        ModelStreamEvent::ToolCallDelta {
            id: ToolCallId("call-9".into()),
            name: None,
            arguments_delta: "{\"path\":".into(),
        }
    );
    assert_eq!(
        events[4],
        ModelStreamEvent::ToolCallDelta {
            id: ToolCallId("call-9".into()),
            name: None,
            arguments_delta: "\"a.txt\"}".into(),
        }
    );
    match &events[5] {
        ModelStreamEvent::Completed {
            stop_reason,
            usage,
            reasoning_signature,
        } => {
            assert_eq!(stop_reason, &StopReason::ToolUse);
            assert_eq!(usage.input_tokens, 11);
            assert_eq!(usage.cache_read_tokens, 3);
            assert_eq!(usage.cache_write_tokens, 2);
            assert_eq!(usage.output_tokens, 7);
            assert!(reasoning_signature.is_none());
        }
        ref other => panic!("unexpected event: {other:?}"),
    }
    assert!(decoder.finish().is_empty(), "no double completion");
}

#[test]
fn openai_body_maps_tool_results_as_tool_messages() {
    let body = openai_body(&request_for_body_tests());
    let messages = body["messages"].as_array().unwrap();
    assert_eq!(messages[0]["role"], json!("system"));
    assert_eq!(messages[1]["role"], json!("user"));
    assert_eq!(messages[2]["role"], json!("assistant"));
    assert_eq!(messages[3]["role"], json!("assistant"));
    assert_eq!(messages[3]["tool_calls"][0]["id"], json!("call-1"));
    assert_eq!(messages[4]["role"], json!("tool"));
    assert_eq!(messages[4]["tool_call_id"], json!("call-1"));
    assert_eq!(body["stream_options"]["include_usage"], json!(true));
}

#[test]
fn openai_decoder_streams_deltas_and_done() {
    let mut decoder = OpenAiDecoder::new("m1".into());
    let mut events = Vec::new();
    for data in [
        r#"{"model":"m1","choices":[{"delta":{"role":"assistant","content":"Hi"},"finish_reason":null}]}"#,
        r#"{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-2","type":"function","function":{"name":"list_dir","arguments":""}}]},"finish_reason":null}]}"#,
        r#"{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":\".\"}"}}]},"finish_reason":null}]}"#,
        r#"{"choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":9,"completion_tokens":4,"prompt_tokens_details":{"cached_tokens":5}}}"#,
        "[DONE]",
    ] {
        for step in decoder.feed(&sse_frame(None, data)) {
            events.push(step.unwrap());
        }
    }

    assert!(matches!(events[0], ModelStreamEvent::Opened { .. }));
    assert_eq!(
        events[1],
        ModelStreamEvent::TextDelta { delta: "Hi".into() }
    );
    assert_eq!(
        events[2],
        ModelStreamEvent::ToolCallDelta {
            id: ToolCallId("call-2".into()),
            name: Some("list_dir".into()),
            arguments_delta: String::new(),
        }
    );
    assert_eq!(
        events[3],
        ModelStreamEvent::ToolCallDelta {
            id: ToolCallId("call-2".into()),
            name: None,
            arguments_delta: "{\"path\":\".\"}".into(),
        }
    );
    match events[4] {
        ModelStreamEvent::Completed {
            stop_reason, usage, ..
        } => {
            assert_eq!(stop_reason, StopReason::ToolUse);
            assert_eq!(usage.input_tokens, 9);
            assert_eq!(usage.output_tokens, 4);
            assert_eq!(usage.cache_read_tokens, 5);
        }
        ref other => panic!("unexpected event: {other:?}"),
    }
}

#[test]
fn openai_reasoning_delta_maps_from_reasoning_content() {
    let mut decoder = OpenAiDecoder::new("m1".into());
    let steps = decoder.feed(&sse_frame(
        None,
        r#"{"choices":[{"delta":{"reasoning_content":"thinking hard"},"finish_reason":null}]}"#,
    ));
    assert!(matches!(
        steps[0].as_ref().unwrap(),
        ModelStreamEvent::Opened { .. }
    ));
    assert_eq!(
        steps[1].as_ref().unwrap(),
        &ModelStreamEvent::ReasoningDelta {
            delta: "thinking hard".into()
        }
    );
}

// ---------- routing provider ----------

#[tokio::test]
async fn routing_provider_dispatches_by_model_provider() {
    use alcoler_adapters::model::anthropic::AnthropicProvider;
    use alcoler_adapters::model::mock::MockProvider;
    use alcoler_adapters::model::router::RoutingProvider;
    use alcoler_contracts::model::{ModelProvider, ProviderErrorKind};

    let mock = Arc::new(MockProvider::default());
    let router = RoutingProvider::new(
        mock.clone() as Arc<dyn ModelProvider>,
        vec![Arc::new(AnthropicProvider::new("", "")) as Arc<dyn ModelProvider>],
    );
    let providers: Vec<String> = router.providers().await.into_iter().map(|p| p.0).collect();
    assert!(providers.contains(&"mock".to_owned()));
    assert!(providers.contains(&"anthropic".to_owned()));

    let mut req = request_for_body_tests();
    req.model = ModelRef {
        provider: "mock".into(),
        model: "mock-1".into(),
    };
    let mut stream = router.stream(req).await.unwrap();
    let first = stream.next().await.unwrap().unwrap();
    assert!(matches!(first, ModelStreamEvent::Opened { .. }));
    assert_eq!(mock.recorded_requests().len(), 1);

    let mut req = request_for_body_tests();
    req.model = ModelRef {
        provider: "nope".into(),
        model: "x".into(),
    };
    let error = match router.stream(req).await {
        Err(error) => error,
        Ok(_) => panic!("unknown provider must not resolve to a stream"),
    };
    assert_eq!(error.kind, ProviderErrorKind::InvalidRequest);
    assert!(!error.retryable);
    assert!(!error.emitted_output);
}
