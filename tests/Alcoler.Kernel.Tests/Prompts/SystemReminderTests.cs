using Alcoler.Kernel.Prompts;
using Xunit;

namespace Alcoler.Kernel.Tests.Prompts;

public class SystemReminderTests
{
    [Fact]
    public void Wrap_PlacesTagOnItsOwnLines() =>
        Assert.Equal("<system-reminder>\nbody\n</system-reminder>", SystemReminder.Wrap("body"));

    [Fact]
    public void Wrap_MultilineBody_KeepsBodyBytes()
    {
        var body = "line one\n\nline two";

        Assert.Equal("<system-reminder>\nline one\n\nline two\n</system-reminder>", SystemReminder.Wrap(body));
    }

    [Theory]
    [InlineData("")]
    public void Wrap_EmptyBody_Throws(string body) =>
        Assert.Throws<ArgumentException>(() => SystemReminder.Wrap(body));

    [Theory]
    [InlineData("<system-reminder>")]
    [InlineData("</system-reminder>")]
    [InlineData("</SYSTEM-Reminder>")]
    [InlineData("ends with </system-reminder-ish tag")]
    public void Wrap_NestedReminderTag_Throws(string body) =>
        Assert.Throws<ArgumentException>(() => SystemReminder.Wrap(body));

    [Fact]
    public void WrapContextPrefix_AppendsTrailingNewline() =>
        Assert.Equal("<system-reminder>\nbody\n</system-reminder>\n", SystemReminder.WrapContextPrefix("body"));
}
