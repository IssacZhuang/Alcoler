//! Search engine over the ripgrep crates: `ignore` walks with gitignore rules
//! (mirrors `rg --hidden` with default ignore rules and VCS-directory
//! exclusions), `grep-searcher`/`grep-regex` match. Events stream through an
//! [`EventSink`]; returning false or raising the cancel flag stops the walk.

use std::ffi::{CStr, CString};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::Arc;

use grep_matcher::Matcher;
use grep_regex::RegexMatcherBuilder;
use grep_searcher::{BinaryDetection, Searcher, SearcherBuilder, Sink, SinkContext, SinkMatch};
use ignore::overrides::OverrideBuilder;
use ignore::{DirEntry, WalkBuilder, WalkState};

#[derive(Debug)]
pub enum Mode {
    Content,
    Files,
    Count,
}

pub struct Request<'a> {
    pub pattern: &'a str,
    pub path: &'a Path,
    pub globs: &'a [String],
    pub mode: Mode,
    pub case_insensitive: bool,
    pub multiline: bool,
    pub only_matching: bool,
    pub before_context: u32,
    pub after_context: u32,
}

#[derive(Debug)]
pub enum EventKind {
    Match,
    Context,
    FileCount,
}

pub struct Event<'a> {
    pub kind: EventKind,
    pub path: &'a CStr,
    pub line_number: u64,
    pub text: String,
    pub count: u64,
}

#[allow(private_bounds)]
pub trait EventSink: Send + Sync {
    /// Returns false to stop the whole search.
    fn emit(&self, event: Event<'_>) -> bool;
    fn cancelled(&self) -> bool;
}

#[derive(Debug)]
pub enum Outcome {
    Completed,
    Stopped,
    Cancelled,
}

#[derive(Debug)]
pub enum SearchError {
    InvalidPattern(String),
    InvalidArgument(String),
}

// Version-control directories are never searched (parity with the managed walker).
const VCS_DIRECTORIES: [&str; 6] = [".git", ".svn", ".hg", ".bzr", ".jj", ".sl"];

pub fn run(request: &Request<'_>, sink: Arc<dyn EventSink>) -> Result<Outcome, SearchError> {
    let matcher = RegexMatcherBuilder::new()
        .case_insensitive(request.case_insensitive)
        .multi_line(request.multiline)
        .dot_matches_new_line(request.multiline)
        .build(request.pattern)
        .map_err(|error| SearchError::InvalidPattern(error.to_string()))?;

    let is_content = matches!(request.mode, Mode::Content);
    if request.path.is_file() {
        let stopped = search_file(
            &matcher,
            request.path,
            is_content,
            request.only_matching,
            request.multiline,
            request.before_context,
            request.after_context,
            &sink,
        );
        return Ok(finish(&sink, stopped));
    }

    let mut overrides = OverrideBuilder::new(request.path);
    for directory in VCS_DIRECTORIES {
        for glob in [format!("!{directory}"), format!("!**/{directory}/**")] {
            overrides
                .add(&glob)
                .map_err(|error| SearchError::InvalidArgument(error.to_string()))?;
        }
    }
    for glob in request.globs {
        overrides
            .add(glob)
            .map_err(|error| SearchError::InvalidArgument(error.to_string()))?;
        // rg matches separator-free globs against any depth, not only the root.
        if !glob.contains('/') {
            overrides
                .add(&format!("**/{glob}"))
                .map_err(|error| SearchError::InvalidArgument(error.to_string()))?;
        }
    }
    let overrides = overrides
        .build()
        .map_err(|error| SearchError::InvalidArgument(error.to_string()))?;

    let stopped = Arc::new(AtomicBool::new(false));
    WalkBuilder::new(request.path)
        .hidden(false)
        .require_git(false)
        .overrides(overrides)
        .build_parallel()
        .run(|| {
            let matcher = matcher.clone();
            let sink = Arc::clone(&sink);
            let stopped = Arc::clone(&stopped);
            let mut searcher = search_builder(is_content, request.multiline, request.before_context, request.after_context).build();
            Box::new(move |entry: Result<DirEntry, ignore::Error>| {
                if sink.cancelled() || stopped.load(Ordering::Relaxed) {
                    return WalkState::Quit;
                }
                let entry = match entry {
                    Ok(entry) => entry,
                    Err(_) => return WalkState::Continue,
                };
                if !entry.file_type().is_some_and(|file_type| file_type.is_file()) {
                    return WalkState::Continue;
                }
                let Some(path_cstr) = cstring_from_path(entry.path()) else {
                    return WalkState::Continue;
                };
                let file_stopped = search_file_with(
                    &mut searcher,
                    &matcher,
                    entry.path(),
                    &path_cstr,
                    is_content,
                    request.only_matching,
                    request.multiline,
                    &sink,
                );
                if file_stopped {
                    stopped.store(true, Ordering::Relaxed);
                    return WalkState::Quit;
                }
                WalkState::Continue
            })
        });

    Ok(finish(&sink, stopped.load(Ordering::Relaxed)))
}

fn finish(sink: &Arc<dyn EventSink>, stopped: bool) -> Outcome {
    if sink.cancelled() {
        Outcome::Cancelled
    } else if stopped {
        Outcome::Stopped
    } else {
        Outcome::Completed
    }
}

fn search_builder(is_content: bool, multiline: bool, before_context: u32, after_context: u32) -> SearcherBuilder {
    let mut builder = SearcherBuilder::new();
    builder.line_number(is_content);
    builder.multi_line(multiline);
    builder.bom_sniffing(true);
    builder.binary_detection(BinaryDetection::quit(0));
    if is_content {
        builder.before_context(before_context as usize);
        builder.after_context(after_context as usize);
    }
    builder
}

#[allow(clippy::too_many_arguments)]
fn search_file(
    matcher: &grep_regex::RegexMatcher,
    path: &Path,
    is_content: bool,
    only_matching: bool,
    multiline: bool,
    before_context: u32,
    after_context: u32,
    sink: &Arc<dyn EventSink>,
) -> bool {
    let Some(path_cstr) = cstring_from_path(path) else {
        return false;
    };
    let mut searcher = search_builder(is_content, multiline, before_context, after_context).build();
    search_file_with(
        &mut searcher,
        matcher,
        path,
        &path_cstr,
        is_content,
        only_matching,
        multiline,
        sink,
    )
}

#[allow(clippy::too_many_arguments)]
fn search_file_with(
    searcher: &mut Searcher,
    matcher: &grep_regex::RegexMatcher,
    path: &Path,
    path_cstr: &CStr,
    is_content: bool,
    only_matching: bool,
    multiline: bool,
    sink: &Arc<dyn EventSink>,
) -> bool {
    let mut count = 0u64;
    let mut stopped = false;
    let mut file_sink = FileSink {
        sink: sink.as_ref(),
        path: path_cstr,
        matcher,
        is_content,
        only_matching,
        multiline,
        count: &mut count,
        stopped: &mut stopped,
    };
    // Unreadable files are skipped, like the managed engine.
    let _ = searcher.search_path(matcher, path, &mut file_sink);
    if !is_content && count > 0 && !sink.emit(Event {
        kind: EventKind::FileCount,
        path: path_cstr,
        line_number: 0,
        text: String::new(),
        count,
    }) {
        return true;
    }
    stopped
}

struct FileSink<'a> {
    sink: &'a dyn EventSink,
    path: &'a CStr,
    matcher: &'a grep_regex::RegexMatcher,
    is_content: bool,
    only_matching: bool,
    multiline: bool,
    count: &'a mut u64,
    stopped: &'a mut bool,
}

impl FileSink<'_> {
    fn emit_line(&mut self, kind: EventKind, line_number: u64, text: &str) -> bool {
        let keep_going = self.sink.emit(Event {
            kind,
            path: self.path,
            line_number,
            text: text.replace('\0', "\u{FFFD}"),
            count: 0,
        });
        if !keep_going {
            *self.stopped = true;
        }
        keep_going
    }
}

impl Sink for FileSink<'_> {
    type Error = std::io::Error;

    fn matched(&mut self, _searcher: &Searcher, mat: &SinkMatch<'_>) -> Result<bool, Self::Error> {
        *self.count += 1;
        if self.is_content {
            let line_number = mat.line_number().unwrap_or(0);
            if self.only_matching {
                // The searcher reports matching lines; individual submatches come from
                // the matcher, like rg's own printers.
                let line = strip_line_terminator(mat.bytes());
                let mut offset = 0usize;
                loop {
                    let Some(found) = self.matcher.find_at(line, offset).ok().flatten() else {
                        break;
                    };
                    let (start, end) = (found.start(), found.end());
                    if end == start {
                        if !self.emit_line(EventKind::Match, line_number, "") {
                            return Ok(false);
                        }
                        if offset >= line.len() {
                            break;
                        }
                        offset += 1;
                        continue;
                    }
                    // A multi-line submatch is reported one part per line, like the managed engine.
                    let piece = String::from_utf8_lossy(&line[start..end]);
                    let mut sub_line = 0u64;
                    for part in piece.split('\n') {
                        let part = part.strip_suffix('\r').unwrap_or(part);
                        if !part.is_empty() && !self.emit_line(EventKind::Match, line_number + sub_line, part) {
                            return Ok(false);
                        }
                        sub_line += 1;
                    }
                    offset = end;
                }
            } else {
                let line = String::from_utf8_lossy(strip_line_terminator(mat.bytes()));
                let text = if self.multiline { first_line(&line) } else { line.as_ref() };
                if !self.emit_line(EventKind::Match, line_number, text) {
                    return Ok(false);
                }
            }
        }
        if self.sink.cancelled() {
            return Ok(false);
        }
        Ok(true)
    }

    fn context(&mut self, _searcher: &Searcher, context: &SinkContext<'_>) -> Result<bool, Self::Error> {
        if self.is_content {
            let line = String::from_utf8_lossy(strip_line_terminator(context.bytes()));
            if !self.emit_line(EventKind::Context, context.line_number().unwrap_or(0), &line) {
                return Ok(false);
            }
        }
        Ok(true)
    }
}

// Line boundaries are LF/CRLF only; a lone CR stays in the line, and a
// terminator at end-of-stream is not part of the line.
fn strip_line_terminator(mut bytes: &[u8]) -> &[u8] {
    if bytes.ends_with(b"\n") {
        bytes = &bytes[..bytes.len() - 1];
    }
    if bytes.ends_with(b"\r") {
        bytes = &bytes[..bytes.len() - 1];
    }
    bytes
}

fn first_line(value: &str) -> &str {
    let first = value.split('\n').next().unwrap_or(value);
    first.strip_suffix('\r').unwrap_or(first)
}

fn cstring_from_path(path: &Path) -> Option<CString> {
    CString::new(path.to_string_lossy().into_owned()).ok()
}

/// FFI sink: forwards events to the host callback and reads the host-owned
/// cancel flag. The callback must not unwind.
pub(crate) struct CEventSink {
    callback: crate::abi::MatchCallback,
    userdata: *mut std::ffi::c_void,
    cancel: *mut u32,
}

unsafe impl Send for CEventSink {}
unsafe impl Sync for CEventSink {}

impl CEventSink {
    pub fn new(
        callback: crate::abi::MatchCallback,
        userdata: *mut std::ffi::c_void,
        cancel: *mut u32,
    ) -> Self {
        Self { callback, userdata, cancel }
    }
}

impl EventSink for CEventSink {
    fn emit(&self, event: Event<'_>) -> bool {
        let kind = match event.kind {
            EventKind::Match => crate::abi::event_kind::MATCH,
            EventKind::Context => crate::abi::event_kind::CONTEXT,
            EventKind::FileCount => crate::abi::event_kind::FILE_COUNT,
        };
        let Ok(text) = CString::new(event.text) else {
            return true;
        };
        let ffi_event = crate::abi::MatchEvent {
            kind,
            path: event.path.as_ptr(),
            line_number: event.line_number,
            text: text.as_ptr(),
            count: event.count,
        };
        (self.callback)(&ffi_event, self.userdata) == 0
    }

    fn cancelled(&self) -> bool {
        if self.cancel.is_null() {
            return false;
        }
        unsafe { AtomicU32::from_ptr(self.cancel).load(Ordering::Relaxed) != 0 }
    }
}
