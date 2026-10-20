using System.Text.Json;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class ToolCallAnomalyTests
{
    private static JsonElement Input(object value) =>
        JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ThirdConsecutiveIdenticalCall_FiresWarningWithVerbatimBody()
    {
        var tracker = new ToolCallAnomalyTracker();

        Assert.Null(tracker.Track("Read", Input(new { file_path = "a.txt" })));
        Assert.Null(tracker.Track("Read", Input(new { file_path = "a.txt" })));
        Assert.Equal(
            "You have called Read with the same input 3 times in a row.\nDo not repeat the exact same tool call again unless the user explicitly asked you to retry it unchanged.\nUse the existing result to take a different next step, explain the blocker, or ask the user for guidance.",
            tracker.Track("Read", Input(new { file_path = "a.txt" })));
    }

    [Fact]
    public void DifferentInput_ResetsStreak()
    {
        var tracker = new ToolCallAnomalyTracker();

        tracker.Track("Read", Input(new { file_path = "a.txt" }));
        tracker.Track("Read", Input(new { file_path = "a.txt" }));
        Assert.Null(tracker.Track("Read", Input(new { file_path = "b.txt" })));
        Assert.Null(tracker.Track("Read", Input(new { file_path = "b.txt" })));
        Assert.NotNull(tracker.Track("Read", Input(new { file_path = "b.txt" })));
    }

    [Fact]
    public void DifferentToolName_ResetsStreak()
    {
        var tracker = new ToolCallAnomalyTracker();

        tracker.Track("Read", Input(new { file_path = "a.txt" }));
        Assert.Null(tracker.Track("Grep", Input(new { file_path = "a.txt" })));
        Assert.Null(tracker.Track("Grep", Input(new { file_path = "a.txt" })));
        Assert.NotNull(tracker.Track("Grep", Input(new { file_path = "a.txt" })));
    }

    [Fact]
    public void AtMostThreeWarningsPerTurn()
    {
        var tracker = new ToolCallAnomalyTracker();

        for (var cycle = 0; cycle < 5; cycle++)
        {
            tracker.Track("Read", Input(new { file_path = "a.txt" }));
            tracker.Track("Read", Input(new { file_path = "a.txt" }));
            var warning = tracker.Track("Read", Input(new { file_path = "a.txt" }));
            // 4th and 5th streaks stay silent; only 3 warnings exist per turn.
            Assert.Equal(cycle < 3, warning is not null);
            // A different call closes the streak so the next triple is consecutive again.
            tracker.Track("Read", Input(new { file_path = "z.txt" }));
        }
    }

    [Fact]
    public void PropertyOrderInInput_DoesNotMaskRepeats()
    {
        var tracker = new ToolCallAnomalyTracker();

        tracker.Track("Read", JsonSerializer.SerializeToElement(new { file_path = "a.txt", offset = 1 }));
        tracker.Track("Read", JsonSerializer.SerializeToElement(new { offset = 1, file_path = "a.txt" }));
        Assert.NotNull(tracker.Track("Read", JsonSerializer.SerializeToElement(new { file_path = "a.txt", offset = 1 })));
    }

    [Fact]
    public void ResetStreak_ForcesAFreshTriple()
    {
        var tracker = new ToolCallAnomalyTracker();

        tracker.Track("Read", Input(new { file_path = "a.txt" }));
        tracker.Track("Read", Input(new { file_path = "a.txt" }));
        tracker.ResetStreak();
        Assert.Null(tracker.Track("Read", Input(new { file_path = "a.txt" })));
        Assert.Null(tracker.Track("Read", Input(new { file_path = "a.txt" })));
        Assert.NotNull(tracker.Track("Read", Input(new { file_path = "a.txt" })));
    }
}
