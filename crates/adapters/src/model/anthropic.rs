//! Anthropic Messages API provider (streaming).
//!
//! Dropping the returned stream drops the underlying HTTP response body,
//! closing the connection — the contract's cancel-on-drop obligation.

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

pub struct AnthropicProvider {
    id: ProviderId,
    http: reqwest::Client,
    base_url: String,
    api_key: String,
}

impl AnthropicProvider {
    pub fn new(base_url: impl Into<String>, api_key: impl Into<String>) -> Self {
        Self {
            id: ProviderId("anthropic".into()),
            http: reqwest::Client::new(),
            base_url: base_url.into(),
            api_key: api_key.into(),
        }
    }
}

impl ModelProvider for AnthropicProvider {
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
        let url = format!("{}/v1/messages", self.base_url.trim_end_matches('/'));
        let http = self.http.clone();
        let api_key = self.api_key.clone();
        Box::pin(async move {
            let response = http
                .post(&url)
                .header("x-api-key", &api_key)
                .header("anthropic-version", "2023-06-01")
                .json(&body)
                .send()
                .await
                .map_err(|error| ProviderError {
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
                decoder: AnthropicDecoder::new(req.model.model),
                pending: VecDeque::new(),
                done: false,
            }) as BoxStream<'static, _>)
        })
    }
}

fn status_error(status: reqwest::StatusCode, body: &str) -> ProviderError {
    let (kind, retryable) = match status.as_u16() {
        401 | 403 => (ProviderErrorKind::Auth, false),
        404 | 400 | 422 => (ProviderErrorKind::InvalidRequest, false),
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

fn block_to_value(block: &ContentBlock) -> Option<Value> {
    match block {
        ContentBlock::Text { text } if !text.is_empty() => {
            Some(json!({"type": "text", "text": text}))
        }
        ContentBlock::Reasoning { text, signature } if !text.is_empty() => {
            let mut value = json!({"type": "thinking", "thinking": text});
            if let Some(signature) = signature {
                value["signature"] = json!(signature);
            }
            Some(value)
        }
        ContentBlock::ToolUse {
            id,
            name,
            arguments,
        } => Some(json!({
            "type": "tool_use", "id": id.0, "name": name, "input": arguments
        })),
        ContentBlock::ToolResult {
            tool_use_id,
            is_error,
            content,
        } => {
            let blocks: Vec<Value> = content
                .iter()
                .map(|block| match block {
                    OutputBlock::Text { text } => json!({"type": "text", "text": text}),
                    OutputBlock::Json { value } => {
                        json!({"type": "text", "text": value.to_string()})
                    }
                })
                .collect();
            Some(json!({
                "type": "tool_result",
                "tool_use_id": tool_use_id.0,
                "content": if blocks.is_empty() { json!("[no output]") } else { Value::Array(blocks) },
                "is_error": is_error,
            }))
        }
        _ => None,
    }
}

pub fn build_body(req: &CompletionRequest) -> Value {
    // Anthropic requires role alternation; consecutive same-role kernel
    // messages merge into one content array.
    let mut messages: Vec<Value> = Vec::new();
    for message in &req.messages {
        let role = match message.role {
            Role::User => "user",
            Role::Assistant => "assistant",
        };
        let blocks: Vec<Value> = message.blocks.iter().filter_map(block_to_value).collect();
        if blocks.is_empty() {
            continue;
        }
        if let Some(last) = messages.last_mut()
            && last["role"] == role
        {
            last["content"].as_array_mut().unwrap().extend(blocks);
            continue;
        }
        messages.push(json!({"role": role, "content": blocks}));
    }

    let mut body = json!({
        "model": req.model.model,
        "max_tokens": req.max_output_tokens.unwrap_or(8192),
        "messages": messages,
        "stream": true,
    });
    if let Some(system) = &req.system {
        body["system"] = json!(system);
    }
    if let Some(temperature) = req.temperature {
        body["temperature"] = json!(temperature);
    }
    if !req.stop_sequences.is_empty() {
        body["stop_sequences"] = json!(req.stop_sequences);
    }
    if !req.tools.is_empty() {
        body["tools"] = Value::Array(
            req.tools
                .iter()
                .map(|tool| {
                    json!({
                        "name": tool.name,
                        "description": tool.description,
                        "input_schema": tool.input_schema,
                    })
                })
                .collect(),
        );
    }
    body
}

pub type DecodeStep = Result<ModelStreamEvent, ProviderError>;

/// Pure SSE-payload decoder, unit-testable without HTTP.
pub struct AnthropicDecoder {
    model: String,
    opened: bool,
    content_emitted: bool,
    tool_ids: HashMap<u64, ToolCallId>,
    signature: Option<String>,
    stop_reason: Option<StopReason>,
    usage: Usage,
    completed: bool,
}

impl AnthropicDecoder {
    pub fn new(model: String) -> Self {
        Self {
            model,
            opened: false,
            content_emitted: false,
            tool_ids: HashMap::new(),
            signature: None,
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
        let event = frame.event.clone().unwrap_or_default();
        let Ok(data) = serde_json::from_str::<Value>(&frame.data) else {
            return out;
        };

        match event.as_str() {
            "message_start" => {
                if !self.opened {
                    self.opened = true;
                    let model = data["message"]["model"]
                        .as_str()
                        .unwrap_or(&self.model)
                        .to_string();
                    if let Some(usage) = data["message"]["usage"].as_object() {
                        self.usage.input_tokens = usage
                            .get("input_tokens")
                            .and_then(Value::as_u64)
                            .unwrap_or(0);
                        self.usage.cache_read_tokens = usage
                            .get("cache_read_input_tokens")
                            .and_then(Value::as_u64)
                            .unwrap_or(0);
                        self.usage.cache_write_tokens = usage
                            .get("cache_creation_input_tokens")
                            .and_then(Value::as_u64)
                            .unwrap_or(0);
                    }
                    out.push(Ok(ModelStreamEvent::Opened { model }));
                }
            }
            "content_block_start" => {
                let index = data["index"].as_u64().unwrap_or(0);
                if data["content_block"]["type"] == "tool_use" {
                    let id = ToolCallId(
                        data["content_block"]["id"]
                            .as_str()
                            .unwrap_or_default()
                            .to_string(),
                    );
                    let name = data["content_block"]["name"]
                        .as_str()
                        .unwrap_or_default()
                        .to_string();
                    self.tool_ids.insert(index, id.clone());
                    self.content_emitted = true;
                    out.push(Ok(ModelStreamEvent::ToolCallDelta {
                        id,
                        name: Some(name),
                        arguments_delta: String::new(),
                    }));
                }
            }
            "content_block_delta" => {
                let index = data["index"].as_u64().unwrap_or(0);
                let delta = &data["delta"];
                match delta["type"].as_str().unwrap_or_default() {
                    "text_delta" => {
                        if let Some(text) = delta["text"].as_str() {
                            self.content_emitted = true;
                            out.push(Ok(ModelStreamEvent::TextDelta {
                                delta: text.to_string(),
                            }));
                        }
                    }
                    "thinking_delta" => {
                        if let Some(text) = delta["thinking"].as_str() {
                            self.content_emitted = true;
                            out.push(Ok(ModelStreamEvent::ReasoningDelta {
                                delta: text.to_string(),
                            }));
                        }
                    }
                    "signature_delta" => {
                        if let Some(signature) = delta["signature"].as_str() {
                            self.signature = Some(signature.to_string());
                        }
                    }
                    "input_json_delta" => {
                        if let (Some(partial), Some(id)) =
                            (delta["partial_json"].as_str(), self.tool_ids.get(&index))
                        {
                            out.push(Ok(ModelStreamEvent::ToolCallDelta {
                                id: id.clone(),
                                name: None,
                                arguments_delta: partial.to_string(),
                            }));
                        }
                    }
                    _ => {}
                }
            }
            "message_delta" => {
                if let Some(stop) = data["delta"]["stop_reason"].as_str() {
                    self.stop_reason = Some(map_stop_reason(stop));
                }
                if let Some(output) = data["usage"]["output_tokens"].as_u64() {
                    self.usage.output_tokens = output;
                }
            }
            "message_stop" => {
                self.completed = true;
                out.push(Ok(ModelStreamEvent::Completed {
                    stop_reason: self.stop_reason.unwrap_or(StopReason::EndTurn),
                    usage: self.usage,
                    reasoning_signature: self.signature.take(),
                }));
            }
            "error" => {
                let error_type = data["error"]["type"].as_str().unwrap_or_default();
                let message = data["error"]["message"].as_str().unwrap_or("stream error");
                out.push(Err(stream_error(error_type, message, self.content_emitted)));
            }
            _ => {}
        }
        out
    }

    /// Stream ended without `message_stop`: synthesize a completion when
    /// content exists, otherwise report a protocol failure.
    pub fn finish(&mut self) -> Vec<DecodeStep> {
        if self.completed {
            return Vec::new();
        }
        self.completed = true;
        if self.content_emitted {
            vec![Ok(ModelStreamEvent::Completed {
                stop_reason: self.stop_reason.unwrap_or(StopReason::EndTurn),
                usage: self.usage,
                reasoning_signature: self.signature.take(),
            })]
        } else {
            vec![Err(ProviderError {
                kind: ProviderErrorKind::Protocol,
                retryable: true,
                emitted_output: false,
                message: "stream ended without message_stop".to_string(),
            })]
        }
    }
}

fn map_stop_reason(reason: &str) -> StopReason {
    match reason {
        "tool_use" => StopReason::ToolUse,
        "max_tokens" => StopReason::MaxTokens,
        "stop_sequence" => StopReason::StopSequence,
        "refusal" => StopReason::Refusal,
        _ => StopReason::EndTurn,
    }
}

fn stream_error(error_type: &str, message: &str, emitted: bool) -> ProviderError {
    let (kind, retryable) = match error_type {
        "overloaded_error" => (ProviderErrorKind::Overloaded, true),
        "rate_limit_error" => (ProviderErrorKind::RateLimited, true),
        "api_error" => (ProviderErrorKind::Overloaded, true),
        "invalid_request_error" => (ProviderErrorKind::InvalidRequest, false),
        "authentication_error" => (ProviderErrorKind::Auth, false),
        _ => (ProviderErrorKind::Protocol, false),
    };
    ProviderError {
        kind,
        retryable,
        emitted_output: emitted,
        message: format!("{error_type}: {message}"),
    }
}

struct ResponseStream {
    bytes: Pin<Box<dyn Stream<Item = reqwest::Result<Bytes>> + Send>>,
    sse: SseBuffer,
    decoder: AnthropicDecoder,
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
