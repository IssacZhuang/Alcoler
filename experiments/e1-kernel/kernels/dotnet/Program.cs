// E1 kernel spike (C# / .NET 8). Contract v1.1: JSONL commands on stdin,
// JSONL events (epoch-ms ts) on stdout. See experiments/e1-kernel/README.md.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class Program
{
    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static readonly object StdoutLock = new();
    static void Emit(object ev)
    {
        lock (StdoutLock)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(ev));
            Console.Out.Flush();
        }
    }

    static readonly Dictionary<string, ToolMeta> Tools = new()
    {
        ["fs.read"] = new ToolMeta(Readonly: true, Destructive: false, Approval: false, MaxOutBytes: 65536),
        ["exec"] = new ToolMeta(Readonly: false, Destructive: true, Approval: true, MaxOutBytes: 8192),
    };

    static Task<bool> Approve(string tool) => Task.FromResult(true); // permission stub

    static async Task<int> Main()
    {
        var dbPath = Environment.GetEnvironmentVariable("E1_DB")
            ?? Path.Combine(Path.GetTempPath(), $"e1-dotnet-{Process.GetCurrentProcess().Id}.sqlite");
        var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        using (var prag = db.CreateCommand())
        {
            prag.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await prag.ExecuteNonQueryAsync();
        }
        using (var init = db.CreateCommand())
        {
            init.CommandText = "CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, turn INTEGER, state TEXT, updated_at INTEGER)";
            await init.ExecuteNonQueryAsync();
        }

        var sessions = new Dictionary<string, Session>();
        Emit(new { type = "ready", ts = Now() });

        string? lineIn;
        while ((lineIn = await Console.In.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(lineIn)) continue;
            using var doc = JsonDocument.Parse(lineIn);
            var root = doc.RootElement;
            if (!root.TryGetProperty("cmd", out var cmdEl)) continue;
            switch (cmdEl.GetString())
            {
                case "start_session":
                {
                    var id = root.GetProperty("id").GetString() ?? "";
                    var p = root.GetProperty("profile");
                    var sess = new Session
                    {
                        Id = id,
                        Prof = new Profile
                        {
                            Turns = p.GetProperty("turns").GetInt32(),
                            EventsPerTurn = p.GetProperty("eventsPerTurn").GetInt32(),
                            IntervalMs = p.GetProperty("intervalMs").GetInt32(),
                            BudgetTokens = p.GetProperty("budgetTokens").GetInt32(),
                            Provider = p.GetProperty("provider").GetString() ?? "",
                        },
                    };
                    lock (sessions) sessions[id] = sess;
                    _ = Task.Run(async () =>
                    {
                        try { await RunSession(db, sess); }
                        catch (Exception e)
                        {
                            Emit(new { type = "error", session = id, code = e.Message, fatal = true, ts = Now() });
                        }
                    });
                    break;
                }
                case "cancel":
                {
                    var id = root.GetProperty("session").GetString() ?? "";
                    int turn = root.GetProperty("turn").GetInt32();
                    Session? sess;
                    lock (sessions) sess = sessions.GetValueOrDefault(id);
                    if (sess != null)
                    {
                        lock (sess.Mu)
                        {
                            if (sess.Active is { } a && a.Turn == turn && !a.Cancelled)
                            {
                                a.Cancelled = true;
                                a.Cts.Cancel();
                            }
                        }
                    }
                    break;
                }
                case "gc":
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    Emit(new { type = "gc_done", forced = true, ts = Now() });
                    break;
                case "shutdown":
                    foreach (var s in sessions.Values)
                    {
                        lock (s.Mu)
                        {
                            if (s.Active is { } a)
                            {
                                try { a.Proc.Kill(entireProcessTree: true); } catch { }
                            }
                        }
                    }
                    await db.CloseAsync();
                    Environment.Exit(0);
                    break;
            }
        }
        Environment.Exit(0);
        return 0;
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static async Task RunSession(SqliteConnection db, Session sess)
    {
        Emit(new { type = "session_started", session = sess.Id, ts = Now() });
        for (int turn = 1; turn <= sess.Prof.Turns; turn++)
        {
            Emit(new { type = "turn_started", session = sess.Id, turn, ts = Now() });
            string url = $"{sess.Prof.Provider}/sse?session={Uri.EscapeDataString(sess.Id)}&turn={turn}&n={sess.Prof.EventsPerTurn}&interval={sess.Prof.IntervalMs}";
            int textBytes = 0;
            var toolCalls = new List<(int seq, string tool, JsonElement args)>();

            using (var stream = await Http.GetStreamAsync(url))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    using var doc = JsonDocument.Parse(line.Substring(6));
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString();
                    if (type == "text_delta")
                    {
                        int b = Encoding.UTF8.GetByteCount(root.GetProperty("text").GetString() ?? "");
                        textBytes += b;
                        Emit(new
                        {
                            type = "model_delta",
                            session = sess.Id,
                            turn,
                            seq = root.GetProperty("seq").GetInt32(),
                            bytes = b,
                            ts = Now(),
                        });
                    }
                    else if (type == "tool_call")
                    {
                        toolCalls.Add((root.GetProperty("seq").GetInt32(), root.GetProperty("tool").GetString() ?? "", root.GetProperty("args").Clone()));
                    }
                    else if (type == "turn_end")
                    {
                        break;
                    }
                }
            }

            foreach (var (seq, tool, args) in toolCalls)
            {
                var meta = Tools.GetValueOrDefault(tool);
                if (meta == null) continue;
                if (meta.Approval && !await Approve(tool)) continue;

                if (tool == "fs.read")
                {
                    Emit(new { type = "tool_call", session = sess.Id, turn, tool = "fs.read", seq, ts = Now() });
                    var sw = Stopwatch.StartNew();
                    string path = args.GetProperty("path").GetString() ?? "";
                    string status = "ok";
                    int bytes = 0;
                    try
                    {
                        var data = await File.ReadAllBytesAsync(path);
                        bytes = Math.Min(data.Length, meta.MaxOutBytes);
                    }
                    catch
                    {
                        status = "error";
                    }
                    Emit(new { type = "tool_result", session = sess.Id, turn, tool = "fs.read", seq, status, bytes, durationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1), ts = Now() });
                    lock (sess.Mu) sess.Ctx.Add(new CtxEntry { Kind = "tool_result", Bytes = bytes });
                }
                else if (tool == "exec")
                {
                    Emit(new { type = "tool_call", session = sess.Id, turn, tool = "exec", seq, ts = Now() });
                    var sw = Stopwatch.StartNew();
                    var argv = args.GetProperty("argv").EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
                    string status;
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = argv[0],
                            UseShellExecute = false,
                            RedirectStandardOutput = false,
                            RedirectStandardError = false,
                        };
                        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
                        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("process not started");
                        using var cts = new CancellationTokenSource();
                        var active = new ActiveExec { Turn = turn, Proc = proc, Cts = cts };
                        lock (sess.Mu) sess.Active = active;
                        var exited = new TaskCompletionSource();
                        proc.EnableRaisingEvents = true;
                        proc.Exited += (_, _) => exited.TrySetResult();
                        if (proc.HasExited) exited.TrySetResult();
                        try
                        {
                            await exited.Task.WaitAsync(cts.Token);
                            status = "ok";
                        }
                        catch (OperationCanceledException)
                        {
                            active.Cancelled = true;
                            try { proc.Kill(entireProcessTree: true); } catch { }
                            await exited.Task;
                            status = "cancelled";
                        }
                        lock (sess.Mu) sess.Active = null;
                    }
                    catch (Exception)
                    {
                        status = "error";
                    }
                    Emit(new { type = "tool_result", session = sess.Id, turn, tool = "exec", seq, status, bytes = 24, durationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1), ts = Now() });
                    lock (sess.Mu) sess.Ctx.Add(new CtxEntry { Kind = "tool_result", Bytes = 24 });
                }
            }

            if (textBytes > 0) lock (sess.Mu) sess.Ctx.Add(new CtxEntry { Kind = "text", Bytes = textBytes });
            int kept = sess.Truncate();
            if (sess.RemovedLastTruncate)
            {
                Emit(new { type = "compacted", session = sess.Id, kept_tokens = kept, ts = Now() });
            }
            string state = sess.StateJson(turn);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO sessions(id, turn, state, updated_at) VALUES($id, $turn, $state, $ts) " +
                                  "ON CONFLICT(id) DO UPDATE SET turn=excluded.turn, state=excluded.state, updated_at=excluded.updated_at";
                cmd.Parameters.AddWithValue("$id", sess.Id);
                cmd.Parameters.AddWithValue("$turn", turn);
                cmd.Parameters.AddWithValue("$state", state);
                cmd.Parameters.AddWithValue("$ts", Now());
                await cmd.ExecuteNonQueryAsync();
            }
            Emit(new { type = "turn_done", session = sess.Id, turn, ts = Now() });
        }
        Emit(new { type = "session_done", session = sess.Id, ts = Now() });
    }
}

internal sealed record ToolMeta(bool Readonly, bool Destructive, bool Approval, int MaxOutBytes);

internal sealed class CtxEntry
{
    public string Kind = "";
    public int Bytes;
}

internal sealed class ActiveExec
{
    public required int Turn;
    public required Process Proc;
    public required CancellationTokenSource Cts;
    public volatile bool Cancelled;
}

internal sealed class Profile
{
    public int Turns;
    public int EventsPerTurn;
    public int IntervalMs;
    public int BudgetTokens;
    public string Provider = "";
}

internal sealed class Session
{
    public required string Id;
    public required Profile Prof;
    public readonly List<CtxEntry> Ctx = new();
    public readonly object Mu = new();
    public ActiveExec? Active;
    public int Tokens;

    /// Returns kept token count after truncation (emission of the compacted
    /// event is done by the caller, which owns the event protocol).
    public int Truncate()
    {
        int budgetBytes = Prof.BudgetTokens * 4;
        int kept;
        lock (Mu)
        {
            int total = 0;
            foreach (var e in Ctx) total += e.Bytes;
            bool removed = false;
            while (total > budgetBytes)
            {
                int idx = Ctx.FindIndex(e => e.Kind == "tool_result" && e.Bytes > 0);
                if (idx < 0) idx = Ctx.FindIndex(e => e.Bytes > 0);
                if (idx < 0) break;
                total -= Ctx[idx].Bytes;
                Ctx[idx].Bytes = 0;
                removed = true;
            }
            kept = total / 4;
            RemovedLastTruncate = removed;
            Tokens = kept;
        }
        return kept;
    }

    public bool RemovedLastTruncate;

    public string StateJson(int turn)
    {
        lock (Mu)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"turn\":").Append(turn).Append(",\"tokens\":").Append(Tokens).Append(",\"entries\":[");
            for (int i = 0; i < Ctx.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"kind\":\"").Append(Ctx[i].Kind).Append("\",\"bytes\":").Append(Ctx[i].Bytes).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
