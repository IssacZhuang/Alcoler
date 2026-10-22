using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Alcoler.Browser.IntegrationTests;

internal sealed class LoopbackFixture : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _connections = new(12);
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly Task _acceptLoop;
    private int _sequence;

    public LoopbackFixture()
    {
        _listener.Start(12);
        Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _acceptLoop = AcceptAsync();
    }

    public string Origin { get; }
    public string Url(string path = "/") => Origin + path;
    public static byte[] MediaBytes => Encoding.UTF8.GetBytes("Alcoler native browser fixture media\n");

    public void AssertHealthy()
    {
        if (!_errors.IsEmpty) throw new AggregateException("Loopback fixture request failed.", _errors);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                await _connections.WaitAsync(_shutdown.Token);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_shutdown.Token); }
                catch { _connections.Release(); throw; }
                var id = Interlocked.Increment(ref _sequence);
                _requests[id] = ServeAsync(client);
                _ = _requests[id].ContinueWith(_ => _requests.TryRemove(id, out var ignored),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { _errors.Enqueue(ex); }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream();
                var header = new byte[MaxHeaderBytes];
                var length = 0;
                var end = -1;
                while (length < header.Length && end < 0)
                {
                    var read = await stream.ReadAsync(header.AsMemory(length), timeout.Token);
                    if (read == 0) return;
                    length += read;
                    end = header.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                }
                if (end < 0) throw new InvalidDataException("HTTP request headers exceeded 16 KiB.");
                var lines = Encoding.ASCII.GetString(header, 0, end).Split("\r\n", StringSplitOptions.None);
                var request = lines[0].Split(' ');
                if (request.Length != 3 || request[0] is not ("GET" or "HEAD") || !request[1].StartsWith('/'))
                    throw new InvalidDataException("Fixture accepts only bounded GET/HEAD requests.");
                if (lines.Skip(1).Any(line => line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && line[15..].Trim() != "0"))
                    throw new InvalidDataException("Fixture requests must not contain a body.");

                var path = request[1].Split('?', 2)[0];
                var (status, mime, body) = path switch
                {
                    "/" => ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page)),
                    "/frame" => ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Frame)),
                    "/second" => ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes("<!doctype html><title>Second fixture page</title><h1>Second page</h1>")),
                    "/popup" => ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes("<!doctype html><title>Popup fixture</title><h1>Session popup</h1>")),
                    "/media" or "/download" => ("200 OK", "application/octet-stream", MediaBytes),
                    "/favicon.ico" => ("204 No Content", "image/x-icon", Array.Empty<byte>()),
                    _ => ("404 Not Found", "text/plain; charset=utf-8", "Not found"u8.ToArray())
                };
                if (body.Length > 256 * 1024) throw new InvalidDataException("Fixture response exceeded its bound.");
                var disposition = path == "/download" ? "Content-Disposition: attachment; filename=fixture-download.bin\r\n" : "";
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {mime}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{disposition}\r\n");
                await stream.WriteAsync(response, timeout.Token);
                if (request[0] != "HEAD") await stream.WriteAsync(body, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (IOException ex) when (ex.InnerException is SocketException) { }
            catch (Exception ex) { _errors.Enqueue(ex); }
            finally { _connections.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        await _acceptLoop;
        await Task.WhenAll(_requests.Values).WaitAsync(TimeSpan.FromSeconds(12));
        _shutdown.Dispose();
        _connections.Dispose();
        AssertHealthy();
    }

    private const string Frame = """
        <!doctype html><html><head><title>Frame fixture</title><style>body{margin:8px}button,input{font:16px sans-serif;margin:4px}</style></head>
        <body><button id="frame-click" onclick="document.body.dataset.clicked='yes'">Frame action</button><label>Frame name <input id="frame-name" oninput="document.body.dataset.value=this.value"></label></body></html>
        """;

    private const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Alcoler native fixture</title>
        <style>
        *{box-sizing:border-box}body{font:16px sans-serif;margin:16px;background:#eef3fb;color:#13243a;min-height:1900px}
        h1{font-size:24px;margin:0 0 12px}button,input,select{font:16px sans-serif;padding:6px;margin:3px}
        section{margin:10px 0;padding:8px;background:white;border:1px solid #b7c8dd}label{display:inline-block}
        #drag-row{display:flex;gap:80px;height:70px;align-items:center}#drag-source,#drag-target{width:140px;height:48px;padding:12px;border:2px solid #1458a3;user-select:none;touch-action:none}
        iframe{width:100%;height:130px;border:0}#bottom-marker{margin-top:950px;padding:20px;background:#b7dfbd}
        </style></head><body data-marker="fixture-ready" data-css-clicks="0" data-role-clicks="0" data-text-clicks="0" data-ref-clicks="0">
        <h1>Native integration fixture</h1><p id="marker">Real native browser marker</p>
        <section><button id="css-click" onclick="document.body.dataset.cssClicks++">CSS action</button>
        <button id="role-click" onclick="document.body.dataset.roleClicks++">Role action</button>
        <button id="text-click" onclick="document.body.dataset.textClicks++">Text action</button>
        <button id="ref-click" onclick="document.body.dataset.refClicks++">Reference action</button>
        <button id="delayed" onclick="setTimeout(()=>{const e=document.createElement('p');e.id='wait-marker';e.textContent='Wait completed';document.body.prepend(e)},100)">Reveal delayed</button></section>
        <section><label>Account name <input id="account" value="original" data-testid="account-input"></label>
        <input id="placeholder" aria-label="Alias" placeholder="Optional alias"><input id="keyboard" aria-label="Keyboard input">
        <label><input id="enabled" type="checkbox">Enable feature</label>
        <label for="choice">Choice</label><select id="choice"><option value="a">Alpha</option><option value="b">Beta</option></select>
        <ul id="items"><li>First item</li><li>Second item</li><li>Third item</li></ul><p id="attribute" data-answer="forty-two">Observed text</p></section>
        <section id="shadow-host"></section>
        <section><div id="drag-row"><div id="drag-source">Drag source</div><div id="drag-target">Drag target</div></div></section>
        <section><iframe id="fixture-frame" title="Fixture frame" src="/frame"></iframe></section>
        <a id="popup" href="/popup" target="_blank">Open popup</a> <a id="download" href="/download" download="fixture-download.bin">Download fixture</a>
        <div id="bottom-marker">Full page screenshot bottom marker</div>
        <script>
        const shadow=document.querySelector('#shadow-host').attachShadow({mode:'open'});
        shadow.innerHTML='<style>button,input{font:16px sans-serif;padding:6px;margin:3px}</style><button id="shadow-click">Shadow action</button><label>Shadow name <input id="shadow-name"></label>';
        shadow.querySelector('button').addEventListener('click',()=>document.body.dataset.shadowClicked='yes');
        let dragging=false,moves=0;
        document.querySelector('#drag-source').addEventListener('mousedown',()=>{dragging=true;moves=0});
        document.addEventListener('mousemove',e=>{if(dragging && e.buttons===1)moves++});
        document.addEventListener('mouseup',e=>{if(dragging){document.body.dataset.dragged=String(moves>1 && e.target.id==='drag-target');dragging=false}});
        document.querySelector('#keyboard').addEventListener('keydown',e=>{if(e.key==='Enter')document.body.dataset.keyboardEnter='yes'});
        </script></body></html>
        """;
}
