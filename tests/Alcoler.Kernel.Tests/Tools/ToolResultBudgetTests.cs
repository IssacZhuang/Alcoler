using Alcoler.Contracts.Tools;
using Xunit;
using Alcoler.Contracts.Conversation;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class ToolResultBudgetTests
{
    [Fact]
    public void OutputWithinBudget_ReturnedUnchanged()
    {
        var result = ToolExecutionResult.Text("short output");

        var budgeted = ToolResultBudget.ApplyBudget(result, 100);

        Assert.Same(result, budgeted);
    }

    [Fact]
    public void OversizedOutput_TruncatedWithParameterizedMarker_WithinBudget()
    {
        var result = ToolExecutionResult.Text(new string('x', 100));

        var budgeted = ToolResultBudget.ApplyBudget(result, 90);

        var text = Assert.IsType<ToolResultText>(Assert.Single(budgeted.Content)).Text;
        var marker = ToolResultBudget.TruncationMarker(100, 90);
        Assert.EndsWith(marker, text);
        Assert.Equal(90, text.Length);
        Assert.Equal("[Tool output truncated by resultBudget: originalChars=100, maxModelChars=90]", marker);
    }

    [Fact]
    public void Truncation_PreservesIsError()
    {
        var result = ToolExecutionResult.Text(new string('x', 100), isError: true);

        var budgeted = ToolResultBudget.ApplyBudget(result, 10);

        Assert.True(budgeted.IsError);
    }

    [Fact]
    public void Truncation_PreservesTurnStopAndSummary()
    {
        var result = ToolExecutionResult.Text(new string('x', 100), isError: true,
            displaySummary: "stopped", stopTurnAfterResult: true);

        var budgeted = ToolResultBudget.ApplyBudget(result, 10);

        Assert.True(budgeted.StopTurnAfterResult);
        Assert.Equal("stopped", budgeted.DisplaySummary);
    }

    [Fact]
    public void MediaBlocks_AreNeverTouched()
    {
        var media = new ToolResultMedia("alcoler-artifact://s/1.png", "image/png", "1.png");
        var result = new ToolExecutionResult(
        [
            new ToolResultText(new string('x', 200)),
            media,
            new ToolResultText("tail that must be dropped"),
        ]);

        var budgeted = ToolResultBudget.ApplyBudget(result, 100);

        Assert.Equal([typeof(ToolResultText), typeof(ToolResultMedia)], budgeted.Content.Select(static b => b.GetType()));
        Assert.Equal(media, budgeted.Content[1]);
        Assert.DoesNotContain("tail that must be dropped", budgeted.Content.OfType<ToolResultText>().Single().Text);
    }

    [Fact]
    public void BlocksExactlyAtBudget_AppendMarkerAsTrailingBlock()
    {
        var result = new ToolExecutionResult(
        [
            new ToolResultText(new string('a', 60)),
            new ToolResultText(new string('b', 60)),
        ]);

        var budgeted = ToolResultBudget.ApplyBudget(result, 60);

        Assert.Equal(2, budgeted.Content.Count);
        Assert.Equal(60, Assert.IsType<ToolResultText>(budgeted.Content[0]).Text.Length);
        Assert.Equal(
            ToolResultBudget.TruncationMarker(120, 60),
            Assert.IsType<ToolResultText>(budgeted.Content[1]).Text);
    }
}
