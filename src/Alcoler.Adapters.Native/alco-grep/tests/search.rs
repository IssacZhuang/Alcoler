use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use alco_grep::test_support::{run, Event, EventKind, EventSink, Mode, Outcome, Request, SearchError};

#[derive(Clone, PartialEq, Eq, Debug)]
struct RecordedEvent {
    kind: EventKindKind,
    path: String,
    line_number: u64,
    text: String,
    count: u64,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum EventKindKind {
    Match,
    Context,
    FileCount,
}

struct RecordingSink {
    events: Mutex<Vec<RecordedEvent>>,
    cancel_after: AtomicUsize,
}

impl RecordingSink {
    fn new() -> Self {
        Self { events: Mutex::new(Vec::new()), cancel_after: AtomicUsize::new(usize::MAX) }
    }

    fn cancel_after(&self, events: usize) {
        self.cancel_after.store(events, Ordering::Relaxed);
    }

    fn events(&self) -> Vec<RecordedEvent> {
        self.events.lock().unwrap().clone()
    }

    fn texts(&self) -> Vec<String> {
        self.events().into_iter().map(|event| event.text).collect()
    }
}

impl EventSink for RecordingSink {
    fn emit(&self, event: Event<'_>) -> bool {
        let kind = match event.kind {
            EventKind::Match => EventKindKind::Match,
            EventKind::Context => EventKindKind::Context,
            EventKind::FileCount => EventKindKind::FileCount,
        };
        let mut events = self.events.lock().unwrap();
        events.push(RecordedEvent {
            kind,
            path: event.path.to_string_lossy().into_owned(),
            line_number: event.line_number,
            text: event.text,
            count: event.count,
        });
        events.len() < self.cancel_after.load(Ordering::Relaxed)
    }

    fn cancelled(&self) -> bool {
        false
    }
}

fn request<'a>(root: &'a Path, pattern: &'a str) -> Request<'a> {
    Request {
        pattern,
        path: root,
        globs: &[],
        mode: Mode::Content,
        case_insensitive: false,
        multiline: false,
        only_matching: false,
        before_context: 0,
        after_context: 0,
    }
}

fn write(root: &Path, name: &str, contents: &str) {
    let path = root.join(name);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(path, contents).unwrap();
}

#[test]
fn content_mode_reports_match_and_context_events_in_order() {
    let root = tempfile::tempdir().unwrap();
    write(root.path(), "ctx.txt", "a\nb\nc\nd\ne\n");
    let mut req = request(root.path(), "c");
    req.before_context = 1;
    req.after_context = 1;

    let sink = Arc::new(RecordingSink::new());
    let outcome = run(&req, sink.clone()).unwrap();

    assert!(matches!(outcome, Outcome::Completed));
    let events = sink.events();
    assert_eq!(events.len(), 3);
    assert_eq!(events[0], RecordedEvent { kind: EventKindKind::Context, path: events[0].path.clone(), line_number: 2, text: "b".into(), count: 0 });
    assert_eq!(events[1].kind, EventKindKind::Match);
    assert_eq!((events[1].line_number, events[1].text.as_str()), (3, "c"));
    assert_eq!((events[2].line_number, events[2].text.as_str()), (4, "d"));
    assert!(events.iter().all(|event| event.path.ends_with("ctx.txt")));
}

#[test]
fn only_matching_reports_each_submatch() {
    let root = tempfile::tempdir().unwrap();
    write(root.path(), "o.txt", "foo bar baz qux\n");
    let mut req = request(root.path(), r"b\w+");
    req.only_matching = true;

    let sink = Arc::new(RecordingSink::new());
    run(&req, sink.clone()).unwrap();

    let events = sink.events();
    assert_eq!(sink.texts(), vec!["bar".to_string(), "baz".to_string()]);
    assert!(events.iter().all(|event| event.line_number == 1 && event.kind == EventKindKind::Match));
}

#[test]
fn multiline_reports_first_line_and_counts_matches() {
    let root = tempfile::tempdir().unwrap();
    write(root.path(), "m.txt", "start\nmiddle\nend\n");
    let mut req = request(root.path(), "start.*end");
    req.multiline = true;

    let content = Arc::new(RecordingSink::new());
    run(&req, content.clone()).unwrap();
    let events = content.events();
    assert_eq!(events.len(), 1);
    assert_eq!((events[0].line_number, events[0].text.as_str()), (1, "start"));

    let mut count_req = request(root.path(), "start.*end");
    count_req.multiline = true;
    count_req.mode = Mode::Count;
    let counts = Arc::new(RecordingSink::new());
    run(&count_req, counts.clone()).unwrap();
    let events = counts.events();
    assert_eq!(events.len(), 1);
    assert_eq!((events[0].kind, events[0].count), (EventKindKind::FileCount, 1));
}

#[test]
fn files_and_count_modes_emit_one_file_count_per_matched_file() {
    let root = tempfile::tempdir().unwrap();
    write(root.path(), "a.txt", "hit\nhit\nmiss\n");
    write(root.path(), "b.txt", "hit\n");
    write(root.path(), "c.txt", "miss\n");

    for mode in [Mode::Files, Mode::Count] {
        let mut req = request(root.path(), "hit");
        req.mode = mode;
        let sink = Arc::new(RecordingSink::new());
        run(&req, sink.clone()).unwrap();
        let mut events = sink.events();
        events.sort_by(|left, right| left.path.cmp(&right.path));
        assert_eq!(events.len(), 2);
        assert_eq!(events[0].count, 2);
        assert_eq!(events[1].count, 1);
        assert!(events[0].path.ends_with("a.txt") && events[1].path.ends_with("b.txt"));
    }
}

#[test]
fn gitignore_rules_are_respected() {
    let root = tempfile::tempdir().unwrap();
    write(root.path(), ".gitignore", "ignored.txt\n");
    write(root.path(), "ignored.txt", "target\n");
    write(root.path(), "keep.txt", "target\n");

    let sink = Arc::new(RecordingSink::new());
    let mut req = request(root.path(), "target");
    req.mode = Mode::Files;
    run(&req, sink.clone()).unwrap();

    let events = sink.events();
    assert_eq!(events.len(), 1);
    assert!(events[0].path.ends_with("keep.txt"));
}

#[test]
fn hidden_files_are_searched_and_vcs_directories_are_skipped() {
    let root = tempfile::tempdir().unwrap();
    write(root.path(), ".hidden.txt", "target\n");
    write(root.path(), ".git/config.txt", "target\n");
    write(root.path(), "plain.txt", "target\n");

    let sink = Arc::new(RecordingSink::new());
    let mut req = request(root.path(), "target");
    req.mode = Mode::Files;
    run(&req, sink.clone()).unwrap();

    let mut paths: Vec<String> = sink.events().into_iter().map(|event| event.path).collect();
    paths.sort();
    assert_eq!(paths.len(), 2);
    assert!(paths.iter().any(|path| path.ends_with(".hidden.txt")));
    assert!(paths.iter().any(|path| path.ends_with("plain.txt")));
}

#[test]
fn binary_files_are_skipped_like_ripgrep_files_mode() {
    let root = tempfile::tempdir().unwrap();
    // rg -l suppresses binary files even when a match precedes the NUL byte.
    std::fs::write(root.path().join("early.bin"), b"hello\n\x00junk\n").unwrap();
    std::fs::write(root.path().join("split.bin"), b"hel\x00lo\n").unwrap();
    write(root.path(), "text.txt", "hello there\n");

    let sink = Arc::new(RecordingSink::new());
    let mut req = request(root.path(), "hello");
    req.mode = Mode::Files;
    run(&req, sink.clone()).unwrap();

    let paths: Vec<String> = sink.events().into_iter().map(|event| event.path).collect();
    assert_eq!(paths.len(), 1);
    assert!(paths[0].ends_with("text.txt"));
}

#[test]
fn glob_filters_restrict_files() {
    let root = tempfile::tempdir().unwrap();
    write(root.path(), "keep.txt", "target\n");
    write(root.path(), "skip.log", "target\n");

    let sink = Arc::new(RecordingSink::new());
    let globs = vec!["*.txt".to_string()];
    let req = Request {
        pattern: "target",
        path: root.path(),
        globs: &globs,
        mode: Mode::Files,
        case_insensitive: false,
        multiline: false,
        only_matching: false,
        before_context: 0,
        after_context: 0,
    };
    run(&req, sink.clone()).unwrap();

    let paths: Vec<String> = sink.events().into_iter().map(|event| event.path).collect();
    assert_eq!(paths.len(), 1);
    assert!(paths[0].ends_with("keep.txt"));
}

#[test]
fn case_insensitive_flag_matches_any_case() {
    let root = tempfile::tempdir().unwrap();
    write(root.path(), "shout.txt", "HELLO world\n");

    let mut req = request(root.path(), "hello");
    req.case_insensitive = true;
    let sink = Arc::new(RecordingSink::new());
    run(&req, sink.clone()).unwrap();
    assert_eq!(sink.texts(), vec!["HELLO world".to_string()]);
}

#[test]
fn single_file_search_skips_the_walker() {
    let root = tempfile::tempdir().unwrap();
    write(root.path(), "one.txt", "hello world\n");
    write(root.path(), "two.txt", "hello again\n");

    let file = root.path().join("one.txt");
    let req = Request {
        pattern: "hello",
        path: file.as_path(),
        globs: &[],
        mode: Mode::Content,
        case_insensitive: false,
        multiline: false,
        only_matching: false,
        before_context: 0,
        after_context: 0,
    };
    let sink = Arc::new(RecordingSink::new());
    run(&req, sink.clone()).unwrap();

    let events = sink.events();
    assert_eq!(events.len(), 1);
    assert!(events[0].path.ends_with("one.txt"));
    assert_eq!(events[0].text, "hello world");
}

#[test]
fn invalid_pattern_is_reported() {
    let root = tempfile::tempdir().unwrap();
    let req = request(root.path(), "(");
    let sink = Arc::new(RecordingSink::new());
    match run(&req, sink) {
        Err(SearchError::InvalidPattern(_)) => {}
        other => panic!("expected invalid pattern, got {other:?}"),
    }
}

#[test]
fn stopping_the_callback_cancels_the_walk() {
    let root = tempfile::tempdir().unwrap();
    for index in 0..50 {
        write(root.path(), &format!("file{index:02}.txt"), "hit\n");
    }

    let sink = Arc::new(RecordingSink::new());
    sink.cancel_after(1);
    let mut req = request(root.path(), "hit");
    req.mode = Mode::Files;
    let outcome = run(&req, sink.clone()).unwrap();

    assert!(matches!(outcome, Outcome::Stopped));
    assert!(sink.events().len() < 50);
}
