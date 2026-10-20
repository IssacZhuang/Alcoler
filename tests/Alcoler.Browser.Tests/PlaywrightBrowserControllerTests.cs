using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Browser.Tests;

public class PlaywrightBrowserControllerTests
{
    private const string DataUrl =
        "data:text/html,<html><head><title>Alcoler%20Live</title></head><body><h1>alcoler-browser-live</h1></body></html>";

    [Fact(Skip = "requires playwright browsers")]
    public async Task Navigate_ThenScreenshot_ProducesUrlTitleAndPng()
    {
        using var controller = new PlaywrightBrowserController();
        var sessionId = new SessionId("sess_live_verification");

        var navigate = await controller.ExecuteAsync(
            sessionId, new BrowserNavigateAction(DataUrl), CancellationToken.None);
        Assert.True(navigate.Ok, navigate.Error);
        Assert.Equal("Alcoler Live", navigate.Title);

        var screenshot = await controller.ExecuteAsync(
            sessionId, new BrowserScreenshotAction(), CancellationToken.None);
        Assert.True(screenshot.Ok, screenshot.Error);
        Assert.NotNull(screenshot.ScreenshotPng);
        Assert.True(screenshot.ScreenshotPng!.Length > 8);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], screenshot.ScreenshotPng.Take(4).ToArray());

        var status = controller.GetStatus(sessionId);
        Assert.Equal(BrowserSessionState.Launched, status!.State);
        Assert.True(File.Exists(status.LatestScreenshotUri));

        var readText = await controller.ExecuteAsync(
            sessionId, new BrowserReadTextAction(), CancellationToken.None);
        Assert.True(readText.Ok, readText.Error);
        Assert.Contains("alcoler-browser-live", readText.Text);
    }
}
