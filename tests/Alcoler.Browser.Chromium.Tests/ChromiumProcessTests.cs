using Alcoler.Browser.Chromium;
using Xunit;

namespace Alcoler.Browser.Chromium.Tests;

public sealed class ChromiumProcessTests
{
    [Fact]
    public void ArgumentsContainTheExactFixedSetWithoutSandbox()
    {
        var arguments = ChromiumProcess.BuildArguments("/tmp/profile dir", headless: false);
        Assert.Equal([
            "--remote-debugging-port=0",
            "--user-data-dir=/tmp/profile dir",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--disable-session-crash-bubble",
            "--hide-crash-restore-bubble",
        ], arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeadlessFlagAppearsOnlyWhenRequested(bool headless)
    {
        var arguments = ChromiumProcess.BuildArguments("C:\\profile", headless);
        Assert.Equal(headless, arguments.Contains("--headless=new"));
        if (headless) Assert.Equal("--headless=new", arguments[^1]);
        Assert.DoesNotContain(arguments, argument => argument.Contains("no-sandbox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParsesDevToolsActivePort()
    {
        Assert.Equal((9222, "/devtools/browser/abc"),
            ChromiumProcess.ParseDevToolsActivePort("9222\n/devtools/browser/abc\n"));
        Assert.Equal((9333, "/devtools/browser/x"),
            ChromiumProcess.ParseDevToolsActivePort(" 9333 \r\n /devtools/browser/x \r\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("9222\n")]
    [InlineData("abc\n/devtools/browser/abc")]
    [InlineData("99999\n/devtools/browser/abc")]
    [InlineData("9222\ndevtools/browser/abc")]
    [InlineData("9222\n/devtools browser/abc")]
    public void RejectsMalformedDevToolsActivePort(string content)
    {
        Assert.Null(ChromiumProcess.ParseDevToolsActivePort(content));
    }
}
