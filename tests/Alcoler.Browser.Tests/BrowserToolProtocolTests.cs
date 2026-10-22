using System.Text.Json;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Browser.Tests;

public sealed class BrowserToolProtocolTests
{
    private static readonly SessionId Session = new("sess_browser_protocol");
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
    private readonly FakeBrowserController _controller = new();
    private readonly FakeToolWorkspace _workspace = new();
    private readonly BrowserTool _tool;

    public BrowserToolProtocolTests() => _tool = new(_controller);

    public static TheoryData<string> GenericInputs => new()
    {
        """{"action":"capabilities"}""",
        """{"action":"tabs"}""",
        """{"action":"new_tab"}""",
        """{"action":"new_tab","url":"https://example.com","timeoutMs":1000}""",
        """{"action":"close_tab","tabId":"tab-1"}""",
        """{"action":"select_tab","tabId":"tab-1"}""",
        """{"action":"back","timeoutMs":1000}""",
        """{"action":"forward"}""",
        """{"action":"reload"}""",
        """{"action":"snapshot","maxChars":24000,"maxDepth":20,"includeHidden":false}""",
        """{"action":"dom_snapshot","locator":{"css":"main"}}""",
        """{"action":"dblclick","x":10.5,"y":20,"button":"left","modifiers":["Control"],"force":true}""",
        """{"action":"hover","ref":"e1"}""",
        """{"action":"fill","locator":{"label":"Email","exact":true},"text":"a@example.com","submit":true}""",
        """{"action":"drag","from":{"selector":"#a"},"to":{"x":20,"y":30},"steps":10,"button":"left"}""",
        """{"action":"drag","path":[{"x":1,"y":2},{"x":3,"y":4}]}""",
        """{"action":"check","selector":"#checkbox","force":false}""",
        """{"action":"uncheck","ref":"e2"}""",
        """{"action":"select_option","selector":"select","values":["a","b"]}""",
        """{"action":"count","locator":{"role":"button","name":"Save","nth":0,"visible":true,"frame":"iframe","hasText":"Save","and":[{"css":".primary"}],"or":[{"testId":"save"}]}}""",
        """{"action":"inner_text","selector":"main"}""",
        """{"action":"text_content","locator":{"text":"Hello"}}""",
        """{"action":"get_attribute","selector":"a","name":"href"}""",
        """{"action":"is_visible","ref":"e3"}""",
        """{"action":"is_enabled","selector":"button"}""",
        """{"action":"element_info","locator":{"placeholder":"Search"}}""",
        """{"action":"wait_for","selector":"#ready","state":"visible","timeoutMs":1000}""",
        """{"action":"wait_for","url":"https://example.com","state":"load"}""",
        """{"action":"wait_for","state":"domcontentloaded"}""",
        """{"action":"evaluate","expression":"(arg) => document.title + arg.suffix","arg":{"suffix":"!"}}""",
        """{"action":"dialog","operation":"status"}""",
        """{"action":"dialog","operation":"accept","text":"yes"}""",
        """{"action":"downloads"}""",
        """{"action":"download_media","url":"https://example.com/video.mp4","fileName":"video.mp4"}""",
        """{"action":"viewport","width":3840,"height":2160}""",
        """{"action":"visibility","visible":false}""",
        """{"action":"takeover"}""",
        """{"action":"resume"}""",
        """{"action":"finalize","keep":["tab-1","tab-2"],"release":true}""",
        """{"action":"record_start","maxDurationMs":90000,"fps":30}""",
        """{"action":"record_status"}""",
        """{"action":"record_cancel"}""",
        """{"action":"navigate","url":"about:blank","waitUntil":"commit","timeoutMs":60000}""",
        """{"action":"click","locator":{"role":"button","name":"Save"},"button":"right"}""",
        """{"action":"type","ref":"e4","text":"hello","delayMs":10}""",
        """{"action":"press","key":"Enter","selector":"#q"}""",
        """{"action":"scroll","deltaY":300,"x":10,"y":10}""",
        """{"action":"screenshot","clip":{"x":0,"y":1.5,"width":100,"height":200}}""",
        """{"action":"screenshot","locator":{"css":"main"}}""",
        """{"action":"read_text","selector":"main","maxChars":1000}""",
    };

    [Theory]
    [MemberData(nameof(GenericInputs))]
    public async Task ExtendedActions_PreserveExactInputAsDetachedGenericCommand(string json)
    {
        Assert.Null(await _tool.ValidateInputAsync(Input(json), CancellationToken.None));
        using (var document = JsonDocument.Parse(json))
        {
            await Execute(document.RootElement);
        }
        var command = Assert.IsType<BrowserCommandAction>(Assert.Single(_controller.Executed).Action);
        Assert.Equal(Input(json).GetProperty("action").GetString(), command.Command);
        Assert.Equal(json, command.Input.GetRawText());
        Assert.False(command.UserInitiated);
    }

    [Theory]
    [InlineData("""{"action":"navigate","url":"https://example.com"}""")]
    [InlineData("""{"action":"click","selector":"button"}""")]
    [InlineData("""{"action":"type","selector":"input","text":""}""")]
    [InlineData("""{"action":"screenshot"}""")]
    [InlineData("""{"action":"press","key":"Enter"}""")]
    [InlineData("""{"action":"scroll","deltaX":-1,"deltaY":2}""")]
    [InlineData("""{"action":"wait","milliseconds":1}""")]
    [InlineData("""{"action":"read_text"}""")]
    public async Task LegacyRecords_RetainTabRoutingWithoutUserInitiatedPrivilege(string json)
    {
        var withTab = json[..^1] + ",\"tabId\":\"tab:42\"}";
        Assert.Null(await _tool.ValidateInputAsync(Input(withTab), CancellationToken.None));
        await Execute(Input(withTab));
        var action = Assert.Single(_controller.Executed).Action;
        Assert.IsNotType<BrowserCommandAction>(action);
        Assert.Equal("tab:42", action.TabId);
        Assert.False(action.UserInitiated);
    }

    public static TheoryData<string> InvalidInputs => new()
    {
        """{"action":"navigate","url":"javascript:alert(1)"}""",
        """{"action":"navigate","url":"file:///C:/secret"}""",
        """{"action":"navigate","url":"data:text/html,test"}""",
        """{"action":"navigate","url":"ftp://example.com"}""",
        """{"action":"navigate","url":"/relative"}""",
        """{"action":"navigate","url":"https://user:secret@example.com"}""",
        """{"action":"navigate","url":"https://example.com/ bad"}""",
        """{"action":"navigate","url":"https://example.com","waitUntil":"networkidle"}""",
        """{"action":"wait_for","state":"networkidle"}""",
        """{"action":"tabs","url":"https://example.com"}""",
        """{"action":"tabs","unexpected":1}""",
        """{"action":"tabs","action":"reload"}""",
        """{"action":"tabs","tabId":"one","tabId":"two"}""",
        """{"action":"select_tab"}""",
        """{"action":"close_tab","tabId":""}""",
        """{"action":"tabs","tabId":"../../other"}""",
        """{"action":"resume","userInitiated":true}""",
        """{"action":"resume","force":true}""",
        """{"action":"click","selector":"button","ref":"e1"}""",
        """{"action":"click","x":1}""",
        """{"action":"click","x":1e309,"y":2}""",
        """{"action":"click","x":1000001,"y":2}""",
        """{"action":"click","x":1,"y":2,"locator":{"role":"button"}}""",
        """{"action":"click","selector":"button","button":"secondary"}""",
        """{"action":"click","selector":"button","modifiers":["Super"]}""",
        """{"action":"click","selector":"button","modifiers":["Alt","Alt"]}""",
        """{"action":"click","locator":{}}""",
        """{"action":"click","locator":{"name":"Save"}}""",
        """{"action":"click","locator":{"css":"button","unknown":true}}""",
        """{"action":"click","locator":{"css":"button","css":"a"}}""",
        """{"action":"click","locator":{"role":"button","nth":-1}}""",
        """{"action":"click","locator":{"role":"button","exact":"true"}}""",
        """{"action":"click","locator":{"and":[]}}""",
        """{"action":"fill","x":1,"y":2,"text":"hi"}""",
        """{"action":"type","selector":"input","text":"hi","submit":"true"}""",
        """{"action":"type","selector":"input","text":"hi","delayMs":1001}""",
        """{"action":"press","key":""}""",
        """{"action":"press","key":"Super+A"}""",
        """{"action":"press","key":"Control++"}""",
        """{"action":"press","key":"F25"}""",
        """{"action":"press","key":"UnknownKey"}""",
        """{"action":"scroll","deltaY":"500"}""",
        """{"action":"scroll","deltaY":500.5}""",
        """{"action":"scroll","deltaX":100001}""",
        """{"action":"wait","milliseconds":1.5}""",
        """{"action":"wait","milliseconds":"10"}""",
        """{"action":"wait","milliseconds":null}""",
        """{"action":"click","selector":"button","timeoutMs":0}""",
        """{"action":"click","selector":"button","timeoutMs":60001}""",
        """{"action":"click","selector":"button","force":1}""",
        """{"action":"snapshot","maxChars":24001}""",
        """{"action":"snapshot","maxDepth":101}""",
        """{"action":"snapshot","includeHidden":"yes"}""",
        """{"action":"drag"}""",
        """{"action":"drag","from":{"selector":"#a"}}""",
        """{"action":"drag","path":[{"x":0,"y":0}]}""",
        """{"action":"drag","path":[{"x":0,"y":0},{"x":1,"y":1,"z":2}]}""",
        """{"action":"drag","from":{"selector":"#a","extra":1},"to":{"selector":"#b"}}""",
        """{"action":"drag","from":{"selector":"#a"},"to":{"selector":"#b"},"path":[{"x":0,"y":0},{"x":1,"y":1}]}""",
        """{"action":"select_option","selector":"select","values":[]}""",
        """{"action":"select_option","selector":"select","values":[1]}""",
        """{"action":"get_attribute","selector":"a"}""",
        """{"action":"wait_for"}""",
        """{"action":"wait_for","selector":"#ready","url":"https://example.com"}""",
        """{"action":"wait_for","selector":"#ready","state":"load"}""",
        """{"action":"wait_for","state":"visible"}""",
        """{"action":"evaluate"}""",
        """{"action":"evaluate","expression":" "}""",
        """{"action":"dialog","operation":"open"}""",
        """{"action":"dialog","operation":"dismiss","text":"hi"}""",
        """{"action":"download_media","url":"about:blank"}""",
        """{"action":"download_media","url":"https://example.com","fileName":"../video.mp4"}""",
        """{"action":"download_media","url":"https://example.com","fileName":"CON.txt"}""",
        """{"action":"download_media","url":"https://example.com","fileName":"video.mp4."}""",
        """{"action":"viewport","width":319,"height":320}""",
        """{"action":"viewport","width":3841,"height":320}""",
        """{"action":"viewport","width":320,"height":2161}""",
        """{"action":"visibility","visible":"false"}""",
        """{"action":"finalize","keep":["tab-1","tab-1"]}""",
        """{"action":"finalize","keep":["../tab"]}""",
        """{"action":"record_start","maxDurationMs":90001}""",
        """{"action":"record_start","maxDurationMs":99}""",
        """{"action":"record_start","fps":31}""",
        """{"action":"screenshot","clip":{"x":-1,"y":0,"width":100,"height":100}}""",
        """{"action":"screenshot","clip":{"x":0,"y":0,"width":0,"height":100}}""",
        """{"action":"screenshot","clip":{"x":0,"y":0,"width":8193,"height":100}}""",
        """{"action":"screenshot","clip":{"x":0,"y":0,"width":8192,"height":8192}}""",
        """{"action":"screenshot","clip":{"x":0,"y":0,"width":100,"height":100,"extra":1}}""",
        """{"action":"screenshot","fullPage":true,"clip":{"x":0,"y":0,"width":100,"height":100}}""",
        """{"action":"screenshot","selector":"main","clip":{"x":0,"y":0,"width":100,"height":100}}""",
        """{"action":"screenshot","selector":"main","fullPage":true}""",
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task InvalidInput_IsRejectedByValidationAndExecutionWithoutControllerCall(string json)
    {
        Assert.NotNull(await _tool.ValidateInputAsync(Input(json), CancellationToken.None));
        var result = await Execute(Input(json));
        Assert.True(result.IsError);
        Assert.Empty(_controller.Executed);
        Assert.Empty(_workspace.SavedArtifacts);
    }

    [Theory]
    [InlineData("selector", 4097, "click")]
    [InlineData("ref", 129, "click")]
    [InlineData("text", 100001, "fill")]
    [InlineData("expression", 100001, "evaluate")]
    [InlineData("url", 8193, "navigate")]
    [InlineData("tabId", 129, "tabs")]
    [InlineData("key", 129, "press")]
    public async Task Strings_AreBounded(string field, int length, string action)
    {
        var input = new Dictionary<string, object?> { ["action"] = action, [field] = new string('a', length) };
        if (action == "fill") input["selector"] = "input";
        var issue = await _tool.ValidateInputAsync(JsonSerializer.SerializeToElement(input), CancellationToken.None);
        Assert.NotNull(issue);
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("Control+A")]
    [InlineData("Shift+ArrowDown")]
    [InlineData("F24")]
    [InlineData("Meta+Enter")]
    public async Task SupportedKeys_PreserveTheShortcut(string key)
    {
        var input = JsonSerializer.SerializeToElement(new { action = "press", key });
        Assert.Null(await _tool.ValidateInputAsync(input, CancellationToken.None));
        await Execute(input);
        Assert.Equal(key, Assert.IsType<BrowserPressAction>(Assert.Single(_controller.Executed).Action).Key);
    }

    [Fact]
    public async Task TotalInputSize_IsBoundedAcrossLocatorFields()
    {
        var locator = new Dictionary<string, object?>
        {
            ["or"] = Enumerable.Range(0, 8).Select(_ => new Dictionary<string, object?>
            {
                ["css"] = new string('c', 4096), ["role"] = new string('r', 4096), ["name"] = new string('n', 4096),
                ["text"] = new string('t', 4096), ["label"] = new string('l', 4096), ["placeholder"] = new string('p', 4096),
                ["testId"] = new string('i', 4096), ["hasText"] = new string('h', 4096),
            }),
        };
        var issue = await _tool.ValidateInputAsync(JsonSerializer.SerializeToElement(new { action = "click", locator }), CancellationToken.None);
        Assert.Equal("input_too_large", issue!.Code);
    }

    [Fact]
    public async Task LocatorNestingAndArrays_AreBounded()
    {
        object locator = new { css = "button" };
        for (var i = 0; i < 6; i++) locator = new { and = new[] { locator } };
        var deep = JsonSerializer.SerializeToElement(new { action = "click", locator });
        Assert.NotNull(await _tool.ValidateInputAsync(deep, CancellationToken.None));
        var wide = JsonSerializer.SerializeToElement(new
        {
            action = "click", locator = new { or = Enumerable.Range(0, 9).Select(i => new { css = ".item" + i }) },
        });
        Assert.NotNull(await _tool.ValidateInputAsync(wide, CancellationToken.None));
        var path = JsonSerializer.SerializeToElement(new
        {
            action = "drag", path = Enumerable.Range(0, 257).Select(i => new { x = i, y = i }),
        });
        Assert.NotNull(await _tool.ValidateInputAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task EvaluationArg_SizeAndDepthAreBounded()
    {
        var large = JsonSerializer.SerializeToElement(new { action = "evaluate", expression = "arg => arg", arg = new string('x', 32769) });
        Assert.NotNull(await _tool.ValidateInputAsync(large, CancellationToken.None));
        object nested = 1;
        for (var i = 0; i < 17; i++) nested = new { value = nested };
        var deep = JsonSerializer.SerializeToElement(new { action = "evaluate", expression = "arg => arg", arg = nested });
        Assert.NotNull(await _tool.ValidateInputAsync(deep, CancellationToken.None));
    }

    [Fact]
    public void Schema_IsClosedAndActionSpecific_WithLocatorAndPointDefinitions()
    {
        var schema = _tool.Contract.InputSchema;
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var names = schema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        var branches = schema.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(names.Length, branches.Length);
        foreach (var branch in branches)
        {
            Assert.False(branch.GetProperty("additionalProperties").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(branch.GetProperty("description").GetString()));
            Assert.Contains(branch.GetProperty("properties").GetProperty("action").GetProperty("const").GetString(), names);
        }
        foreach (var name in new[] { "locator", "target", "point", "clip" })
            Assert.False(schema.GetProperty("$defs").GetProperty(name).GetProperty("additionalProperties").GetBoolean());
        Assert.DoesNotContain("userInitiated", schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("networkidle", schema.GetProperty("properties").GetProperty("waitUntil").GetProperty("enum").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void Policy_NeverMarksCheckboxOrEvaluationWritesAsReadOnly()
    {
        Assert.False(_tool.Metadata.ReadOnly);
        Assert.False(_tool.Metadata.AllowedInPlanMode);
        Assert.False(typeof(IToolInputPolicy).IsAssignableFrom(typeof(BrowserTool)));
        Assert.Contains("untrusted", _tool.Contract.Description);
        Assert.Contains("Refs expire", _tool.Contract.Description);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("dom_snapshot")]
    [InlineData("read_text")]
    [InlineData("capabilities")]
    public async Task Output_PreservesBoundedTextAndStructuredSideChannel(string action)
    {
        const string structured = """{"refs":["e1","e2"],"count":2}""";
        _controller.ResultFactory = _ => new(true, Text: "Page contents including e1 and e2", TabId: "tab-7", StructuredOutput: structured);
        var result = await Execute(Input(JsonSerializer.Serialize(new { action })));
        var text = Assert.IsType<ToolResultText>(Assert.Single(result.Content)).Text;
        Assert.Contains("Page contents", text);
        Assert.Contains("e1", text);
        Assert.Contains("tab-7", text);
        Assert.Contains(structured, text);
        Assert.Equal(structured, result.StructuredOutput);
    }

    [Fact]
    public async Task LargeOutputs_AreBoundedWithoutInvalidJsonSideChannel()
    {
        _controller.ResultFactory = _ => new(true, Url: new string('u', 50_000), Text: new string('t', 80_000),
            StructuredOutput: JsonSerializer.Serialize(new { value = new string('s', 90_000) }));
        var result = await Execute(Input("""{"action":"snapshot"}"""));
        var text = Assert.IsType<ToolResultText>(Assert.Single(result.Content)).Text;
        Assert.InRange(text.Length, 1, 24_000);
        Assert.InRange(result.StructuredOutput!.Length, 1, 24_000);
        using var parsed = JsonDocument.Parse(result.StructuredOutput);
        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UncertainSideEffects_AreVisibleBeforeLargePageText(bool ok)
    {
        _controller.ResultFactory = _ => new(ok, Text: new string('x', 80_000), Error: "Timed out", SideEffectUncertain: true);
        var result = await Execute(Input("""{"action":"click","selector":"button"}"""));
        var text = Assert.IsType<ToolResultText>(Assert.Single(result.Content)).Text;
        Assert.Equal(!ok, result.IsError);
        Assert.Contains("SideEffectUncertain: true", text);
        Assert.Contains("before retrying", text);
        Assert.InRange(text.Length, 1, 24_000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Screenshot_ReturnsSavedPngArtifactWithText_NeverBase64(bool genericMedia)
    {
        _controller.ResultFactory = _ => genericMedia ? new(true, Media: Png, MediaType: "image/png") : new(true, ScreenshotPng: Png);
        var result = await Execute(Input("""{"action":"screenshot","fullPage":true}"""));
        Assert.False(result.IsError);
        var artifact = Assert.Single(_workspace.SavedArtifacts);
        Assert.Equal(Png, artifact.Data);
        Assert.Equal(Session, artifact.SessionId);
        Assert.Equal("image/png", artifact.MimeType);
        var media = Assert.IsType<ToolResultMedia>(result.Content[1]);
        Assert.Equal("image/png", media.MimeType);
        Assert.Equal(artifact.FileName, media.FileName);
        var text = Assert.IsType<ToolResultText>(result.Content[0]).Text;
        Assert.DoesNotContain(Convert.ToBase64String(Png), text);
        Assert.DoesNotContain("data:image", text);
    }

    [Theory]
    [InlineData("download_media", "video/mp4", "clip.mp4")]
    [InlineData("record_status", "video/webm", "recording.webm")]
    public async Task MediaResults_AreSavedAsWorkspaceArtifacts(string action, string mime, string name)
    {
        byte[] bytes = [1, 2, 3, 4];
        _controller.ResultFactory = _ => new(true, Text: "Media available", Media: bytes, MediaType: mime, MediaFileName: name);
        var input = action == "download_media" ? """{"action":"download_media","url":"https://example.com/clip.mp4"}""" : """{"action":"record_status"}""";
        var result = await Execute(Input(input));
        Assert.False(result.IsError);
        var saved = Assert.Single(_workspace.SavedArtifacts);
        Assert.Equal((name, mime), (saved.FileName, saved.MimeType));
        Assert.Equal(bytes, saved.Data);
        Assert.IsType<ToolResultText>(result.Content[0]);
        Assert.Equal(mime, Assert.IsType<ToolResultMedia>(result.Content[1]).MimeType);
    }

    [Fact]
    public async Task MissingScreenshotMedia_IsExplicitFailure()
    {
        var result = await Execute(Input("""{"action":"screenshot"}"""));
        Assert.True(result.IsError);
        Assert.Contains("no PNG media", Assert.IsType<ToolResultText>(Assert.Single(result.Content)).Text);
        Assert.Empty(_workspace.SavedArtifacts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20 * 1024 * 1024 + 1)]
    public async Task ScreenshotBytes_AreSizeBounded(int length)
    {
        _controller.ResultFactory = _ => new(true, ScreenshotPng: new byte[length]);
        var result = await Execute(Input("""{"action":"screenshot"}"""));
        Assert.True(result.IsError);
        Assert.Empty(_workspace.SavedArtifacts);
        Assert.Single(result.Content);
    }

    [Fact]
    public async Task ScreenshotBytes_MustBePng()
    {
        _controller.ResultFactory = _ => new(true, ScreenshotPng: [1, 2, 3]);
        var result = await Execute(Input("""{"action":"screenshot"}"""));
        Assert.True(result.IsError);
        Assert.Empty(_workspace.SavedArtifacts);
    }

    [Theory]
    [InlineData("video/webm", "../record.webm")]
    [InlineData("video/webm", "C:\\record.webm")]
    [InlineData("video/webm", "NUL.webm")]
    [InlineData("text/html\r\nInjected: true", "record.html")]
    [InlineData(null, "record.webm")]
    public async Task MediaMetadata_RejectsUnsafePathsAndMimeTypes(string? mime, string name)
    {
        _controller.ResultFactory = _ => new(true, Media: [1, 2, 3], MediaType: mime, MediaFileName: name);
        var result = await Execute(Input("""{"action":"record_status"}"""));
        Assert.True(result.IsError);
        Assert.Empty(_workspace.SavedArtifacts);
        Assert.Contains("media rejected", Assert.IsType<ToolResultText>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task MediaBytes_ExceedingLimitAreNeverSaved()
    {
        _controller.ResultFactory = _ => new(true, Media: new byte[32 * 1024 * 1024 + 1], MediaType: "video/webm");
        var result = await Execute(Input("""{"action":"record_status"}"""));
        Assert.True(result.IsError);
        Assert.Empty(_workspace.SavedArtifacts);
    }

    [Fact]
    public async Task CancellationToken_IsPropagatedToArtifactSave()
    {
        _controller.ResultFactory = _ => new(true, ScreenshotPng: Png);
        using var cancellation = new CancellationTokenSource();
        await Execute(Input("""{"action":"screenshot"}"""), cancellation.Token);
        Assert.Equal(cancellation.Token, _workspace.LastSaveCancellationToken);
    }

    private ValueTask<ToolExecutionResult> Execute(JsonElement input, CancellationToken cancellationToken = default) =>
        _tool.ExecuteAsync(new ToolExecutionContext(Session, _workspace.Root, input, _workspace, TimeProvider.System), cancellationToken);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
