//! Raw C ABI contract types, mirrored field-by-field by the C# host
//! (`Alcoler.Adapters/Grep/AlcoGrepAbi.cs`). Every struct is `#[repr(C)]`;
//! booleans are `u32` (`TRUE = 1`), enum values are raw `u32` validated at the
//! boundary, arrays are `(pointer, count)` pairs, and strings are
//! NUL-terminated UTF-8 borrowed for the call or callback duration only.

use std::ffi::c_char;

/// ABI major version. Increment on any breaking layout/semantic change.
pub const ABI_MAJOR: u32 = 1;
/// ABI minor version. Increment on additive changes.
pub const ABI_MINOR: u32 = 0;

/// Boolean true in `u32` bool fields.
pub const TRUE: u32 = 1;

/// Status code returned by every fallible ABI entry point.
#[repr(transparent)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Status(pub u32);

impl Status {
    pub const OK: Self = Self(0);
    pub const INVALID_ARGUMENT: Self = Self(2);
    pub const PANIC: Self = Self(6);
    pub const INVALID_PATTERN: Self = Self(9);
    pub const CANCELLED: Self = Self(10);
}

/// `SearchOptions::mode` values.
pub mod mode {
    pub const CONTENT: u32 = 1;
    pub const FILES_WITH_MATCHES: u32 = 2;
    pub const COUNT: u32 = 3;
}

/// `MatchEvent::kind` values.
pub mod event_kind {
    pub const MATCH: u32 = 1;
    pub const CONTEXT: u32 = 2;
    pub const FILE_COUNT: u32 = 3;
}

/// One-shot search request. All strings and arrays are borrowed for the call.
#[repr(C)]
pub struct SearchOptions {
    /// NUL-terminated UTF-8 regex pattern (required).
    pub pattern: *const c_char,
    /// NUL-terminated UTF-8 file or directory to search (required).
    pub path: *const c_char,
    /// Array of NUL-terminated UTF-8 glob filters (may be null with count 0).
    pub globs: *const *const c_char,
    /// Number of entries in `globs`.
    pub glob_count: u32,
    /// One of the `mode::*` values.
    pub mode: u32,
    /// TRUE for case-insensitive matching.
    pub case_insensitive: u32,
    /// TRUE for multi-line matching (`rg -U --multiline-dotall`).
    pub multiline: u32,
    /// TRUE to report each submatch separately (`rg -o`).
    pub only_matching: u32,
    /// Context lines before a match (`rg -B`), content mode only.
    pub before_context: u32,
    /// Context lines after a match (`rg -A`), content mode only.
    pub after_context: u32,
}

/// One streamed search event. `path` and `text` are borrowed for the callback
/// call only and must be copied by the host if retained.
#[repr(C)]
pub struct MatchEvent {
    /// One of the `event_kind::*` values.
    pub kind: u32,
    /// NUL-terminated UTF-8 path of the file producing the event.
    pub path: *const c_char,
    /// 1-based line number, 0 when absent.
    pub line_number: u64,
    /// NUL-terminated UTF-8 line/match text (empty for FILE_COUNT).
    pub text: *const c_char,
    /// FILE_COUNT only: number of matching units in the file.
    pub count: u64,
}

/// Last-error record for `grep_last_error`.
#[repr(C)]
pub struct ErrorInfo {
    /// Matching `Status` for the failed call.
    pub status: u32,
    /// NUL-terminated message (borrowed until the next failure on this thread).
    pub message: *const c_char,
}

/// Host callback: return 0 to continue, nonzero to stop the search early.
/// Must not unwind, throw, or reenter the library.
pub type MatchCallback = extern "C" fn(event: *const MatchEvent, userdata: *mut std::ffi::c_void) -> u32;
