//! Scripted mock provider: drives the full loop without network access.
//!
//! The default script runs one tool round trip (list_dir) and finishes;
//! custom scripts (per model request) can be injected for tests and demos.

use std::collections::VecDeque;
use std::sync::Mutex;

use alcoler_contracts::ids::{ProviderId, ToolCallId};
use alcoler_contracts::model::{
    CompletionRequest, ModelProvider, ModelStreamEvent, ProviderError, StopReason, Usage,
};
use alcoler_contracts::{BoxFuture, BoxStream};

pub struct MockProvider {
    id: ProviderId,
    scripts: Mutex<VecDeque<Vec<ModelStreamEvent>>>,
    requests: Mutex<Vec<CompletionRequest>>,
}

impl Default for MockProvider {
    fn default() -> Self {
        Self::new(default_script())
    }
}

fn default_script() -> Vec<Vec<ModelStreamEvent>> {
    vec![
        vec![
            ModelStreamEvent::Opened {
                model: "mock-1".into(),
            },
            ModelStreamEvent::TextDelta {
                delta: "Checking the workspace first.\n".into(),
            },
            ModelStreamEvent::ToolCallDelta {
                id: ToolCallId("mock-call-1".into()),
                name: Some("list_dir".into()),
                arguments_delta: String::new(),
            },
            ModelStreamEvent::ToolCallDelta {
                id: ToolCallId("mock-call-1".into()),
                name: None,
                arguments_delta: "{\"path\":\".\"}".into(),
            },
            ModelStreamEvent::Completed {
                stop_reason: StopReason::ToolUse,
                usage: Usage {
                    input_tokens: 10,
                    output_tokens: 20,
                    ..Usage::default()
                },
                reasoning_signature: None,
            },
        ],
        vec![
            ModelStreamEvent::Opened {
                model: "mock-1".into(),
            },
            ModelStreamEvent::TextDelta {
                delta: "Mock turn complete: one tool round trip executed.".into(),
            },
            ModelStreamEvent::Completed {
                stop_reason: StopReason::EndTurn,
                usage: Usage {
                    input_tokens: 30,
                    output_tokens: 15,
                    ..Usage::default()
                },
                reasoning_signature: None,
            },
        ],
    ]
}

impl MockProvider {
    pub fn new(scripts: Vec<Vec<ModelStreamEvent>>) -> Self {
        Self {
            id: ProviderId("mock".into()),
            scripts: Mutex::new(scripts.into()),
            requests: Mutex::new(Vec::new()),
        }
    }

    pub fn recorded_requests(&self) -> Vec<CompletionRequest> {
        self.requests.lock().unwrap().clone()
    }
}

impl ModelProvider for MockProvider {
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
            let script = self
                .scripts
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or_else(|| default_script().pop().unwrap());
            Ok(
                Box::pin(futures_util::stream::iter(script.into_iter().map(Ok)))
                    as BoxStream<'static, _>,
            )
        })
    }
}
