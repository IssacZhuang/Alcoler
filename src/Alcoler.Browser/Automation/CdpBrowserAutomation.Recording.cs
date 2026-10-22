using System.Diagnostics;
using System.Text.Json;
using Alcoler.Contracts.Browser;

namespace Alcoler.Browser.Automation;

public sealed partial class CdpBrowserAutomation
{
    private CancellationTokenSource? _recordingCancellation;
    private Task? _recordingTask;
    private string _recordingState = "idle";
    private string? _recordingError;
    private byte[]? _recordingMedia;
    private long _recordedMilliseconds;
    private bool _recordingStop;

    public async Task SuspendAsync(CancellationToken cancellationToken)
    {
        _recordingCancellation?.Cancel();
        _recordingMedia = null;
        _recordingState = "cancelled";
        if (_disposed) return;
        var recording = _recordingTask;
        if (recording is not null) await recording.WaitAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _recordingMedia = null;
            _recordingState = "cancelled";
            _recordingError = null;
            await ReleasePressedInputAsync();
        }
        finally { _gate.Release(); }
    }

    private async Task<BrowserActionResult> RecordingAsync(string command, JsonElement input, CancellationToken ct)
    {
        if (command == "record_start")
        {
            if (_recordingTask is { IsCompleted: false }) throw new InvalidOperationException("A recording is already running.");
            var duration = Integer(input, "maxDurationMs", 10000, 100, 90000);
            var fps = Integer(input, "fps", 5, 1, 30);
            var context = await ContextAsync(null, ct);
            var value = RemoteValue(await CallAsync(context, """
                function(fps,maxBytes) {
                  if(typeof MediaRecorder==='undefined' || !HTMLCanvasElement.prototype.captureStream)throw Error('WebM recording is not supported by this browser');
                  const mime=['video/webm;codecs=vp9','video/webm;codecs=vp8','video/webm'].find(t=>MediaRecorder.isTypeSupported(t));
                  if(!mime)throw Error('No WebM MediaRecorder codec is supported');
                  const canvas=document.createElement('canvas');canvas.width=Math.min(innerWidth,1920);canvas.height=Math.min(innerHeight,1080);
                  const stream=canvas.captureStream(fps), recorder=new MediaRecorder(stream,{mimeType:mime,videoBitsPerSecond:2000000});
                  const state={canvas,stream,recorder,chunks:[],size:0,error:null,done:false};
                  state.finished=new Promise(resolve=>recorder.onstop=()=>{state.done=true;stream.getTracks().forEach(t=>t.stop());resolve(true);});
                  recorder.onerror=e=>{state.error=e.error?.message || 'MediaRecorder failed';};
                  recorder.ondataavailable=e=>{state.size+=e.data.size;if(state.size>maxBytes){state.error='Recording exceeds media size limit';if(recorder.state!=='inactive')recorder.stop();}else state.chunks.push(e.data);};
                  globalThis.__alcolerRecording=state;
                  recorder.start(250);
                  return {mimeType:mime,width:canvas.width,height:canvas.height};
                }
                """, [D(("value", fps)), D(("value", MaxMedia))], ct, true));
            _recordingCancellation?.Dispose();
            _recordingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _recordingState = "recording";
            _recordingError = null;
            _recordingMedia = null;
            _recordingStop = false;
            _recordedMilliseconds = 0;
            _recordingTask = RecordFramesAsync(context, duration, fps, _recordingCancellation.Token);
            return new(true, Text: "Recording started.", StructuredOutput: value.GetRawText());
        }
        if (command == "record_cancel")
        {
            _recordingCancellation?.Cancel();
            _recordingState = "cancelled";
            _recordingMedia = null;
            return Structured(BrowserJson.Element(D(("state", _recordingState))));
        }
        if (command == "record_stop") _recordingStop = true;
        if (_recordingState == "failed") return new(false, Error: _recordingError ?? "Recording failed.", StructuredOutput: BrowserJson.Serialize(D(("state", _recordingState))));
        var result = new BrowserActionResult(true, StructuredOutput: BrowserJson.Serialize(D(("state", _recordingState), ("elapsedMs", Interlocked.Read(ref _recordedMilliseconds)))), Media: _recordingMedia, MediaType: _recordingMedia is null ? null : "video/webm", MediaFileName: _recordingMedia is null ? null : "recording.webm");
        return result;
    }

    private async Task RecordFramesAsync(FrameContext context, int duration, int fps, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.ElapsedMilliseconds < duration && !_recordingStop)
            {
                ct.ThrowIfCancellationRequested();
                var frameStart = clock.ElapsedMilliseconds;
                await _gate.WaitAsync(ct);
                try
                {
                    if (!_contexts.TryGetValue(context.FrameId, out var current) || current.Id != context.Id) throw new InvalidOperationException("Recording was interrupted by page navigation.");
                    using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    frameTimeout.CancelAfter(10000);
                    var frame = await SendAsync("Page.captureScreenshot", D(("format", "jpeg"), ("quality", 65), ("fromSurface", true), ("captureBeyondViewport", false)), frameTimeout.Token);
                    var data = RequiredString(frame, "data");
                    if (data.Length > MaxMedia) throw new InvalidOperationException("Recording frame exceeds size limit.");
                    await CallAsync(context, "async function(data){const s=globalThis.__alcolerRecording;if(!s || s.error)throw Error(s?.error || 'Recording context lost');if(s.done)throw Error('Recording stopped unexpectedly');const image=new Image();image.src='data:image/jpeg;base64,'+data;await image.decode();s.canvas.getContext('2d').drawImage(image,0,0,s.canvas.width,s.canvas.height);return true;}", [D(("value", data))], frameTimeout.Token, true);
                }
                finally { _gate.Release(); }
                Interlocked.Exchange(ref _recordedMilliseconds, clock.ElapsedMilliseconds);
                var delay = 1000 / fps - (int)(clock.ElapsedMilliseconds - frameStart);
                if (delay > 0) await Task.Delay(delay, ct);
            }
            await _gate.WaitAsync(ct);
            try
            {
                using var finish = CancellationTokenSource.CreateLinkedTokenSource(ct);
                finish.CancelAfter(10000);
                var result = RemoteValue(await CallAsync(context, "async function(){const s=globalThis.__alcolerRecording;if(!s)throw Error('Recording context lost');if(s.recorder.state!=='inactive')s.recorder.stop();await s.finished;if(s.error)throw Error(s.error);const blob=new Blob(s.chunks,{type:s.recorder.mimeType});s.chunks=[];const data=await new Promise((resolve,reject)=>{const r=new FileReader();r.onload=()=>resolve(r.result);r.onerror=()=>reject(r.error);r.readAsDataURL(blob);});delete globalThis.__alcolerRecording;return data.slice(data.indexOf(',')+1);}", [], finish.Token, true));
                _recordingMedia = DecodeBounded(result.GetString() ?? "", MaxMedia);
                _recordingState = "completed";
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _recordingState = "cancelled"; }
        catch (Exception ex) { _recordingError = ex.Message; _recordingState = "failed"; }
        finally
        {
            if (!_lifetime.IsCancellationRequested && _recordingState != "completed")
            {
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                cleanup.CancelAfter(2000);
                var entered = false;
                try
                {
                    await _gate.WaitAsync(cleanup.Token);
                    entered = true;
                    await CallAsync(context, "function(){const s=globalThis.__alcolerRecording;if(s){s.stream.getTracks().forEach(t=>t.stop());if(s.recorder.state!=='inactive')s.recorder.stop();s.chunks=[];delete globalThis.__alcolerRecording;}return true;}", [], cleanup.Token, true);
                }
                catch (Exception) { }
                finally { if (entered) _gate.Release(); }
            }
        }
    }
}
