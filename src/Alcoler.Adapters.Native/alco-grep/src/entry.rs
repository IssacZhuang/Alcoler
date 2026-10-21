//! Entry-point guard machinery: every exported ABI function runs its body
//! through [`guard`], which wraps it in `catch_unwind` so a Rust panic becomes
//! `Status::PANIC` plus a thread-local message instead of an abort at the FFI
//! boundary (an abort would kill the host process before the host runtime
//! could react). The crate MUST be built with `panic = "unwind"`.
//!
//! Failure protocol: a body returning a failure must record its own fresh
//! diagnostic through [`set_error`] or [`set_error_from`]. Success never
//! touches diagnostic storage. `grep_last_error` exposes the latest failure on
//! this thread, not the outcome of a later successful call.

use crate::abi::{ErrorInfo, Status};
use std::cell::RefCell;
use std::ffi::CString;
use std::sync::Once;

thread_local! {
    static LAST_ERROR: RefCell<Option<(Status, CString)>> = const { RefCell::new(None) };
}

static HOOK_INSTALLED: Once = Once::new();

/// Installs a silent panic hook so contained panics never spam host stderr.
fn initialize() {
    HOOK_INSTALLED.call_once(|| {
        std::panic::set_hook(Box::new(|_info| {}));
    });
}

/// Records a failure with a human-readable message on the current thread.
#[cold]
pub(crate) fn set_error(status: Status, message: impl Into<String>) {
    let message = message.into();
    LAST_ERROR.with(|slot| {
        *slot.borrow_mut() = Some((
            status,
            // Replace interior NULs so the C string stays faithful.
            CString::new(message.replace('\0', "\\0"))
                .unwrap_or_else(|_| CString::new("invalid error message").unwrap()),
        ));
    });
}

/// ABI export: copies the latest thread-local failure into `out`.
/// The message is borrowed until the next failure on this thread.
///
/// # Safety
/// `out` must be a valid pointer to an `ErrorInfo`.
#[no_mangle]
pub unsafe extern "C" fn grep_last_error(out: *mut ErrorInfo) {
    if out.is_null() {
        return;
    }
    initialize();
    LAST_ERROR.with(|slot| match slot.borrow().as_ref() {
        Some((status, message)) => {
            (*out).status = status.0;
            (*out).message = message.as_ptr();
        }
        None => {
            (*out).status = Status::OK.0;
            (*out).message = std::ptr::null();
        }
    });
}

/// Runs an ABI entry body under panic protection. See the module docs for the
/// failure protocol.
pub(crate) fn guard(body: impl FnOnce() -> Status) -> Status {
    initialize();
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(body)) {
        Ok(status) => status,
        Err(payload) => {
            let message = payload
                .downcast_ref::<String>()
                .cloned()
                .or_else(|| payload.downcast_ref::<&str>().map(|s| s.to_string()))
                .unwrap_or_else(|| "panic with non-string payload".to_string());
            set_error(Status::PANIC, message);
            Status::PANIC
        }
    }
}
