//! alco-grep: Alco-owned C ABI over the ripgrep search engine (`ignore` +
//! `grep-searcher` + `grep-regex`, pinned to the ripgrep 14.1.1 dependency
//! set). The host mirrors this ABI in `Alcoler.Adapters/Grep/`.

mod abi;
mod entry;
mod search;

use std::ffi::{c_char, c_void, CStr};
use std::sync::Arc;

use abi::{MatchCallback, SearchOptions, Status};

#[doc(hidden)]
pub mod test_support {
    pub use crate::search::{run, Event, EventKind, EventSink, Mode, Outcome, Request, SearchError};
}

/// Returns `(ABI_MAJOR << 16) | ABI_MINOR`; the host rejects major mismatches.
#[no_mangle]
pub extern "C" fn grep_abi_version() -> u32 {
    (abi::ABI_MAJOR << 16) | abi::ABI_MINOR
}

/// Runs one search, streaming events to `callback` until it completes, the
/// callback requests a stop, or the host raises `cancel_flag`.
///
/// # Safety
/// `options` and its strings/glob array must stay valid for the call;
/// `cancel_flag` (nullable) must stay valid and 4-byte aligned for the call;
/// `callback` must not unwind or reenter the library.
#[no_mangle]
pub unsafe extern "C-unwind" fn grep_search(
    options: *const SearchOptions,
    callback: Option<MatchCallback>,
    userdata: *mut c_void,
    cancel_flag: *mut u32,
) -> u32 {
    entry::guard(|| {
        let invalid = |message: &str| {
            entry::set_error(Status::INVALID_ARGUMENT, message);
            Status::INVALID_ARGUMENT
        };
        let Some(options) = options.as_ref() else {
            return invalid("options is null");
        };
        let Some(callback) = callback else {
            return invalid("callback is null");
        };
        let Some(pattern) = borrow_cstr(options.pattern) else {
            return invalid("pattern is null");
        };
        let Some(path) = borrow_cstr(options.path) else {
            return invalid("path is null");
        };
        let glob_count = options.glob_count as usize;
        let mut globs = Vec::with_capacity(glob_count);
        if glob_count > 0 {
            if options.globs.is_null() {
                return invalid("globs is null with a nonzero glob_count");
            }
            for index in 0..glob_count {
                let Some(glob) = borrow_cstr(*options.globs.add(index)) else {
                    return invalid("globs contains a null entry");
                };
                globs.push(glob);
            }
        }
        let mode = match options.mode {
            abi::mode::CONTENT => search::Mode::Content,
            abi::mode::FILES_WITH_MATCHES => search::Mode::Files,
            abi::mode::COUNT => search::Mode::Count,
            _ => return invalid("unknown mode"),
        };
        let path = std::path::PathBuf::from(path);
        let request = search::Request {
            pattern: pattern.as_str(),
            path: path.as_path(),
            globs: globs.as_slice(),
            mode,
            case_insensitive: options.case_insensitive == abi::TRUE,
            multiline: options.multiline == abi::TRUE,
            only_matching: options.only_matching == abi::TRUE,
            before_context: options.before_context,
            after_context: options.after_context,
        };
        let sink = Arc::new(search::CEventSink::new(callback, userdata, cancel_flag));
        match search::run(&request, sink) {
            Ok(search::Outcome::Cancelled) => {
                entry::set_error(Status::CANCELLED, "search cancelled");
                Status::CANCELLED
            }
            Ok(_) => Status::OK,
            Err(search::SearchError::InvalidPattern(message)) => {
                entry::set_error(Status::INVALID_PATTERN, message);
                Status::INVALID_PATTERN
            }
            Err(search::SearchError::InvalidArgument(message)) => {
                entry::set_error(Status::INVALID_ARGUMENT, message);
                Status::INVALID_ARGUMENT
            }
        }
    })
    .0
}

fn borrow_cstr(pointer: *const c_char) -> Option<String> {
    if pointer.is_null() {
        return None;
    }
    // Lossy conversion matches rg's handling of non-UTF-8 command input.
    Some(unsafe { CStr::from_ptr(pointer) }.to_string_lossy().into_owned())
}
