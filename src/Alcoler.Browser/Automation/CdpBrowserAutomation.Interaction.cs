using System.Text.Json;
using Alcoler.Contracts.Browser;

namespace Alcoler.Browser.Automation;

public sealed partial class CdpBrowserAutomation
{
    private async Task<BrowserActionResult> QueryAsync(string command, JsonElement input, CancellationToken ct)
    {
        var context = await ContextForInputAsync(input, ct);
        if (command is "count" or "is_visible" or "is_enabled")
        {
            var locator = await RuntimeLocatorAsync(input, context, ct);
            var expression = command switch
            {
                "count" => $"__alcolerAutomation.resolve({locator}).length",
                "is_visible" => $"(() => {{const es=__alcolerAutomation.resolve({locator});if(es.length>1)throw Error('Strict locator matched '+es.length+' elements');return es.length===1 && __alcolerAutomation.visible(es[0]);}})()",
                _ => $"__alcolerAutomation.enabled(__alcolerAutomation.select({locator}))"
            };
            return Structured(await EvaluateValueAsync(context, expression, ct));
        }
        var function = command switch
        {
            "inner_text" => "function(){return this.innerText ?? '';}",
            "text_content" => "function(){return this.textContent;}",
            "get_attribute" => "function(name){return this.getAttribute(name);}",
            _ => "function(){return globalThis.__alcolerAutomation.inspect.call(this);}"
        };
        object[] args = command == "get_attribute" ? [D("value", RequiredString(input, "name"))] : [];
        return Structured(RemoteValue(await WithElementAsync(input, function, args, ct, true)));
    }

    private async Task<BrowserActionResult> InteractAsync(string command, JsonElement input, CancellationToken ct)
    {
        if (command == "press")
        {
            if (Locator(input, false) is not null)
                await WithElementAsync(input, "function(){if(!globalThis.__alcolerAutomation.visible(this)||!globalThis.__alcolerAutomation.enabled(this))throw Error('Element is not visible and enabled');this.focus();return document.activeElement===this || this.getRootNode().activeElement===this;}", [], ct, true);
            await PressAsync(RequiredString(input, "key"), ct);
            return new(true);
        }
        if (command == "scroll")
        {
            var point = await PointAsync(input, false, ct);
            _sideEffect = true;
            await SendAsync("Input.dispatchMouseEvent", D(("type", "mouseWheel"), ("x", point.X), ("y", point.Y), ("deltaX", Number(input, "deltaX")), ("deltaY", Number(input, "deltaY"))), ct);
            return new(true);
        }
        if (command == "drag") return await DragAsync(input, ct);
        if (command is "fill" or "type")
        {
            await ActionableAsync(input, false, ct, true);
            var focus = RemoteValue(await WithElementAsync(input, "function(){this.focus();return globalThis.__alcolerAutomation.inspect.call(this);}", [], ct, true));
            if (!Boolean(focus, "editable")) throw new InvalidOperationException("Element is not editable.");
            var text = RequiredString(input, "text");
            if (text.Length > 1000000) throw new ArgumentException("Text exceeds one million characters.");
            if (command == "fill")
            {
                await PressAsync("Control+A", ct);
                if (text.Length == 0) await PressAsync("Backspace", ct);
                else { _sideEffect = true; await SendAsync("Input.insertText", D("text", text), ct); }
            }
            else
            {
                var delay = Integer(input, "delayMs", 0, 0, 1000);
                if (delay == 0) { _sideEffect = true; await SendAsync("Input.insertText", D("text", text), ct); }
                else foreach (var rune in text.EnumerateRunes())
                {
                    _sideEffect = true;
                    await SendAsync("Input.insertText", D("text", rune.ToString()), ct);
                    await Task.Delay(delay, ct);
                }
            }
            if (Boolean(input, "submit")) await PressAsync("Enter", ct);
            return new(true);
        }
        if (command == "select_option")
        {
            await ActionableAsync(input, false, ct, false);
            var values = input.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array ? v : throw new ArgumentException("'values' must be an array of option values.");
            foreach (var value in values.EnumerateArray()) if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Option values must be strings.");
            _sideEffect = true;
            var result = await WithElementAsync(input, "function(values){if(!(this instanceof HTMLSelectElement))throw Error('Element is not a select');if(!this.multiple && values.length>1)throw Error('Select does not allow multiple values');for(const value of values)if(!Array.from(this.options).some(o=>o.value===value&&!o.disabled))throw Error('Option missing or disabled: '+value);for(const o of this.options)o.selected=values.includes(o.value);this.dispatchEvent(new Event('input',{bubbles:true}));this.dispatchEvent(new Event('change',{bubbles:true}));return Array.from(this.selectedOptions).map(o=>o.value);}", [D("value", values)], ct, true);
            return Structured(RemoteValue(result));
        }
        var force = Boolean(input, "force");
        var target = await PointAsync(input, true, ct, force);
        if (command is "check" or "uncheck")
        {
            var state = RemoteValue(await WithElementAsync(input, "function(){if(!this.matches('input[type=checkbox],input[type=radio]'))throw Error('check requires a checkbox or radio input');return this.checked;}", [], ct, true));
            if (state.GetBoolean() == (command == "check")) return new(true);
        }
        var modifiers = Modifiers(input);
        var button = String(input, "button") ?? "left";
        ValidateButton(button);
        _sideEffect = true;
        await MouseAsync("mouseMoved", target, "none", 0, modifiers, ct);
        if (command == "hover") return new(true);
        var clicks = command == "dblclick" ? 2 : 1;
        for (var i = 1; i <= clicks; i++)
        {
            await MouseAsync("mousePressed", target, button, i, modifiers, ct);
            await MouseAsync("mouseReleased", target, button, i, modifiers, ct);
        }
        if (command is "check" or "uncheck")
        {
            var state = RemoteValue(await WithElementAsync(input, "function(){return this.checked;}", [], ct, true));
            if (state.GetBoolean() != (command == "check")) throw new InvalidOperationException("Click did not produce the requested checked state.");
        }
        return new(true);
    }

    private async Task<Point> PointAsync(JsonElement input, bool actionability, CancellationToken ct, bool force = false)
    {
        if (input.TryGetProperty("x", out _) && input.TryGetProperty("y", out _)) return new(Number(input, "x"), Number(input, "y"));
        if (Locator(input, false) is null)
        {
            if (actionability) throw new ArgumentException("A locator or x/y coordinates are required.");
            return new(_viewportWidth / 2d, _viewportHeight / 2d);
        }
        if (actionability) return await ActionableAsync(input, force, ct, true);
        var info = RemoteValue(await WithElementAsync(input, "function(){return globalThis.__alcolerAutomation.inspect.call(this);}", [], ct, true));
        var context = await ContextForInputAsync(input, ct);
        var offset = await FrameOffsetAsync(context.FrameId, ct);
        return new(Number(info, "x") + offset.X, Number(info, "y") + offset.Y);
    }

    private async Task<Point> ActionableAsync(JsonElement input, bool force, CancellationToken ct, bool hitTest)
    {
        var context = await ContextForInputAsync(input, ct);
        var id = await ResolveObjectAsync(input, context, ct);
        try
        {
            await CallObjectAsync(context, id, "function(){this.scrollIntoView({block:'center',inline:'center',behavior:'instant'});return true;}", [], ct, true);
            JsonElement? previous = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var info = RemoteValue(await CallObjectAsync(context, id, "function(){return globalThis.__alcolerAutomation.inspect.call(this);}", [], ct, true));
                if (!Boolean(info, "connected")) throw new InvalidOperationException("Element is detached.");
                var stable = previous is { } old && Math.Abs(Number(old, "left") - Number(info, "left")) < .25 && Math.Abs(Number(old, "top") - Number(info, "top")) < .25 && Math.Abs(Number(old, "width") - Number(info, "width")) < .25 && Math.Abs(Number(old, "height") - Number(info, "height")) < .25;
                if (Boolean(info, "visible") && (force || Boolean(info, "enabled")) && (force || !hitTest || Boolean(info, "hit")) && (force || stable))
                {
                    var offset = await FrameOffsetAsync(context.FrameId, ct);
                    var point = new Point(Number(info, "x") + offset.X, Number(info, "y") + offset.Y);
                    if (force || !hitTest || await FrameHitTestAsync(context.FrameId, point, ct)) return point;
                }
                previous = info;
                await Task.Delay(32, ct);
            }
        }
        finally { await ReleaseAsync(id, context.Session, ct); }
    }

    private const string FrameOffsetFunction = """
        function(){
          const r=this.getBoundingClientRect();
          if(!globalThis.__alcolerAutomation.visible(this))throw Error('Frame is not visible');
          const transforms=[];
          for(let e=this;e;e=e.assignedSlot || e.parentElement || e.getRootNode().host){
            const s=getComputedStyle(e);
            transforms.push({matrix:Array.from(new DOMMatrixReadOnly(s.transform==='none'?undefined:s.transform).toFloat64Array()),rotate:s.rotate,scale:s.scale,perspective:s.perspective});
          }
          if(Math.abs(r.width-this.offsetWidth)>1 || Math.abs(r.height-this.offsetHeight)>1)throw Error('Transformed frame input is not supported');
          return {x:r.left+this.clientLeft,y:r.top+this.clientTop,transforms};
        }
        """;

    private static void ValidateFrameTranslation(JsonElement box)
    {
        foreach (var transform in box.GetProperty("transforms").EnumerateArray())
        {
            var matrix = transform.GetProperty("matrix");
            if (matrix.GetArrayLength() != 16) throw new InvalidOperationException("Transformed frame input is not supported.");
            for (var i = 0; i < 16; i++)
            {
                var value = matrix[i].GetDouble();
                var expected = i is 0 or 5 or 10 or 15 ? 1d : 0d;
                if (!double.IsFinite(value) || i is not (12 or 13 or 14) && Math.Abs(value - expected) > 1e-9)
                    throw new InvalidOperationException("Transformed frame input is not supported.");
            }
            if (String(transform, "rotate") is not (null or "none") || String(transform, "scale") is not (null or "none") || String(transform, "perspective") is not (null or "none"))
                throw new InvalidOperationException("Transformed frame input is not supported.");
        }
    }

    private async Task<Point> FrameOffsetAsync(string frameId, CancellationToken ct)
    {
        if (frameId == _mainFrame) return new(0, 0);
        var ancestors = await FrameAncestorsAsync(frameId, ct);
        double x = 0, y = 0;
        foreach (var (frame, owner) in ancestors.AsEnumerable().Reverse())
        {
            var context = await ContextAsync(owner.ParentId, ct);
            var backendId = owner.BackendId ?? (await SendAsync("DOM.getFrameOwner", D("frameId", frame), ct, context.Session)).GetProperty("backendNodeId").GetInt32();
            var resolved = await SendAsync("DOM.resolveNode", D("backendNodeId", backendId, "executionContextId", context.Id), ct, context.Session);
            var objectId = RequiredString(resolved.GetProperty("object"), "objectId");
            try
            {
                var box = RemoteValue(await CallObjectAsync(context, objectId, FrameOffsetFunction, [], ct, true));
                ValidateFrameTranslation(box);
                x += Number(box, "x"); y += Number(box, "y");
            }
            finally { await ReleaseAsync(objectId, context.Session, ct); }
        }
        return new(x, y);
    }

    private async Task<bool> FrameHitTestAsync(string frameId, Point point, CancellationToken ct)
    {
        if (frameId == _mainFrame) return true;
        foreach (var (frame, owner) in await FrameAncestorsAsync(frameId, ct))
        {
            var context = await ContextAsync(owner.ParentId, ct);
            var offset = await FrameOffsetAsync(owner.ParentId, ct);
            var backendId = owner.BackendId ?? (await SendAsync("DOM.getFrameOwner", D("frameId", frame), ct, context.Session)).GetProperty("backendNodeId").GetInt32();
            var resolved = await SendAsync("DOM.resolveNode", D("backendNodeId", backendId, "executionContextId", context.Id), ct, context.Session);
            var objectId = RequiredString(resolved.GetProperty("object"), "objectId");
            try
            {
                var hit = RemoteValue(await CallObjectAsync(context, objectId, "function(x,y){let hit=document.elementFromPoint(x,y);while(hit?.shadowRoot){const next=hit.shadowRoot.elementFromPoint(x,y);if(!next||next===hit)break;hit=next;}return hit===this;}", [D("value", point.X - offset.X), D("value", point.Y - offset.Y)], ct, true));
                if (!hit.GetBoolean()) return false;
            }
            finally { await ReleaseAsync(objectId, context.Session, ct); }
        }
        return true;
    }

    private async Task<List<(string Frame, FrameOwner Owner)>> FrameAncestorsAsync(string frameId, CancellationToken ct)
    {
        var result = new List<(string Frame, FrameOwner Owner)>();
        var visited = new HashSet<string>();
        while (frameId != _mainFrame)
        {
            if (!visited.Add(frameId) || visited.Count > 100) throw new InvalidOperationException("Invalid frame ancestry.");
            if (!_frameOwners.TryGetValue(frameId, out var owner))
            {
                void Collect(JsonElement branch)
                {
                    if (!branch.TryGetProperty("childFrames", out var children)) return;
                    var parent = RequiredString(branch.GetProperty("frame"), "id");
                    foreach (var child in children.EnumerateArray())
                    {
                        var id = RequiredString(child.GetProperty("frame"), "id");
                        _frameOwners.TryAdd(id, new(parent, null));
                        Collect(child);
                    }
                }
                var tree = await SendAsync("Page.getFrameTree", null, ct);
                Collect(tree.GetProperty("frameTree"));
                if (!_frameOwners.TryGetValue(frameId, out owner))
                {
                    foreach (var session in _frameSessions.Values.Distinct())
                    {
                        var sessionTree = await SendAsync("Page.getFrameTree", null, ct, session);
                        Collect(sessionTree.GetProperty("frameTree"));
                        if (_frameOwners.TryGetValue(frameId, out owner)) break;
                    }
                }
                if (owner is null) throw new InvalidOperationException("Frame is no longer attached.");
            }
            result.Add((frameId, owner));
            frameId = owner.ParentId;
        }
        return result;
    }

    private async Task<BrowserActionResult> DragAsync(JsonElement input, CancellationToken ct)
    {
        var points = new List<Point>();
        if (input.TryGetProperty("path", out var path))
        {
            if (path.ValueKind != JsonValueKind.Array || path.GetArrayLength() < 2 || path.GetArrayLength() > 1000) throw new ArgumentException("Drag path requires 2 to 1000 points.");
            foreach (var p in path.EnumerateArray()) points.Add(new(Number(p, "x"), Number(p, "y")));
        }
        else
        {
            if (!input.TryGetProperty("from", out var from) || !input.TryGetProperty("to", out var to)) throw new ArgumentException("Drag requires from/to targets or a path.");
            var start = await PointAsync(from, true, ct);
            var end = await PointAsync(to, true, ct);
            var steps = Integer(input, "steps", 12, 1, 1000);
            for (var i = 0; i <= steps; i++) points.Add(new(start.X + (end.X - start.X) * i / steps, start.Y + (end.Y - start.Y) * i / steps));
        }
        var button = String(input, "button") ?? "left";
        ValidateButton(button);
        _sideEffect = true;
        await MouseAsync("mouseMoved", points[0], "none", 0, 0, ct);
        await MouseAsync("mousePressed", points[0], button, 1, 0, ct);
        foreach (var point in points.Skip(1))
        {
            await SendAsync("Input.dispatchMouseEvent", D(("type", "mouseMoved"), ("x", point.X), ("y", point.Y), ("button", button), ("buttons", ButtonFlag(button))), ct);
            await Task.Delay(16, ct);
        }
        await MouseAsync("mouseReleased", points[^1], button, 1, 0, ct);
        return new(true);
    }

    private Task<JsonElement> MouseAsync(string type, Point point, string button, int count, int modifiers, CancellationToken ct) =>
        SendAsync("Input.dispatchMouseEvent", D(("type", type), ("x", point.X), ("y", point.Y), ("button", button), ("buttons", type == "mousePressed" ? ButtonFlag(button) : 0), ("clickCount", count), ("modifiers", modifiers)), ct);

    private async Task PressAsync(string shortcut, CancellationToken ct)
    {
        var key = shortcut;
        var modifiers = 0;
        var held = new List<(string Key, string Code, int Vk, int Flag)>();
        var start = 0;
        for (var i = 0; i <= shortcut.Length; i++)
        {
            if (i < shortcut.Length && shortcut[i] != '+') continue;
            var segment = shortcut.AsSpan(start, i - start);
            if (i == shortcut.Length)
            {
                key = segment.ToString();
                if (key.Length == 0 && shortcut.Length > 0 && shortcut[^1] == '+') key = "+";
                break;
            }
            var modifier = Modifier(segment);
            modifiers |= modifier.Flag;
            held.Add(modifier);
            start = i + 1;
        }
        var (normalized, code, vk, text) = Key(key, modifiers);
        _sideEffect = true;
        foreach (var m in held) await KeyEventAsync("rawKeyDown", m.Key, m.Code, m.Vk, modifiers, null, ct);
        await KeyEventAsync(text is null ? "rawKeyDown" : "keyDown", normalized, code, vk, modifiers, text, ct);
        await KeyEventAsync("keyUp", normalized, code, vk, modifiers, null, ct);
        for (var i = held.Count - 1; i >= 0; i--)
        {
            var m = held[i];
            modifiers &= ~m.Flag;
            await KeyEventAsync("keyUp", m.Key, m.Code, m.Vk, modifiers, null, ct);
        }
    }

    private static (string Key, string Code, int Vk, int Flag) Modifier(ReadOnlySpan<char> modifier)
    {
        if (modifier.Equals("control", StringComparison.OrdinalIgnoreCase) || modifier.Equals("ctrl", StringComparison.OrdinalIgnoreCase)
            || modifier.Equals("controlorcommand", StringComparison.OrdinalIgnoreCase))
            return ("Control", "ControlLeft", 17, 2);
        if (modifier.Equals("shift", StringComparison.OrdinalIgnoreCase)) return ("Shift", "ShiftLeft", 16, 8);
        if (modifier.Equals("alt", StringComparison.OrdinalIgnoreCase)) return ("Alt", "AltLeft", 18, 1);
        if (modifier.Equals("meta", StringComparison.OrdinalIgnoreCase) || modifier.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || modifier.Equals("command", StringComparison.OrdinalIgnoreCase))
            return ("Meta", "MetaLeft", 91, 4);
        throw new ArgumentException($"Unknown keyboard modifier '{modifier.ToString()}'.");
    }

    private Task<JsonElement> KeyEventAsync(string type, string key, string code, int vk, int modifiers, string? text, CancellationToken ct)
    {
        var p = D(("type", type), ("key", key), ("code", code), ("windowsVirtualKeyCode", vk), ("nativeVirtualKeyCode", vk), ("modifiers", modifiers));
        if (text is not null) p["text"] = text;
        return SendAsync("Input.dispatchKeyEvent", p, ct);
    }

    private static (string Key, string Code, int Vk, string? Text) Key(string key, int modifiers)
    {
        var normalized = key switch { "Return" => "Enter", "Esc" => "Escape", "Space" => " ", "Del" => "Delete", "Left" => "ArrowLeft", "Right" => "ArrowRight", "Up" => "ArrowUp", "Down" => "ArrowDown", _ => key };
        var special = normalized switch
        {
            "Enter" => ("Enter", 13), "Tab" => ("Tab", 9), "Backspace" => ("Backspace", 8), "Delete" => ("Delete", 46),
            "Escape" => ("Escape", 27), "ArrowLeft" => ("ArrowLeft", 37), "ArrowRight" => ("ArrowRight", 39), "ArrowUp" => ("ArrowUp", 38), "ArrowDown" => ("ArrowDown", 40),
            "Home" => ("Home", 36), "End" => ("End", 35), "PageUp" => ("PageUp", 33), "PageDown" => ("PageDown", 34), "Insert" => ("Insert", 45),
            "Control" => ("ControlLeft", 17), "Alt" => ("AltLeft", 18), "Shift" => ("ShiftLeft", 16), "Meta" => ("MetaLeft", 91),
            _ => ("", 0)
        };
        if (special.Item2 != 0) return (normalized, special.Item1, special.Item2, normalized == "Enter" && (modifiers & 7) == 0 ? "\r" : null);
        if (normalized.Length > 1 && normalized[0] == 'F' && int.TryParse(normalized.AsSpan(1), out var f) && f is >= 1 and <= 24) return (normalized, normalized, 111 + f, null);
        if (normalized.Length != 1) throw new ArgumentException($"Unsupported key '{key}'.");
        var c = normalized[0];
        var code = char.IsAsciiLetter(c) ? "Key" + char.ToUpperInvariant(c) : char.IsAsciiDigit(c) ? "Digit" + c : c == ' ' ? "Space" : "";
        var vk = char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : c == ' ' ? 32 : 0;
        return (normalized, code, vk, (modifiers & 7) == 0 ? normalized : null);
    }

    private static int Modifiers(JsonElement input)
    {
        if (!input.TryGetProperty("modifiers", out var list)) return 0;
        var flags = 0;
        foreach (var item in list.EnumerateArray()) flags |= item.GetString() switch { "Alt" => 1, "Control" => 2, "Meta" => 4, "Shift" => 8, _ => throw new ArgumentException("Invalid modifier.") };
        return flags;
    }
    private static void ValidateButton(string button) { if (button is not ("left" or "right" or "middle")) throw new ArgumentException("Mouse button must be left, right, or middle."); }
    private static int ButtonFlag(string button) => button switch { "left" => 1, "right" => 2, "middle" => 4, _ => 0 };
    private sealed record Point(double X, double Y);
}
