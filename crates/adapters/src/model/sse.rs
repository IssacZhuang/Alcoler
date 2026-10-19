//! Minimal SSE frame parser shared by the streaming providers.

pub struct SseFrame {
    pub event: Option<String>,
    pub data: String,
}

#[derive(Default)]
pub struct SseBuffer {
    buf: String,
}

impl SseBuffer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn push(&mut self, chunk: &[u8]) {
        self.buf.push_str(&String::from_utf8_lossy(chunk));
    }

    /// Extract all complete frames (blocks terminated by a blank line).
    pub fn frames(&mut self) -> Vec<SseFrame> {
        self.buf = self.buf.replace("\r\n", "\n");
        let mut out = Vec::new();
        while let Some(position) = self.buf.find("\n\n") {
            let block: String = self.buf.drain(..position + 2).collect();
            let mut event = None;
            let mut data_lines: Vec<String> = Vec::new();
            for line in block.trim_end_matches('\n').split('\n') {
                if let Some(rest) = line.strip_prefix("event:") {
                    event = Some(rest.trim_start().to_string());
                } else if let Some(rest) = line.strip_prefix("data:") {
                    data_lines.push(rest.strip_prefix(' ').unwrap_or(rest).to_string());
                }
            }
            if data_lines.is_empty() {
                continue;
            }
            out.push(SseFrame {
                event,
                data: data_lines.join("\n"),
            });
        }
        out
    }
}
