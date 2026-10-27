using System.Text.Json;
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
}
