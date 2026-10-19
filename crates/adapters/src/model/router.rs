//! Provider routing: dispatches each completion request to the concrete
//! provider named by `ModelRef.provider`, so one composition root can serve
//! sessions on different providers at the same time.
//!
//! Routes are mutable at runtime: custom providers registered after startup
//! (from persisted config) join the same table without restarting.

use std::collections::HashMap;
use std::sync::Arc;

use alcoler_contracts::ids::ProviderId;
use alcoler_contracts::model::{
    CompletionRequest, ModelProvider, ModelStreamEvent, ProviderError, ProviderErrorKind,
};
use alcoler_contracts::{BoxFuture, BoxStream};
use tokio::sync::RwLock;

pub struct RoutingProvider {
    default: ProviderId,
    routes: RwLock<HashMap<String, Arc<dyn ModelProvider>>>,
}

impl RoutingProvider {
    pub fn new(default: Arc<dyn ModelProvider>, extra: Vec<Arc<dyn ModelProvider>>) -> Self {
        let mut routes = HashMap::new();
        routes.insert(default.provider_id().0.clone(), default.clone());
        for provider in extra {
            routes.insert(provider.provider_id().0.clone(), provider);
        }
        Self {
            default: default.provider_id().clone(),
            routes: RwLock::new(routes),
        }
    }

    /// Add or replace the route for `provider.provider_id()`.
    pub async fn register(&self, provider: Arc<dyn ModelProvider>) {
        self.routes
            .write()
            .await
            .insert(provider.provider_id().0.clone(), provider);
    }

    pub async fn unregister(&self, id: &ProviderId) {
        self.routes.write().await.remove(id.0.as_str());
    }

    pub async fn providers(&self) -> Vec<ProviderId> {
        self.routes
            .read()
            .await
            .keys()
            .map(|k| ProviderId(k.clone()))
            .collect()
    }
}

impl ModelProvider for RoutingProvider {
    fn provider_id(&self) -> &ProviderId {
        &self.default
    }

    fn stream(
        &self,
        req: CompletionRequest,
    ) -> BoxFuture<
        '_,
        Result<BoxStream<'static, Result<ModelStreamEvent, ProviderError>>, ProviderError>,
    > {
        Box::pin(async move {
            let route = self.routes.read().await.get(&req.model.provider).cloned();
            match route {
                Some(provider) => provider.stream(req).await,
                None => Err(ProviderError {
                    kind: ProviderErrorKind::InvalidRequest,
                    retryable: false,
                    emitted_output: false,
                    message: format!("no provider registered for '{}'", req.model.provider),
                }),
            }
        })
    }
}
