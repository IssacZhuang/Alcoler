//! Builtin tools: file read/write/list and shell command execution.

use std::path::Path;
use std::sync::Arc;

use alcoler_contracts::BoxFuture;
use alcoler_contracts::message::OutputBlock;
use alcoler_contracts::tool::{
    ApprovalPolicy, Concurrency, EffectScope, RiskLevel, Tool, ToolEffect, ToolExecContext,
    ToolOutput, ToolPolicy, ToolSpec, ToolStatus,
};
use serde_json::{Value, json};

/// The default toolset injected at the composition root.
pub fn builtin_tools() -> Vec<Arc<dyn Tool>> {
    vec![
        Arc::new(ReadFile::new()),
        Arc::new(WriteFile::new()),
        Arc::new(ListDir::new()),
        Arc::new(RunCommand::new()),
    ]
}

fn io_failed(context: &str, error: &std::io::Error) -> ToolOutput {
    ToolOutput::failed("io_error", format!("{context}: {error}"))
}

struct ReadFile {
    spec: ToolSpec,
}

impl ReadFile {
    fn new() -> Self {
        Self {
            spec: ToolSpec {
                name: "read_file".into(),
                description: "Read a file's content as UTF-8 text.".into(),
                input_schema: json!({
                    "type": "object",
                    "properties": {"path": {"type": "string"}},
                    "required": ["path"]
                }),
                policy: ToolPolicy::default(),
            },
        }
    }
}

impl Tool for ReadFile {
    fn spec(&self) -> &ToolSpec {
        &self.spec
    }

    fn execute<'a>(&'a self, input: Value, _ctx: &'a ToolExecContext) -> BoxFuture<'a, ToolOutput> {
        Box::pin(async move {
            let path = input["path"].as_str().unwrap_or_default().to_string();
            match tokio::fs::read(&path).await {
                Ok(bytes) => {
                    let text = String::from_utf8_lossy(&bytes).to_string();
                    let size = text.len() as u64;
                    let mut out = ToolOutput::ok(vec![OutputBlock::Text { text }]);
                    out.bytes_emitted = size;
                    out
                }
                Err(error) => io_failed("read_file", &error),
            }
        })
    }
}

struct WriteFile {
    spec: ToolSpec,
}

impl WriteFile {
    fn new() -> Self {
        Self {
            spec: ToolSpec {
                name: "write_file".into(),
                description: "Write text content to a file (creates or overwrites).".into(),
                input_schema: json!({
                    "type": "object",
                    "properties": {
                        "path": {"type": "string"},
                        "content": {"type": "string"}
                    },
                    "required": ["path", "content"]
                }),
                policy: ToolPolicy {
                    effect: ToolEffect::Mutating,
                    concurrency: Concurrency::ParallelSafe,
                    effect_scope: EffectScope::Workspace,
                    risk: RiskLevel::Medium,
                    approval: ApprovalPolicy::ByRisk,
                    ..ToolPolicy::default()
                },
            },
        }
    }
}

impl Tool for WriteFile {
    fn spec(&self) -> &ToolSpec {
        &self.spec
    }

    fn execute<'a>(&'a self, input: Value, _ctx: &'a ToolExecContext) -> BoxFuture<'a, ToolOutput> {
        Box::pin(async move {
            let path = input["path"].as_str().unwrap_or_default().to_string();
            let content = input["content"].as_str().unwrap_or_default().to_string();
            if path.is_empty() {
                return ToolOutput::failed("invalid_input", "path must not be empty");
            }
            if let Some(parent) = Path::new(&path).parent()
                && !parent.as_os_str().is_empty()
                && let Err(error) = tokio::fs::create_dir_all(parent).await
            {
                return io_failed("write_file mkdir", &error);
            }
            match tokio::fs::write(&path, content.as_bytes()).await {
                Ok(()) => {
                    let mut out = ToolOutput::ok(vec![OutputBlock::Text {
                        text: format!("wrote {} bytes to {path}", content.len()),
                    }]);
                    out.bytes_emitted = content.len() as u64;
                    out
                }
                Err(error) => io_failed("write_file", &error),
            }
        })
    }
}

struct ListDir {
    spec: ToolSpec,
}

impl ListDir {
    fn new() -> Self {
        Self {
            spec: ToolSpec {
                name: "list_dir".into(),
                description: "List the entries of a directory.".into(),
                input_schema: json!({
                    "type": "object",
                    "properties": {"path": {"type": "string"}},
                    "required": ["path"]
                }),
                policy: ToolPolicy::default(),
            },
        }
    }
}

impl Tool for ListDir {
    fn spec(&self) -> &ToolSpec {
        &self.spec
    }

    fn execute<'a>(&'a self, input: Value, _ctx: &'a ToolExecContext) -> BoxFuture<'a, ToolOutput> {
        Box::pin(async move {
            let path = input["path"].as_str().unwrap_or_default().to_string();
            let mut reader = match tokio::fs::read_dir(&path).await {
                Ok(reader) => reader,
                Err(error) => return io_failed("list_dir", &error),
            };
            let mut entries: Vec<(String, bool)> = Vec::new();
            loop {
                match reader.next_entry().await {
                    Ok(Some(entry)) => {
                        let is_dir = entry.file_type().await.map(|t| t.is_dir()).unwrap_or(false);
                        entries.push((entry.file_name().to_string_lossy().to_string(), is_dir));
                    }
                    Ok(None) => break,
                    Err(error) => return io_failed("list_dir", &error),
                }
            }
            entries.sort();
            let text = if entries.is_empty() {
                "(empty directory)".to_string()
            } else {
                entries
                    .into_iter()
                    .map(|(name, is_dir)| if is_dir { format!("{name}/") } else { name })
                    .collect::<Vec<_>>()
                    .join("\n")
            };
            let bytes = text.len() as u64;
            let mut out = ToolOutput::ok(vec![OutputBlock::Text { text }]);
            out.bytes_emitted = bytes;
            out
        })
    }
}

struct RunCommand {
    spec: ToolSpec,
}

/// Hard cap on captured output per stream; the kernel's output budget
/// governs what the model sees, this cap only bounds memory.
const CAPTURE_LIMIT: usize = 1024 * 1024;

impl RunCommand {
    fn new() -> Self {
        Self {
            spec: ToolSpec {
                name: "run_command".into(),
                description: "Run a shell command and return its merged output.".into(),
                input_schema: json!({
                    "type": "object",
                    "properties": {"command": {"type": "string"}},
                    "required": ["command"]
                }),
                policy: ToolPolicy {
                    effect: ToolEffect::Mutating,
                    concurrency: Concurrency::SerialOnly,
                    effect_scope: EffectScope::System,
                    risk: RiskLevel::High,
                    approval: ApprovalPolicy::ByRisk,
                    timeout_ms: 120_000,
                    timeout_overridable: false,
                    ..ToolPolicy::default()
                },
            },
        }
    }
}

impl Tool for RunCommand {
    fn spec(&self) -> &ToolSpec {
        &self.spec
    }

    fn execute<'a>(&'a self, input: Value, ctx: &'a ToolExecContext) -> BoxFuture<'a, ToolOutput> {
        Box::pin(async move {
            let command = input["command"].as_str().unwrap_or_default().to_string();
            if command.trim().is_empty() {
                return ToolOutput::failed("invalid_input", "command must not be empty");
            }

            let mut cmd = if cfg!(windows) {
                let mut cmd = tokio::process::Command::new("cmd");
                cmd.arg("/C");
                cmd
            } else {
                let mut cmd = tokio::process::Command::new("sh");
                cmd.arg("-c");
                cmd
            };
            let mut child = match cmd
                .arg(&command)
                .stdin(std::process::Stdio::null())
                .stdout(std::process::Stdio::piped())
                .stderr(std::process::Stdio::piped())
                .kill_on_drop(true)
                .spawn()
            {
                Ok(child) => child,
                Err(error) => return io_failed("run_command spawn", &error),
            };
            let pid = child.id();
            let mut stdout = child.stdout.take();
            let mut stderr = child.stderr.take();

            let output_task = tokio::spawn(async move {
                let mut stdout_buf = Vec::new();
                let mut stderr_buf = Vec::new();
                if let Some(stream) = stdout.as_mut() {
                    read_capped(stream, &mut stdout_buf).await;
                }
                if let Some(stream) = stderr.as_mut() {
                    read_capped(stream, &mut stderr_buf).await;
                }
                (stdout_buf, stderr_buf)
            });

            let cancel = ctx.cancel.cancelled();
            let wait = child.wait();
            let outcome = futures_util::future::select(Box::pin(wait), Box::pin(cancel)).await;
            let (exit_code, cancelled) = match outcome {
                futures_util::future::Either::Left((status, _)) => {
                    (status.ok().and_then(|status| status.code()), false)
                }
                futures_util::future::Either::Right(((), wait)) => {
                    // `cmd /C` spawns children that kill-on-drop does not
                    // reach on Windows; taskkill /T walks the tree without
                    // needing unsafe Job Object code at this fidelity.
                    if cfg!(windows) {
                        if let Some(pid) = pid {
                            let _ = tokio::process::Command::new("taskkill")
                                .args(["/PID", &pid.to_string(), "/T", "/F"])
                                .stdout(std::process::Stdio::null())
                                .stderr(std::process::Stdio::null())
                                .status()
                                .await;
                        }
                    } else {
                        let mut wait = Box::pin(wait);
                        let _ = wait.as_mut().await;
                    }
                    (None, true)
                }
            };
            let (stdout_buf, stderr_buf) = output_task.await.unwrap_or_default();

            let mut text = String::from_utf8_lossy(&stdout_buf).to_string();
            if !stderr_buf.is_empty() {
                if !text.is_empty() {
                    text.push('\n');
                }
                text.push_str(&String::from_utf8_lossy(&stderr_buf));
            }
            let bytes = text.len() as u64;

            if cancelled || ctx.cancel.is_cancelled() {
                return ToolOutput {
                    status: ToolStatus::Cancelled,
                    content: vec![OutputBlock::Text {
                        text: if text.is_empty() {
                            "command cancelled".into()
                        } else {
                            text
                        },
                    }],
                    bytes_emitted: bytes,
                    truncated: false,
                };
            }

            let failed = exit_code.map(|code| code != 0).unwrap_or(true);
            let header = match exit_code {
                Some(0) => String::new(),
                Some(code) => format!("[exit code {code}]\n"),
                None => "[terminated by signal]\n".to_string(),
            };
            let body = format!("{header}{text}");
            ToolOutput {
                status: if failed {
                    ToolStatus::Failed {
                        code: "exit_code".into(),
                        message: format!("command exited with status {exit_code:?}"),
                    }
                } else {
                    ToolStatus::Ok
                },
                content: vec![OutputBlock::Text { text: body }],
                bytes_emitted: bytes,
                truncated: false,
            }
        })
    }
}

async fn read_capped<R: tokio::io::AsyncRead + Unpin>(mut stream: R, buffer: &mut Vec<u8>) {
    use tokio::io::AsyncReadExt;
    let mut chunk = [0u8; 8192];
    loop {
        if buffer.len() >= CAPTURE_LIMIT {
            return;
        }
        match stream.read(&mut chunk).await {
            Ok(0) | Err(_) => return,
            Ok(n) => buffer.extend_from_slice(&chunk[..n]),
        }
    }
}
