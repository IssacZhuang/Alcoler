using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests.ViewModel;

public class ToolCallItemViewModelTests
{
    [Fact]
    public void InputJson_KeepsQuotesAndNonAsciiReadable()
    {
        const string arguments = """
            {
              "new_string": "    \"Components\": {\n        \"Placeable\": {\n            // 中文注释\n        }\n    }\n"
            }
            """;
        using var document = JsonDocument.Parse(arguments);
        var row = new ToolCallRow("t1", StrongId.NewToolCallId(), "Edit", document.RootElement.Clone(),
            ToolCallStatus.Succeeded, Output: null, IsError: false, Duration: null, StartedAt: DateTimeOffset.UtcNow);
        var item = new ToolCallItemViewModel("t1", row.Id, row.ToolName);

        item.UpdateFrom(row);

        Assert.Contains("\\\"Components\\\"", item.InputJson);
        Assert.Contains("中文注释", item.InputJson);
        Assert.DoesNotContain("\\u0022", item.InputJson);
        Assert.DoesNotContain("\\u4E2D", item.InputJson);
        using var displayed = JsonDocument.Parse(item.InputJson);
        Assert.Equal("    \"Components\": {\n        \"Placeable\": {\n            // 中文注释\n        }\n    }\n",
            displayed.RootElement.GetProperty("new_string").GetString());
    }

    [Fact]
    public void CollapsedRow_KeepsExpandedProjectionsEmptyWithoutMaterializing()
    {
        var item = Create(Row(output: "large output payload"));

        Assert.Equal(string.Empty, item.ExpandedInputJson);
        Assert.Equal(string.Empty, item.ExpandedOutputText);
    }

    [Fact]
    public void ExpandedRow_MaterializesDetailsAndTrimReleasesThem()
    {
        var item = Create(Row(output: "large output payload"));
        item.IsExpanded = true;

        Assert.Contains("ls -la", item.ExpandedInputJson);
        Assert.Equal("large output payload", item.ExpandedOutputText);

        item.TrimMaterializedDetails();
        Assert.Equal("large output payload", item.OutputText);

        item.IsExpanded = false;
        Assert.Equal(string.Empty, item.ExpandedOutputText);
        Assert.Equal("large output payload", item.OutputText);
    }

    [Fact]
    public void InputChange_InvalidatesMaterializedDetail()
    {
        var item = Create(Row(output: null));
        item.IsExpanded = true;
        Assert.Contains("ls -la", item.ExpandedInputJson);

        item.UpdateFrom(Row(output: null) with
        {
            Input = JsonSerializer.SerializeToElement(new { command = "updated" }),
        });

        Assert.Contains("updated", item.ExpandedInputJson);
        Assert.DoesNotContain("ls -la", item.ExpandedInputJson);
    }

    private static ToolCallRow Row(string? output) => new("t1", StrongId.NewToolCallId(), "Bash",
        JsonSerializer.SerializeToElement(new { command = "ls -la" }), ToolCallStatus.Succeeded,
        output is null ? null : [new ToolResultText(output)], IsError: false,
        Duration: null, StartedAt: DateTimeOffset.UtcNow);

    private static ToolCallItemViewModel Create(ToolCallRow row)
    {
        var item = new ToolCallItemViewModel(row.RowId, row.Id, row.ToolName);
        item.UpdateFrom(row);
        return item;
    }
}

public class AssistantReasoningItemViewModelTests
{
    [Fact]
    public void ReasoningBody_MaterializesOnlyWhileExpanded()
    {
        var assistant = new AssistantMessageItemViewModel("a1", StrongId.NewMessageId());
        assistant.UpdateFrom(new AssistantMessageRow("a1", StrongId.NewMessageId(),
        [
            new ReasoningPart(StrongId.NewPartId(), "first thought"),
            new ReasoningPart(StrongId.NewPartId(), "second thought"),
        ], IsStreaming: false, At: DateTimeOffset.UtcNow));

        Assert.Equal(string.Empty, assistant.ExpandedReasoningText);

        assistant.IsReasoningExpanded = true;
        Assert.Equal("first thought\n\nsecond thought", assistant.ExpandedReasoningText);
        Assert.Equal("first thought\n\nsecond thought", assistant.ReasoningText);

        assistant.IsReasoningExpanded = false;
        Assert.Equal(string.Empty, assistant.ExpandedReasoningText);
        Assert.Equal("first thought\n\nsecond thought", assistant.ReasoningText);
    }
}
