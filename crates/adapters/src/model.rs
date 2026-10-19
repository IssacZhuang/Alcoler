//! Model provider adapters: Anthropic Messages API, OpenAI-compatible chat
//! completions, and a scripted mock.

pub mod anthropic;
pub mod mock;
pub mod openai;
pub mod router;
pub mod sse;
