//! Composition root: env config, port assembly, HTTP serve, graceful exit.

#![forbid(unsafe_code)]

mod http;
mod projection;
mod wire;

use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::Arc;

use alcoler_adapters::model::anthropic::AnthropicProvider;
use alcoler_adapters::model::mock::MockProvider;
use alcoler_adapters::model::openai::OpenAiProvider;
use alcoler_adapters::model::router::RoutingProvider;
use alcoler_adapters::runtime::{AllowGate, HeuristicTokenCounter, SystemClock, TimeIdGenerator};
use alcoler_adapters::storage::{FileBlobStore, FileSessionStore};
use alcoler_adapters::tooling::builtin_tools;
use alcoler_contracts::model::{ModelProvider, ModelRef};
use alcoler_contracts::storage::{SessionProfile, SessionStore};
use alcoler_contracts::wire::Command;
use alcoler_kernel::{Kernel, KernelConfig, KernelDeps};
use tokio::sync::{broadcast, mpsc};

use crate::http::AppState;
use crate::projection::Projection;
use crate::wire::{BroadcastSink, ChannelSource};

fn env_or(key: &str, default: &str) -> String {
    std::env::var(key).unwrap_or_else(|_| default.to_owned())
}

fn data_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("ALCOLER_DATA_DIR") {
        return PathBuf::from(dir);
    }
    let home = std::env::var("USERPROFILE")
        .or_else(|_| std::env::var("HOME"))
        .unwrap_or_else(|_| ".".into());
    PathBuf::from(home).join(".alcoler")
}

fn env_first(keys: &[&str]) -> Option<String> {
    keys.iter().find_map(|k| std::env::var(k).ok())
}

const ANTHROPIC_DEFAULT_BASE: &str = "https://api.anthropic.com";
const OPENAI_DEFAULT_BASE: &str = "https://api.openai.com/v1";

fn default_model_name(provider: &str) -> String {
    match provider {
        "anthropic" => "claude-sonnet-4-5".to_owned(),
        "openai" => "gpt-5.1".to_owned(),
        _ => "mock-1".to_owned(),
    }
}

#[tokio::main]
async fn main() -> ExitCode {
    let default_kind = match env_or("ALCOLER_PROVIDER", "mock")
        .to_ascii_lowercase()
        .as_str()
    {
        "anthropic" => "anthropic".to_owned(),
        "openai" => "openai".to_owned(),
        _ => "mock".to_owned(),
    };
    let default_model =
        std::env::var("ALCOLER_MODEL").unwrap_or_else(|_| default_model_name(&default_kind));

    let anthropic_base = env_first(&["ALCOLER_ANTHROPIC_BASE_URL", "ALCOLER_BASE_URL"])
        .unwrap_or_else(|| ANTHROPIC_DEFAULT_BASE.to_owned());
    let anthropic_key = env_first(&["ANTHROPIC_API_KEY", "ALCOLER_API_KEY"]).unwrap_or_default();
    let openai_base = env_first(&["ALCOLER_OPENAI_BASE_URL", "ALCOLER_BASE_URL"])
        .unwrap_or_else(|| OPENAI_DEFAULT_BASE.to_owned());
    let openai_key = env_first(&["OPENAI_API_KEY", "ALCOLER_API_KEY"]).unwrap_or_default();

    // All providers stay registered: each session routes by its own
    // `ModelRef.provider`. Availability below only drives the UI hint.
    let mock = Arc::new(MockProvider::default());
    let anthropic = Arc::new(AnthropicProvider::new(
        anthropic_base.clone(),
        anthropic_key.clone(),
    ));
    let openai = Arc::new(OpenAiProvider::new(openai_base.clone(), openai_key.clone()));
    let (default_provider, extras): (Arc<dyn ModelProvider>, Vec<Arc<dyn ModelProvider>>) =
        match default_kind.as_str() {
            "anthropic" => (anthropic.clone(), vec![mock.clone(), openai.clone()]),
            "openai" => (openai.clone(), vec![mock.clone(), anthropic.clone()]),
            _ => (mock.clone(), vec![anthropic.clone(), openai.clone()]),
        };
    let provider: Arc<dyn ModelProvider> = Arc::new(RoutingProvider::new(default_provider, extras));

    let available =
        |base: &str, key: &str, default_base: &str| !key.is_empty() || base != default_base;
    let mut entries = vec![
        crate::http::ModelEntry {
            provider: "mock".into(),
            model: default_model_name("mock"),
            available: true,
        },
        crate::http::ModelEntry {
            provider: "anthropic".into(),
            model: default_model_name("anthropic"),
            available: available(&anthropic_base, &anthropic_key, ANTHROPIC_DEFAULT_BASE),
        },
        crate::http::ModelEntry {
            provider: "openai".into(),
            model: default_model_name("openai"),
            available: available(&openai_base, &openai_key, OPENAI_DEFAULT_BASE),
        },
    ];
    entries
        .iter_mut()
        .find(|entry| entry.provider == default_kind)
        .expect("default kind has an entry")
        .model = default_model.clone();
    let models = crate::http::ModelCatalog {
        default: crate::http::ModelEntry {
            provider: default_kind.clone(),
            model: default_model.clone(),
            available: true,
        },
        entries,
    };
    let model = ModelRef {
        provider: default_kind.clone(),
        model: default_model.clone(),
    };

    let data_dir = data_dir();
    let store = Arc::new(FileSessionStore::new(data_dir.clone()));
    let blobs = Arc::new(FileBlobStore::new(data_dir.clone()));
    if let Err(e) = store.open().await {
        eprintln!(
            "alcoler-server: cannot open store at {}: {}",
            data_dir.display(),
            e.message
        );
        return ExitCode::FAILURE;
    }

    let profile = SessionProfile {
        name: "standard".into(),
        model: model.clone(),
        context_budget_tokens: 64_000,
        max_concurrent_tools: 4,
        event_buffer_bound: 1024,
        permission_timeout_ms: 60_000,
    };

    let ids = Arc::new(TimeIdGenerator::new());
    let kernel = Kernel::new(
        KernelDeps {
            store: store.clone(),
            blobs,
            provider,
            tools: builtin_tools(),
            gate: Arc::new(AllowGate),
            counter: Arc::new(HeuristicTokenCounter),
            clock: Arc::new(SystemClock),
            ids: ids.clone(),
        },
        KernelConfig {
            system_prompt: Some(
                "You are Alcoler, a minimal coding agent. Use the provided tools to inspect \
                 and modify the workspace; keep answers brief."
                    .into(),
            ),
            max_output_tokens: Some(8192),
            temperature: None,
            text_flush_bytes: 128,
            ..KernelConfig::default()
        },
    );

    let (cmd_tx, cmd_rx) = mpsc::channel::<Command>(64);
    let (events_tx, _) = broadcast::channel::<Arc<String>>(1024);
    let kernel_task = tokio::spawn(kernel.run(
        Box::new(ChannelSource::new(cmd_rx)),
        Arc::new(BroadcastSink::new(events_tx.clone())),
    ));

    let projection = Projection::new();
    projection.clone().spawn(events_tx.clone());

    let state = AppState {
        cmd_tx: cmd_tx.clone(),
        events: events_tx,
        projection,
        store,
        ids,
        profile,
        models,
    };

    let listen = env_or("ALCOLER_LISTEN", "127.0.0.1:8787");
    let listener = match tokio::net::TcpListener::bind(&listen).await {
        Ok(listener) => listener,
        Err(e) => {
            eprintln!("alcoler-server: cannot bind {listen}: {e}");
            return ExitCode::FAILURE;
        }
    };
    eprintln!(
        "alcoler-server listening on http://{listen} (default model: {default_kind}/{default_model})"
    );

    let shutdown_tx = cmd_tx.clone();
    let serve = axum::serve(listener, http::router(state)).with_graceful_shutdown(async move {
        let _ = tokio::signal::ctrl_c().await;
    });
    if let Err(e) = serve.await {
        eprintln!("alcoler-server: {e}");
        return ExitCode::FAILURE;
    }
    let _ = shutdown_tx.send(Command::Shutdown).await;
    let _ = kernel_task.await;
    ExitCode::SUCCESS
}
