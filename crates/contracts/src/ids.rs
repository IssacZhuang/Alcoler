//! Identifier types for sessions, turns, and calls.
//!
//! All are transparent newtypes over scalars: they serialize directly to the
//! underlying string/integer, matching the wire JSON shapes (`"session":"s1"`,
//! `"turn":1`) locked by the regression tests.

use serde::{Deserialize, Serialize};

macro_rules! string_id {
    ($(#[$doc:meta])* $name:ident) => {
        $(#[$doc])*
        #[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
        #[serde(transparent)]
        pub struct $name(pub String);
    };
}

string_id!(
    /// Session identifier.
    SessionId
);
string_id!(
    /// User command identifier: the basis for command dedup after crash
    /// recovery.
    CommandId
);
string_id!(
    /// Identifier of a single tool call, assigned by the model side (provider
    /// tool-call IDs are preserved verbatim).
    ToolCallId
);
string_id!(
    /// Model provider identifier (e.g. `"anthropic"`).
    ProviderId
);

/// Turn number: monotonically increasing from 1 within a session; both
/// persistence and cancellation are turn-granular.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(transparent)]
pub struct TurnId(pub u64);
