//! Provider routing: dispatches each completion request to the concrete
//! provider named by `ModelRef.provider`, so one composition root can serve
//! sessions on different providers at the same time.

use std::collections::HashMap;
use std::sync::Arc;

use alcoler_contracts::ids::ProviderId;
use alcoler_contracts::model::{
    CompletionRequest, ModelProvider, ModelStreamEvent, ProviderError, ProviderErrorKind,
};
use alcoler_contracts::{BoxFuture, BoxStream};

pub struct RoutingProvider {
    default: ProviderId,
    routes: HashMap<String, Arc<dyn ModelProvider>>,
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
            routes,
        }
    }

    pub fn providers(&self) -> Vec<ProviderId> {
        self.routes.keys().map(|k| ProviderId(k.clone())).collect()
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
        let route = self.routes.get(&req.model.provider).cloned();
        Box::pin(async move {
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
