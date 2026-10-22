using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Browser;

namespace Alcoler.Browser.Automation;

public sealed partial class CdpBrowserAutomation
{
    private async Task<BrowserActionResult> SnapshotAsync(JsonElement input, bool dom, CancellationToken ct)
    {
        var context = await ContextForInputAsync(input, ct);
        var generation = _generation;
        var sequence = Interlocked.Increment(ref _snapshotSequence);
        var maxChars = Integer(input, "maxChars", MaxText, 1, MaxText);
        var maxDepth = Integer(input, "maxDepth", 30, 0, 100);
        if (dom)
        {
            var locator = Locator(input, false);
            var scope = locator is null ? "document" : $"__alcolerAutomation.select({await RuntimeLocatorAsync(input, context, ct)})";
            var value = await EvaluateValueAsync(context, $"__alcolerAutomation.snapshot({generation * 100000L + sequence},1000,{scope},{(Boolean(input, "includeHidden") ? "true" : "false")})", ct);
            _references.Clear();
            foreach (var item in value.EnumerateArray())
                if (String(item, "ref") is { } reference) _references[reference] = new(generation, context.FrameId, null);
            var nodes = new List<JsonElement>();
            var used = 2;
            foreach (var item in value.EnumerateArray())
            {
                if (used + item.GetRawText().Length + 1 > maxChars) break;
                nodes.Add(item.Clone());
                used += item.GetRawText().Length + 1;
            }
            return Structured(BrowserJson.Element(nodes));
        }
        var frames = new List<string> { context.FrameId };
        var scoped = Locator(input, false) is not null;
        if (!scoped)
        {
            var frameTree = await SendAsync("Page.getFrameTree", null, ct);
            void Collect(JsonElement branch)
            {
                var frame = branch.GetProperty("frame");
                if (String(frame, "id") is { } id && !frames.Contains(id)) frames.Add(id);
                if (branch.TryGetProperty("childFrames", out var children)) foreach (var child in children.EnumerateArray()) Collect(child);
            }
            Collect(frameTree.GetProperty("frameTree"));
        }
        var nodesById = new Dictionary<string, JsonElement>();
        var childIds = new HashSet<string>();
        var nodeFrames = new Dictionary<string, string>();
        foreach (var frame in frames.Take(64))
        {
            var frameContext = frame == context.FrameId ? context : await ContextAsync(frame, ct);
            JsonElement tree;
            if (scoped)
            {
                var id = await ResolveObjectAsync(input, frameContext, ct);
                try
                {
                    var description = await SendAsync("DOM.describeNode", D(("objectId", id)), ct, frameContext.Session);
                    tree = await SendAsync("Accessibility.getFullAXTree", D(("depth", maxDepth), ("frameId", frame)), ct, frameContext.Session);
                    var backend = description.GetProperty("node").GetProperty("backendNodeId").GetInt32();
                    var selected = tree.GetProperty("nodes").EnumerateArray().FirstOrDefault(n => n.TryGetProperty("backendDOMNodeId", out var b) && b.GetInt32() == backend);
                    if (selected.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Element is not present in the accessibility tree; use dom_snapshot.");
                    var reachable = new HashSet<string>();
                    var all = tree.GetProperty("nodes").EnumerateArray().ToDictionary(n => RequiredString(n, "nodeId"));
                    void Visit(JsonElement n)
                    {
                        if (!reachable.Add(RequiredString(n, "nodeId"))) return;
                        if (n.TryGetProperty("childIds", out var cs)) foreach (var c in cs.EnumerateArray()) if (all.TryGetValue(c.GetString()!, out var child)) Visit(child);
                    }
                    Visit(selected);
                    tree = BrowserJson.Element(D(("nodes", all.Where(p => reachable.Contains(p.Key)).Select(p => p.Value).ToArray())));
                }
                finally { await ReleaseAsync(id, frameContext.Session, ct); }
            }
            else tree = await SendAsync("Accessibility.getFullAXTree", D(("depth", maxDepth), ("frameId", frame)), ct, frameContext.Session);
            foreach (var node in tree.GetProperty("nodes").EnumerateArray())
            {
                var key = frame + ":" + RequiredString(node, "nodeId");
                nodesById[key] = node.Clone();
                nodeFrames[key] = frame;
                if (node.TryGetProperty("childIds", out var children)) foreach (var child in children.EnumerateArray()) childIds.Add(frame + ":" + child.GetString());
            }
        }
        _references.Clear();
        var text = new StringBuilder();
        var output = new List<Dictionary<string, object?>>();
        var visited = new HashSet<string>();
        var structuredLength = 100;
        var truncated = false;
        var index = 0;
        void Append(string key, JsonElement node, int depth)
        {
            var nodeFrame = nodeFrames[key];
            if (depth > maxDepth || text.Length >= maxChars || !visited.Add(key)) return;
            var ignored = Boolean(node, "ignored");
            var role = AxValue(node, "role");
            var name = AxValue(node, "name");
            var meaningful = !ignored && role is not ("none" or "generic" or "InlineTextBox");
            if (Boolean(input, "includeHidden") || meaningful)
            {
                var reference = node.TryGetProperty("backendDOMNodeId", out var backend) ? $"a{generation}_{sequence}_{++index}" : null;
                if (reference is not null) _references[reference] = new(generation, String(node, "frameId") ?? nodeFrame, backend.GetInt32());
                var states = new Dictionary<string, object?>();
                if (node.TryGetProperty("properties", out var properties)) foreach (var p in properties.EnumerateArray())
                {
                    var stateName = String(p, "name");
                    if (stateName is not null && p.TryGetProperty("value", out var state) && state.TryGetProperty("value", out var stateValue)) states[stateName] = stateValue.Clone();
                }
                var line = new StringBuilder().Append(' ', Math.Min(depth, 100) * 2).Append(role);
                if (name.Length > 0) line.Append(' ').Append(BrowserJson.Serialize(Truncate(name, 1000)));
                if (reference is not null) line.Append(" [ref=").Append(reference).Append(']');
                foreach (var state in states.Where(p => p.Key is "checked" or "disabled" or "expanded" or "selected" or "focused" or "required" or "level" or "readonly" or "pressed")) line.Append(' ').Append(state.Key).Append('=').Append(state.Value);
                var value = AxValue(node, "value");
                if (value.Length > 0) line.Append(" value=").Append(BrowserJson.Serialize(Truncate(value, 1000)));
                var entry = D(("ref", reference), ("role", role), ("name", Truncate(name, 1000)), ("value", Truncate(value, 1000)), ("depth", depth), ("states", states));
                var entryLength = BrowserJson.Serialize(entry).Length + 1;
                if (text.Length + line.Length + 1 > maxChars || structuredLength + entryLength > MaxText)
                {
                    truncated = true;
                    if (reference is not null) _references.TryRemove(reference, out _);
                    return;
                }
                text.AppendLine(line.ToString());
                output.Add(entry);
                structuredLength += entryLength;
            }
            if (node.TryGetProperty("childIds", out var children)) foreach (var child in children.EnumerateArray())
                if (nodesById.TryGetValue(nodeFrame + ":" + child.GetString(), out var next)) Append(nodeFrame + ":" + child.GetString(), next, depth + (meaningful ? 1 : 0));
        }
        foreach (var root in nodesById.Where(n => !childIds.Contains(n.Key))) Append(root.Key, root.Value, 0);
        return new(true, Text: Truncate(text.ToString(), maxChars), StructuredOutput: BrowserJson.Serialize(D(("generation", generation), ("nodes", output), ("truncated", truncated))));
    }

    private static string AxValue(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var p) || !p.TryGetProperty("value", out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private async Task<BrowserActionResult> ScreenshotAsync(JsonElement input, CancellationToken ct)
    {
        Dictionary<string, object?>? clip = null;
        var fullPage = Boolean(input, "fullPage");
        if (Locator(input, false) is not null)
        {
            if (fullPage || input.TryGetProperty("clip", out _)) throw new ArgumentException("Element screenshots cannot also specify fullPage or clip.");
            await ActionableAsync(input, true, ct, false);
            var box = RemoteValue(await WithElementAsync(input, "function(){const r=this.getBoundingClientRect();return {x:r.x+scrollX,y:r.y+scrollY,width:r.width,height:r.height};}", [], ct, true));
            var context = await ContextForInputAsync(input, ct);
            var offset = await FrameOffsetAsync(context.FrameId, ct);
            clip = ScreenshotClip(Number(box, "x") + offset.X, Number(box, "y") + offset.Y, Number(box, "width"), Number(box, "height"));
        }
        else if (input.TryGetProperty("clip", out var explicitClip))
        {
            if (fullPage) throw new ArgumentException("fullPage and clip are mutually exclusive.");
            clip = ScreenshotClip(Number(explicitClip, "x"), Number(explicitClip, "y"), Number(explicitClip, "width"), Number(explicitClip, "height"));
        }
        else if (fullPage)
        {
            var metrics = await SendAsync("Page.getLayoutMetrics", null, ct);
            var size = metrics.TryGetProperty("cssContentSize", out var css) ? css : metrics.GetProperty("contentSize");
            clip = ScreenshotClip(0, 0, Number(size, "width"), Number(size, "height"));
        }
        else if ((long)_viewportWidth * _viewportHeight > 16000000) throw new InvalidOperationException("Screenshot exceeds 16 million pixels.");
        var parameters = D(("format", "png"), ("fromSurface", true), ("captureBeyondViewport", clip is not null));
        if (clip is not null) parameters["clip"] = clip;
        var result = await SendAsync("Page.captureScreenshot", parameters, ct);
        var png = DecodeBounded(RequiredString(result, "data"), MaxMedia);
        return new(true, ScreenshotPng: png);
    }

    private static Dictionary<string, object?> ScreenshotClip(double x, double y, double width, double height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || width > 32767 || height > 32767 || width * height > 16000000)
            throw new ArgumentException("Screenshot clip must be positive and at most 16 million CSS pixels.");
        return D(("x", x), ("y", y), ("width", width), ("height", height), ("scale", 1));
    }

    private async Task<BrowserActionResult> DialogAsync(JsonElement input, CancellationToken ct)
    {
        var operation = String(input, "operation") ?? "status";
        if (operation == "status") return Structured(BrowserJson.Element(D(("open", _dialog is not null), ("dialog", _dialog))));
        if (operation is not ("accept" or "dismiss")) throw new ArgumentException("Dialog operation must be status, accept, or dismiss.");
        if (_dialog is null) throw new InvalidOperationException("No JavaScript dialog is open.");
        _sideEffect = true;
        await SendAsync("Page.handleJavaScriptDialog", D(("accept", operation == "accept"), ("promptText", String(input, "text") ?? "")), ct);
        _dialog = null;
        return new(true);
    }

    private async Task<BrowserActionResult> DownloadMediaAsync(JsonElement input, CancellationToken ct)
    {
        var context = await ContextAsync(null, ct);
        var url = RequiredString(input, "url");
        var result = await CallAsync(context, """
            async function(url,maxBytes) {
              const u=new URL(url,location.href);
              if(!['http:','https:','blob:','data:'].includes(u.protocol))throw Error('Media URL protocol is not supported');
              const response=await fetch(u.href,{credentials:'include'});
              if(!response.ok)throw Error('Media fetch failed: '+response.status);
              if(Number(response.headers.get('content-length'))>maxBytes)throw Error('Media exceeds size limit');
              const reader=response.body?.getReader();
              let blob;
              if(reader){const chunks=[];let size=0;try{while(true){const {done,value}=await reader.read();if(done)break;size+=value.byteLength;if(size>maxBytes)throw Error('Media exceeds size limit');chunks.push(value);}}finally{await reader.cancel();}blob=new Blob(chunks,{type:response.headers.get('content-type')||'application/octet-stream'});}
              else {blob=await response.blob();if(blob.size>maxBytes)throw Error('Media exceeds size limit');}
              const data=await new Promise((resolve,reject)=>{const r=new FileReader();r.onload=()=>resolve(r.result);r.onerror=()=>reject(r.error);r.readAsDataURL(blob);});
              return {type:blob.type,data:data.slice(data.indexOf(',')+1)};
            }
            """, [D(("value", url)), D(("value", MaxMedia))], ct, true);
        var value = RemoteValue(result);
        var data = DecodeBounded(RequiredString(value, "data"), MaxMedia);
        var fileName = SafeFileName(String(input, "fileName") ?? "media.bin");
        return new(true, Media: data, MediaType: String(value, "type") ?? "application/octet-stream", MediaFileName: fileName);
    }

    private static byte[] DecodeBounded(string base64, int maxBytes)
    {
        if (base64.Length > (maxBytes + 2L) / 3 * 4) throw new InvalidOperationException("Media exceeds the byte limit.");
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length > maxBytes) throw new InvalidOperationException("Media exceeds the byte limit.");
        return bytes;
    }
    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        if (name.Length == 0 || name.Length > 128 || name.Any(c => c < 32 || "<>:\"|?*".Contains(c))) throw new ArgumentException("Invalid media file name.");
        return name;
    }
}
