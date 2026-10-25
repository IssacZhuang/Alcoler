using System.Runtime.InteropServices;
using System.Text.Json;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Browser.Tests;

public class BrowserToolTests
{
    private static readonly SessionId Session = new("sess_tool_tests");

    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
    private readonly FakeBrowserController _controller = new();
    private readonly FakeToolWorkspace _workspace = new();
    private readonly BrowserTool _tool;

    public BrowserToolTests() => _tool = new BrowserTool(_controller);

    [Fact]
    public void PlatformSupport_IsWindowsOnly()
    {
        Assert.True(_tool.IsAvailable(OSPlatform.Windows));
        Assert.False(_tool.IsAvailable(OSPlatform.Linux));
        Assert.False(_tool.IsAvailable(OSPlatform.OSX));
    }

    [Fact]
    public async Task UnknownVerb_IsRejected()
    {
        var issue = await _tool.ValidateInputAsync(Input("""{"action":"explode"}"""), CancellationToken.None);
        Assert.Equal("unknown_action", issue!.Code);
        Assert.Contains("explode", issue.Message);
    }

    [Fact]
    public async Task MissingAction_IsRejected()
    {
        var issue = await _tool.ValidateInputAsync(Input("""{"url":"https://example.com"}"""), CancellationToken.None);
        Assert.Equal("missing_action", issue!.Code);
    }

    [Fact]
    public async Task NonObjectInput_IsRejected()
    {
        var issue = await _tool.ValidateInputAsync(Input("[]"), CancellationToken.None);
        Assert.Equal("missing_action", issue!.Code);
    }

    [Fact]
    public async Task Navigate_RequiresUrl()
    {
        var issue = await _tool.ValidateInputAsync(Input("""{"action":"navigate"}"""), CancellationToken.None);
        Assert.Equal("missing_url", issue!.Code);
    }

    [Fact]
    public async Task Click_RequiresSelector()
    {
        var issue = await _tool.ValidateInputAsync(Input("""{"action":"click"}"""), CancellationToken.None);
        Assert.Equal("missing_selector", issue!.Code);
    }

    [Fact]
    public async Task Type_RequiresSelectorAndText()
    {
        var missingSelector = await _tool.ValidateInputAsync(
            Input("""{"action":"type","text":"hi"}"""), CancellationToken.None);
        Assert.Equal("missing_selector", missingSelector!.Code);

        var missingText = await _tool.ValidateInputAsync(
            Input("""{"action":"type","selector":"#q"}"""), CancellationToken.None);
        Assert.Equal("missing_text", missingText!.Code);
    }

    [Fact]
    public async Task Press_RequiresKey()
    {
        var issue = await _tool.ValidateInputAsync(Input("""{"action":"press"}"""), CancellationToken.None);
        Assert.Equal("missing_key", issue!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(70000)]
    public async Task Wait_RejectsOutOfRangeMilliseconds(int milliseconds)
    {
        var issue = await _tool.ValidateInputAsync(
            Input($$"""{"action":"wait","milliseconds":{{milliseconds}}}"""), CancellationToken.None);
        Assert.Equal("invalid_milliseconds", issue!.Code);
    }

    [Fact]
    public async Task EachVerb_ParsesIntoTheMatchingAction()
    {
        await Execute("""{"action":"navigate","url":"https://example.com"}""");
        Assert.IsType<BrowserNavigateAction>(_controller.Executed[0].Action);
        Assert.Equal("https://example.com", ((BrowserNavigateAction)_controller.Executed[0].Action).Url);

        await Execute("""{"action":"click","selector":"#submit"}""");
        Assert.Equal("click", _controller.Executed[1].Action.Verb);
        Assert.Equal("#submit", ((BrowserClickAction)_controller.Executed[1].Action).Selector);

        await Execute("""{"action":"type","selector":"#q","text":"alcoler","submit":true}""");
        var type = Assert.IsType<BrowserTypeAction>(_controller.Executed[2].Action);
        Assert.Equal(("#q", "alcoler", true), (type.Selector, type.Text, type.Submit));

        await Execute("""{"action":"screenshot"}""");
        Assert.IsType<BrowserScreenshotAction>(_controller.Executed[3].Action);

        await Execute("""{"action":"press","key":"Control+A"}""");
        Assert.Equal("Control+A", ((BrowserPressAction)_controller.Executed[4].Action).Key);

        await Execute("""{"action":"scroll","deltaY":600}""");
        var scroll = Assert.IsType<BrowserScrollAction>(_controller.Executed[5].Action);
        Assert.Equal((0, 600), (scroll.DeltaX, scroll.DeltaY));

        await Execute("""{"action":"wait","milliseconds":250}""");
        Assert.Equal(250, ((BrowserWaitAction)_controller.Executed[6].Action).Milliseconds);

        await Execute("""{"action":"read_text"}""");
        Assert.IsType<BrowserReadTextAction>(_controller.Executed[7].Action);
    }

    [Fact]
    public async Task Execute_UsesContextSessionId_AndReturnsUrlTitleSummary()
    {
        _controller.ResultFactory = _ => new BrowserActionResult(
            Ok: true, Url: "https://example.com", Title: "Example Domain");
        var result = await Execute("""{"action":"navigate","url":"https://example.com"}""");

        Assert.False(result.IsError);
        Assert.Equal(Session, _controller.Executed.Single().SessionId);
        Assert.Contains("https://example.com", Assert.IsType<ToolResultText>(result.Content.Single()).Text);
        Assert.Contains("Example Domain", Assert.IsType<ToolResultText>(result.Content.Single()).Text);
    }

    [Fact]
    public async Task Execute_ScreenshotSummary_ExcludesPngBytes()
    {
        _controller.ResultFactory = _ => new BrowserActionResult(
            Ok: true, Url: "https://example.com", ScreenshotPng: Png);
        var result = await Execute("""{"action":"screenshot"}""");

        Assert.False(result.IsError);
        var text = Assert.IsType<ToolResultText>(result.Content[0]).Text;
        Assert.Contains("artifact", text);
        Assert.DoesNotContain("iVBOR", text);
        var media = Assert.IsType<ToolResultMedia>(result.Content[1]);
        Assert.Equal("image/png", media.MimeType);
        var saved = Assert.Single(_workspace.SavedArtifacts);
        Assert.Equal(Session, saved.SessionId);
        Assert.Equal(Png, saved.Data);
        Assert.Equal(saved.FileName, media.FileName);
    }

    [Fact]
    public async Task Execute_ReadText_AppendsPageText()
    {
        _controller.ResultFactory = _ => new BrowserActionResult(
            Ok: true, Url: "https://example.com", Text: "page body words");
        var result = await Execute("""{"action":"read_text"}""");

        var text = Assert.IsType<ToolResultText>(result.Content.Single()).Text;
        Assert.Contains("page body words", text);
    }

    [Fact]
    public async Task Execute_FailedActionResult_BecomesErrorToolResult()
    {
        _controller.ResultFactory = _ => new BrowserActionResult(Ok: false, Error: "net::ERR_CONNECTION_REFUSED");
        var result = await Execute("""{"action":"navigate","url":"https://nope.invalid"}""");

        Assert.True(result.IsError);
        Assert.Contains("net::ERR_CONNECTION_REFUSED", Assert.IsType<ToolResultText>(result.Content.Single()).Text);
    }

    [Fact]
    public async Task Execute_InvalidInput_ShortCircuitsWithoutTouchingController()
    {
        var result = await Execute("""{"action":"explode"}""");
        Assert.True(result.IsError);
        Assert.Empty(_controller.Executed);
    }

    [Fact]
    public void Metadata_MatchesBrowserPolicy()
    {
        var metadata = _tool.Metadata;
        Assert.False(metadata.ReadOnly);
        Assert.False(metadata.Destructive);
        Assert.False(metadata.ConcurrentSafe);
        Assert.Equal(SideEffectScope.System, metadata.SideEffectScope);
        Assert.Equal(RiskLevel.Medium, metadata.RiskLevel);
        Assert.False(metadata.NeedsApproval);
        Assert.False(metadata.AllowedInPlanMode);
    }

    [Fact]
    public void Contract_IsNamedBrowser_AndSchemaListsEveryVerb()
    {
        Assert.Equal("Browser", _tool.Contract.Name);
        var schema = _tool.Contract.InputSchema;
        var verbs = schema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray()
            .Select(v => v.GetString()!).ToArray();
        Assert.Equal(
            ["navigate", "click", "type", "screenshot", "press", "scroll", "wait", "read_text",
                "capabilities", "tabs", "new_tab", "close_tab", "select_tab", "back", "forward", "reload",
                "snapshot", "dom_snapshot", "dblclick", "hover", "fill", "drag", "check", "uncheck", "select_option",
                "count", "inner_text", "text_content", "is_visible", "is_enabled", "element_info", "get_attribute",
                "wait_for", "evaluate", "dialog", "downloads", "download_media", "viewport", "visibility", "takeover",
                "resume", "finalize", "record_start", "record_status", "record_cancel"],
            verbs);
    }

    private async Task<ToolExecutionResult> Execute(string json)
    {
        var context = new ToolExecutionContext(
            Session, @"C:\fake-workspace", Input(json), _workspace, TimeProvider.System);
        return await _tool.ExecuteAsync(context, CancellationToken.None);
    }

    private static JsonElement Input(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
