//! Custom provider configs: user-defined endpoints persisted at
//! `<data_dir>/providers.json` and registered into the routing provider at
//! startup and on every change. API keys are stored in plaintext: the file
//! lives next to the session store and this is a local harness.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use alcoler_adapters::model::anthropic::AnthropicProvider;
use alcoler_adapters::model::openai::OpenAiProvider;
use alcoler_adapters::model::router::RoutingProvider;
use alcoler_contracts::ids::ProviderId;
use alcoler_contracts::model::ModelProvider;
use serde::{Deserialize, Serialize};
use tokio::sync::Mutex;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ApiFormat {
    AnthropicMessages,
    OpenaiChatCompletions,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProviderConfig {
    pub id: String,
    pub name: String,
    pub api_format: ApiFormat,
    pub base_url: String,
    pub api_key: String,
    pub models: Vec<String>,
}

fn build_provider(config: &ProviderConfig) -> Arc<dyn ModelProvider> {
    let id = ProviderId(config.id.clone());
    match config.api_format {
        ApiFormat::AnthropicMessages => Arc::new(AnthropicProvider::with_id(
            id,
            config.base_url.clone(),
            config.api_key.clone(),
        )),
        ApiFormat::OpenaiChatCompletions => Arc::new(OpenAiProvider::with_id(
            id,
            config.base_url.clone(),
            config.api_key.clone(),
        )),
    }
}

fn validate(config: &ProviderConfig) -> Result<(), String> {
    if config.id.trim().is_empty() {
        return Err("provider id must not be empty".into());
    }
    if config.name.trim().is_empty() {
        return Err("provider name must not be empty".into());
    }
    let base = config.base_url.trim();
    if !base.starts_with("http://") && !base.starts_with("https://") {
        return Err("base_url must be an http(s) URL".into());
    }
    let mut seen = std::collections::HashSet::new();
    for model in &config.models {
        if model.trim().is_empty() {
            return Err("model names must not be empty".into());
        }
        if !seen.insert(model.trim()) {
            return Err(format!("duplicate model '{model}'"));
        }
    }
    Ok(())
}

pub struct ProviderRegistry {
    path: PathBuf,
    router: Arc<RoutingProvider>,
    configs: Mutex<Vec<ProviderConfig>>,
}

impl ProviderRegistry {
    /// Load persisted configs and register their routes. A corrupted file is
    /// a hard error: silently dropping configs would orphan every session
    /// pinned to them.
    pub async fn open(path: PathBuf, router: Arc<RoutingProvider>) -> Result<Self, String> {
        let configs = load_configs(&path)?;
        for config in &configs {
            router.register(build_provider(config)).await;
        }
        Ok(Self {
            path,
            router,
            configs: Mutex::new(configs),
        })
    }

    pub async fn list(&self) -> Vec<ProviderConfig> {
        self.configs.lock().await.clone()
    }

    pub async fn get(&self, id: &str) -> Option<ProviderConfig> {
        self.configs
            .lock()
            .await
            .iter()
            .find(|config| config.id == id)
            .cloned()
    }

    /// Add or replace a config by id. Persists first so a failed write leaves
    /// memory and routes untouched.
    pub async fn upsert(&self, config: ProviderConfig) -> Result<(), String> {
        validate(&config)?;
        let mut configs = self.configs.lock().await;
        let mut next = configs.clone();
        match next.iter_mut().find(|c| c.id == config.id) {
            Some(slot) => *slot = config.clone(),
            None => next.push(config.clone()),
        }
        persist(&self.path, &next)?;
        *configs = next;
        drop(configs);
        self.router.register(build_provider(&config)).await;
        Ok(())
    }

    pub async fn remove(&self, id: &str) -> bool {
        let mut configs = self.configs.lock().await;
        if !configs.iter().any(|c| c.id == id) {
            return false;
        }
        let next: Vec<ProviderConfig> = configs.iter().filter(|c| c.id != id).cloned().collect();
        if persist(&self.path, &next).is_err() {
            return false;
        }
        configs.retain(|c| c.id != id);
        drop(configs);
        self.router.unregister(&ProviderId(id.to_owned())).await;
        true
    }
}

fn load_configs(path: &Path) -> Result<Vec<ProviderConfig>, String> {
    let raw = match std::fs::read_to_string(path) {
        Ok(raw) => raw,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(e) => return Err(format!("cannot read {}: {e}", path.display())),
    };
    serde_json::from_str(&raw).map_err(|e| format!("cannot parse {}: {e}", path.display()))
}

fn persist(path: &Path, configs: &[ProviderConfig]) -> Result<(), String> {
    let body = serde_json::to_string_pretty(configs)
        .map_err(|e| format!("cannot serialize providers: {e}"))?;
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, body).map_err(|e| format!("cannot write {}: {e}", tmp.display()))?;
    if path.exists() && std::fs::remove_file(path).is_err() {
        return Err(format!("cannot replace {}", path.display()));
    }
    std::fs::rename(&tmp, path).map_err(|e| format!("cannot rename {}: {e}", path.display()))
}
