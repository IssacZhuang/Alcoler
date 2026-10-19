// E1 kernel spike (Go). Contract v1.1: JSONL commands on stdin,
// JSONL events (epoch-ms ts) on stdout. See experiments/e1-kernel/README.md.
package main

import (
	"bufio"
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"runtime"
	"runtime/debug"
	"strings"
	"sync"
	"time"

	_ "modernc.org/sqlite"
)

var stdoutMu sync.Mutex

func now() int64 { return time.Now().UnixMilli() }

func emit(v map[string]any) {
	b, err := json.Marshal(v)
	if err != nil {
		return
	}
	stdoutMu.Lock()
	fmt.Fprintf(os.Stdout, "%s\n", b)
	stdoutMu.Unlock()
}

type toolMeta struct {
	readonly     bool
	destructive  bool
	approval     bool
	maxOutBytes  int
}

var tools = map[string]toolMeta{
	"fs.read": {readonly: true, destructive: false, approval: false, maxOutBytes: 65536},
	"exec":    {readonly: false, destructive: true, approval: true, maxOutBytes: 8192},
}

type ctxEntry struct {
	Kind  string `json:"kind"`
	Bytes int    `json:"bytes"`
}

type activeExec struct {
	turn      int
	cmd       *exec.Cmd
	cancelled bool
}

type session struct {
	id      string
	profile profile

	mu      sync.Mutex
	ctx     []ctxEntry
	tokens  int
	active  *activeExec
}

type profile struct {
	Turns         int    `json:"turns"`
	EventsPerTurn int    `json:"eventsPerTurn"`
	IntervalMs    int    `json:"intervalMs"`
	BudgetTokens  int    `json:"budgetTokens"`
	Provider      string `json:"provider"`
}

var (
	sessionsMu sync.Mutex
	sessions   = map[string]*session{}
	db         *sql.DB
	dbMu       sync.Mutex
)

func (s *session) approve(tool string) bool { return true } // permission stub

func (s *session) truncate() {
	budgetBytes := s.profile.BudgetTokens * 4
	total := 0
	for _, e := range s.ctx {
		total += e.Bytes
	}
	removed := false
	for total > budgetBytes {
		idx := -1
		for i, e := range s.ctx {
			if e.Kind == "tool_result" && e.Bytes > 0 {
				idx = i
				break
			}
		}
		if idx < 0 {
			for i, e := range s.ctx {
				if e.Bytes > 0 {
					idx = i
					break
				}
			}
		}
		if idx < 0 {
			break
		}
		total -= s.ctx[idx].Bytes
		s.ctx[idx].Bytes = 0
		removed = true
	}
	if removed {
		emit(map[string]any{"type": "compacted", "session": s.id, "kept_tokens": total / 4, "ts": now()})
	}
	s.tokens = total / 4
}

func (s *session) persist(turn int) {
	s.mu.Lock()
	state, _ := json.Marshal(map[string]any{"turn": turn, "tokens": s.tokens, "entries": s.ctx})
	entries := string(state)
	s.mu.Unlock()
	dbMu.Lock()
	db.Exec(`INSERT INTO sessions(id, turn, state, updated_at) VALUES(?, ?, ?, ?)
	         ON CONFLICT(id) DO UPDATE SET turn=excluded.turn, state=excluded.state, updated_at=excluded.updated_at`,
		s.id, turn, entries, now())
	dbMu.Unlock()
}

type wireEvent struct {
	Type string          `json:"type"`
	Seq  int             `json:"seq"`
	Text string          `json:"text"`
	Tool string          `json:"tool"`
	Args json.RawMessage `json:"args"`
}

func runTurnStream(s *session, turn int) (textBytes int, toolCalls []wireEvent, err error) {
	url := fmt.Sprintf("%s/sse?session=%s&turn=%d&n=%d&interval=%d",
		s.profile.Provider, s.id, turn, s.profile.EventsPerTurn, s.profile.IntervalMs)
	resp, err := http.Get(url)
	if err != nil {
		return 0, nil, err
	}
	defer resp.Body.Close()
	sc := bufio.NewScanner(resp.Body)
	sc.Buffer(make([]byte, 0, 64*1024), 1024*1024)
	for sc.Scan() {
		line := sc.Text()
		if !strings.HasPrefix(line, "data: ") {
			continue
		}
		var ev wireEvent
		if err := json.Unmarshal([]byte(line[6:]), &ev); err != nil {
			continue
		}
		switch ev.Type {
		case "text_delta":
			b := len(ev.Text)
			textBytes += b
			emit(map[string]any{"type": "model_delta", "session": s.id, "turn": turn, "seq": ev.Seq, "bytes": b, "ts": now()})
		case "tool_call":
			toolCalls = append(toolCalls, ev)
		case "turn_end":
			return textBytes, toolCalls, nil
		}
	}
	return textBytes, toolCalls, io.ErrUnexpectedEOF
}

func runExec(s *session, turn, seq int, argv []string) {
	emit(map[string]any{"type": "tool_call", "session": s.id, "turn": turn, "tool": "exec", "seq": seq, "ts": now()})
	t0 := time.Now()
	cmd := exec.Command(argv[0], argv[1:]...)
	cmd.Stdout = nil
	cmd.Stderr = nil
	if err := cmd.Start(); err != nil {
		emit(map[string]any{"type": "tool_result", "session": s.id, "turn": turn, "tool": "exec", "seq": seq, "status": "error", "bytes": 0, "durationMs": msSince(t0), "ts": now()})
		return
	}
	s.mu.Lock()
	s.active = &activeExec{turn: turn, cmd: cmd}
	s.mu.Unlock()
	_ = cmd.Wait()
	s.mu.Lock()
	cancelled := s.active != nil && s.active.cancelled
	s.active = nil
	s.mu.Unlock()
	status := "ok"
	if cancelled {
		status = "cancelled"
	}
	emit(map[string]any{"type": "tool_result", "session": s.id, "turn": turn, "tool": "exec", "seq": seq,
		"status": status, "bytes": 24, "durationMs": msSince(t0), "ts": now()})
	s.mu.Lock()
	s.ctx = append(s.ctx, ctxEntry{Kind: "tool_result", Bytes: 24})
	s.mu.Unlock()
}

func msSince(t0 time.Time) float64 {
	return float64(time.Since(t0).Microseconds()) / 1000.0
}

func runSession(s *session) {
	emit(map[string]any{"type": "session_started", "session": s.id, "ts": now()})
	for turn := 1; turn <= s.profile.Turns; turn++ {
		emit(map[string]any{"type": "turn_started", "session": s.id, "turn": turn, "ts": now()})
		textBytes, toolCalls, err := runTurnStream(s, turn)
		if err != nil {
			emit(map[string]any{"type": "error", "session": s.id, "code": err.Error(), "fatal": true, "ts": now()})
			return
		}
		for _, tc := range toolCalls {
			if !s.approve(tc.Tool) {
				continue
			}
			if tc.Tool == "fs.read" {
				emit(map[string]any{"type": "tool_call", "session": s.id, "turn": turn, "tool": "fs.read", "seq": tc.Seq, "ts": now()})
				t0 := time.Now()
				var args struct {
					Path string `json:"path"`
				}
				_ = json.Unmarshal(tc.Args, &args)
				data, err := os.ReadFile(args.Path)
				bytes := len(data)
				if bytes > tools["fs.read"].maxOutBytes {
					bytes = tools["fs.read"].maxOutBytes
				}
				status := "ok"
				if err != nil {
					status = "error"
					bytes = 0
				}
				emit(map[string]any{"type": "tool_result", "session": s.id, "turn": turn, "tool": "fs.read", "seq": tc.Seq,
					"status": status, "bytes": bytes, "durationMs": msSince(t0), "ts": now()})
				s.mu.Lock()
				s.ctx = append(s.ctx, ctxEntry{Kind: "tool_result", Bytes: bytes})
				s.mu.Unlock()
			} else if tc.Tool == "exec" {
				var args struct {
					Argv []string `json:"argv"`
				}
				_ = json.Unmarshal(tc.Args, &args)
				runExec(s, turn, tc.Seq, args.Argv)
			}
		}
		s.mu.Lock()
		if textBytes > 0 {
			s.ctx = append(s.ctx, ctxEntry{Kind: "text", Bytes: textBytes})
		}
		s.truncate()
		s.mu.Unlock()
		s.persist(turn)
		emit(map[string]any{"type": "turn_done", "session": s.id, "turn": turn, "ts": now()})
	}
	emit(map[string]any{"type": "session_done", "session": s.id, "ts": now()})
}

func killChildren() {
	sessionsMu.Lock()
	defer sessionsMu.Unlock()
	for _, s := range sessions {
		s.mu.Lock()
		if s.active != nil && s.active.cmd != nil && s.active.cmd.Process != nil {
			_ = s.active.cmd.Process.Kill()
		}
		s.mu.Unlock()
	}
}

func shutdown() {
	killChildren()
	dbMu.Lock()
	if db != nil {
		_ = db.Close()
	}
	dbMu.Unlock()
	os.Exit(0)
}

type command struct {
	Cmd     string   `json:"cmd"`
	ID      string   `json:"id"`
	Session string   `json:"session"`
	Turn    int      `json:"turn"`
	Profile *profile `json:"profile"`
}

func main() {
	dbPath := os.Getenv("E1_DB")
	if dbPath == "" {
		dbPath = os.TempDir() + fmt.Sprintf("\\e1-go-%d.sqlite", os.Getpid())
	}
	var err error
	db, err = sql.Open("sqlite", dbPath)
	if err != nil {
		panic(err)
	}
	if _, err := db.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"); err != nil {
		panic(err)
	}
	if _, err := db.Exec("CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, turn INTEGER, state TEXT, updated_at INTEGER)"); err != nil {
		panic(err)
	}
	emit(map[string]any{"type": "ready", "ts": now()})

	sc := bufio.NewScanner(os.Stdin)
	sc.Buffer(make([]byte, 0, 64*1024), 1024*1024)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" {
			continue
		}
		var cmd command
		if err := json.Unmarshal([]byte(line), &cmd); err != nil {
			continue
		}
		switch cmd.Cmd {
		case "start_session":
			if cmd.Profile == nil {
				continue
			}
			s := &session{id: cmd.ID, profile: *cmd.Profile}
			sessionsMu.Lock()
			sessions[s.id] = s
			sessionsMu.Unlock()
			go runSession(s)
		case "cancel":
			sessionsMu.Lock()
			s := sessions[cmd.Session]
			sessionsMu.Unlock()
			if s == nil {
				continue
			}
			s.mu.Lock()
			if s.active != nil && s.active.turn == cmd.Turn && !s.active.cancelled {
				s.active.cancelled = true
				if s.active.cmd != nil && s.active.cmd.Process != nil {
					_ = s.active.cmd.Process.Kill()
				}
			}
			s.mu.Unlock()
		case "gc":
			runtime.GC()
			debug.FreeOSMemory()
			emit(map[string]any{"type": "gc_done", "forced": true, "ts": now()})
		case "shutdown":
			shutdown()
		}
	}
	shutdown()
}
