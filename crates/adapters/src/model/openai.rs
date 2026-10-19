//! OpenAI-compatible chat-completions provider (streaming): works with
//! OpenAI, GLM, DeepSeek, ollama, vllm, and other compatible endpoints.
//!
//! Reasoning blocks are not echoed back (the protocol has no request-side
//! field for them), so signatures are dropped on this dialect; tool results
//! map onto `role: "tool"` messages.

use std::collections::HashMap;
use std::collections::VecDeque;
use std::pin::Pin;
use std::task::{Context, Poll};

use alcoler_contracts::ids::{ProviderId, ToolCallId};
use alcoler_contracts::message::{ContentBlock, OutputBlock, Role};
use alcoler_contracts::model::{
    CompletionRequest, ModelProvider, ModelStreamEvent, ProviderError, ProviderErrorKind,
    StopReason, Usage,
};
use alcoler_contracts::{BoxFuture, BoxStream};
use bytes::Bytes;
use futures_util::Stream;
use serde_json::{Value, json};

use super::sse::{SseBuffer, SseFrame};

pub struct OpenAiProvider {
    id: ProviderId,
    http: reqwest::Client,
    base_url: String,
    api_key: String,
}

impl OpenAiProvider {
    pub fn new(base_url: impl Into<String>, api_key: impl Into<String>) -> Self {
        Self {
            id: ProviderId("openai".into()),
            http: reqwest::Client::new(),
            base_url: base_url.into(),
            api_key: api_key.into(),
        }
    }
}

impl ModelProvider for OpenAiProvider {
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
        let body = build_body(&req);
        let url = format!("{}/chat/completions", self.base_url.trim_end_matches('/'));
        let http = self.http.clone();
        let api_key = self.api_key.clone();
        Box::pin(async move {
            let mut request = http.post(&url).json(&body);
            if !api_key.is_empty() {
                request = request.header("authorization", format!("Bearer {api_key}"));
            }
            let response = request.send().await.map_err(|error| ProviderError {
                kind: ProviderErrorKind::Network,
                retryable: true,
                emitted_output: false,
                message: error.to_string(),
            })?;
            let status = response.status();
            if !status.is_success() {
                let text = response.text().await.unwrap_or_default();
                return Err(status_error(status, &text));
            }
            Ok(Box::pin(ResponseStream {
                bytes: Box::pin(response.bytes_stream()),
                sse: SseBuffer::new(),
                decoder: OpenAiDecoder::new(req.model.model),
                pending: VecDeque::new(),
                done: false,
            }) as BoxStream<'static, _>)
        })
    }
}

fn status_error(status: reqwest::StatusCode, body: &str) -> ProviderError {
    let (kind, retryable) = match status.as_u16() {
        401 | 403 => (ProviderErrorKind::Auth, false),
        400 | 404 | 422 => (ProviderErrorKind::InvalidRequest, false),
        429 => (ProviderErrorKind::RateLimited, true),
        500..=599 => (ProviderErrorKind::Overloaded, true),
        _ => (ProviderErrorKind::Protocol, false),
    };
    ProviderError {
        kind,
        retryable,
        emitted_output: false,
        message: format!(
            "HTTP {status}: {}",
            body.chars().take(500).collect::<String>()
        ),
    }
}

pub fn build_body(req: &CompletionRequest) -> Value {
    let mut messages: Vec<Value> = Vec::new();
    if let Some(system) = &req.system {
        messages.push(json!({"role": "system", "content": system}));
    }
    for message in &req.messages {
        match message.role {
            Role::User => {
                let text: Vec<&str> = message
                    .blocks
                    .iter()
                    .filter_map(|block| match block {
                        ContentBlock::Text { text } => Some(text.as_str()),
                        _ => None,
                    })
                    .collect();
                if !text.is_empty() {
                    messages.push(json!({"role": "user", "content": text.join("\n")}));
                }
                for block in &message.blocks {
                    if let ContentBlock::ToolResult {
                        tool_use_id,
                        content,
                        ..
                    } = block
                    {
                        let content: Vec<String> = content
                            .iter()
                            .map(|block| match block {
                                OutputBlock::Text { text } => text.clone(),
                                OutputBlock::Json { value } => value.to_string(),
                            })
                            .collect();
                        let content = if content.is_empty() {
                            "[no output]".to_string()
                        } else {
                            content.join("\n")
                        };
                        messages.push(json!({
                            "role": "tool",
                            "tool_call_id": tool_use_id.0,
                            "content": content,
                        }));
                    }
                }
            }
            Role::Assistant => {
                let text: Vec<&str> = message
                    .blocks
                    .iter()
                    .filter_map(|block| match block {
                        ContentBlock::Text { text } => Some(text.as_str()),
                        _ => None,
                    })
                    .collect();
                let tool_calls: Vec<Value> = message
                    .blocks
                    .iter()
                    .filter_map(|block| match block {
                        ContentBlock::ToolUse {
                            id,
                            name,
                            arguments,
                        } => Some(json!({
                            "id": id.0,
                            "type": "function",
                            "function": {"name": name, "arguments": arguments.to_string()},
                        })),
                        _ => None,
                    })
                    .collect();
                if !text.is_empty() || !tool_calls.is_empty() {
                    let mut value = json!({"role": "assistant"});
                    if !text.is_empty() {
                        value["content"] = json!(text.join("\n"));
                    }
                    if !tool_calls.is_empty() {
                        value["tool_calls"] = Value::Array(tool_calls);
                    }
                    messages.push(value);
                }
            }
        }
    }

    let mut body = json!({
        "model": req.model.model,
        "messages": messages,
        "stream": true,
        "stream_options": {"include_usage": true},
    });
    if let Some(max_tokens) = req.max_output_tokens {
        body["max_tokens"] = json!(max_tokens);
    }
    if let Some(temperature) = req.temperature {
        body["temperature"] = json!(temperature);
    }
    if !req.stop_sequences.is_empty() {
        body["stop"] = json!(req.stop_sequences);
    }
    if !req.tools.is_empty() {
        body["tools"] = Value::Array(
            req.tools
                .iter()
                .map(|tool| {
                    json!({
                        "type": "function",
                        "function": {
                            "name": tool.name,
                            "description": tool.description,
                            "parameters": tool.input_schema,
                        },
                    })
                })
                .collect(),
        );
    }
    body
}

pub type DecodeStep = Result<ModelStreamEvent, ProviderError>;

pub struct OpenAiDecoder {
    model: String,
    opened: bool,
    content_emitted: bool,
    index_to_id: HashMap<u64, ToolCallId>,
    stop_reason: Option<StopReason>,
    usage: Usage,
    completed: bool,
}

impl OpenAiDecoder {
    pub fn new(model: String) -> Self {
        Self {
            model,
            opened: false,
            content_emitted: false,
            index_to_id: HashMap::new(),
            stop_reason: None,
            usage: Usage::default(),
            completed: false,
        }
    }

    pub fn content_emitted(&self) -> bool {
        self.content_emitted
    }

    pub fn feed(&mut self, frame: &SseFrame) -> Vec<DecodeStep> {
        let mut out = Vec::new();
        if frame.data.trim() == "[DONE]" {
            self.completed = true;
            out.push(Ok(ModelStreamEvent::Completed {
                stop_reason: self.stop_reason.unwrap_or(StopReason::EndTurn),
                usage: self.usage,
                reasoning_signature: None,
            }));
            return out;
        }
        let Ok(data) = serde_json::from_str::<Value>(&frame.data) else {
            return out;
        };

        if !self.opened {
            self.opened = true;
            let model = data["model"].as_str().unwrap_or(&self.model).to_string();
            out.push(Ok(ModelStreamEvent::Opened { model }));
        }
        if let Some(usage) = data["usage"].as_object() {
            self.usage.input_tokens = usage
                .get("prompt_tokens")
                .and_then(Value::as_u64)
                .unwrap_or(0);
            self.usage.output_tokens = usage
                .get("completion_tokens")
                .and_then(Value::as_u64)
                .unwrap_or(0);
            self.usage.cache_read_tokens = usage
                .get("prompt_tokens_details")
                .and_then(|details| details.get("cached_tokens"))
                .and_then(Value::as_u64)
                .unwrap_or(0);
        }

        let Some(choice) = data["choices"].get(0) else {
            return out;
        };
        if let Some(delta) = choice.get("delta") {
            if let Some(text) = delta["content"].as_str()
                && !text.is_empty()
            {
                self.content_emitted = true;
                out.push(Ok(ModelStreamEvent::TextDelta {
                    delta: text.to_string(),
                }));
            }
            let reasoning = delta["reasoning_content"]
                .as_str()
                .or_else(|| delta["reasoning"].as_str());
            if let Some(text) = reasoning
                && !text.is_empty()
            {
                self.content_emitted = true;
                out.push(Ok(ModelStreamEvent::ReasoningDelta {
                    delta: text.to_string(),
                }));
            }
            if let Some(tool_calls) = delta["tool_calls"].as_array() {
                for tool_call in tool_calls {
                    let index = tool_call["index"].as_u64().unwrap_or(0);
                    let name = tool_call["function"]["name"].as_str();
                    let fragment = tool_call["function"]["arguments"].as_str();
                    if let Some(id) = tool_call["id"].as_str() {
                        self.index_to_id.insert(index, ToolCallId(id.to_string()));
                        self.content_emitted = true;
                        out.push(Ok(ModelStreamEvent::ToolCallDelta {
                            id: ToolCallId(id.to_string()),
                            name: name.map(str::to_string),
                            arguments_delta: String::new(),
                        }));
                        continue;
                    }
                    let Some(id) = self.index_to_id.get(&index).cloned() else {
                        continue;
                    };
                    if let Some(name) = name {
                        out.push(Ok(ModelStreamEvent::ToolCallDelta {
                            id: id.clone(),
                            name: Some(name.to_string()),
                            arguments_delta: String::new(),
                        }));
                    }
                    if let Some(fragment) = fragment
                        && !fragment.is_empty()
                    {
                        self.content_emitted = true;
                        out.push(Ok(ModelStreamEvent::ToolCallDelta {
                            id: id.clone(),
                            name: None,
                            arguments_delta: fragment.to_string(),
                        }));
                    }
                }
            }
        }
        if let Some(finish) = choice["finish_reason"].as_str() {
            self.stop_reason = Some(map_finish_reason(finish));
        }
        out
    }

    pub fn finish(&mut self) -> Vec<DecodeStep> {
        if self.completed {
            return Vec::new();
        }
        self.completed = true;
        if self.content_emitted {
            vec![Ok(ModelStreamEvent::Completed {
                stop_reason: self.stop_reason.unwrap_or(StopReason::EndTurn),
                usage: self.usage,
                reasoning_signature: None,
            })]
        } else {
            vec![Err(ProviderError {
                kind: ProviderErrorKind::Protocol,
                retryable: true,
                emitted_output: false,
                message: "stream ended without [DONE]".to_string(),
            })]
        }
    }
}

fn map_finish_reason(reason: &str) -> StopReason {
    match reason {
        "tool_calls" | "function_call" => StopReason::ToolUse,
        "length" => StopReason::MaxTokens,
        "content_filter" => StopReason::Refusal,
        "stop" => StopReason::EndTurn,
        _ => StopReason::EndTurn,
    }
}

struct ResponseStream {
    bytes: Pin<Box<dyn Stream<Item = reqwest::Result<Bytes>> + Send>>,
    sse: SseBuffer,
    decoder: OpenAiDecoder,
    pending: VecDeque<DecodeStep>,
    done: bool,
}

impl Stream for ResponseStream {
    type Item = Result<ModelStreamEvent, ProviderError>;

    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        loop {
            if let Some(step) = self.pending.pop_front() {
                return Poll::Ready(Some(step));
            }
            if self.done {
                return Poll::Ready(None);
            }
            match self.bytes.as_mut().poll_next(cx) {
                Poll::Ready(Some(Ok(chunk))) => {
                    self.sse.push(&chunk);
                    for frame in self.sse.frames() {
                        for step in self.decoder.feed(&frame) {
                            self.pending.push_back(step);
                        }
                    }
                }
                Poll::Ready(Some(Err(error))) => {
                    self.done = true;
                    let emitted = self.decoder.content_emitted();
                    self.pending.push_back(Err(ProviderError {
                        kind: ProviderErrorKind::Network,
                        retryable: true,
                        emitted_output: emitted,
                        message: error.to_string(),
                    }));
                }
                Poll::Ready(None) => {
                    self.done = true;
                    for step in self.decoder.finish() {
                        self.pending.push_back(step);
                    }
                }
                Poll::Pending => return Poll::Pending,
            }
        }
    }
}
