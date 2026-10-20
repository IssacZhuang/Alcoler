using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

public static class ToolResultBudget
{
    public static string TruncationMarker(int originalChars, int maxModelChars) =>
        $"[Tool output truncated by resultBudget: originalChars={originalChars}, maxModelChars={maxModelChars}]";

    public static ToolExecutionResult ApplyBudget(ToolExecutionResult result, int maxChars)
    {
        if (maxChars < 0)
            maxChars = 0;

        var originalChars = 0;
        foreach (var block in result.Content)
            if (block is ToolResultText original)
                originalChars += original.Text.Length;
        var marker = TruncationMarker(originalChars, maxChars);

        var blocks = new List<ToolResultBlock>(result.Content.Count);
        var totalChars = 0;
        var truncated = false;

        foreach (var block in result.Content)
        {
            if (block is not ToolResultText text)
            {
                blocks.Add(block);
                continue;
            }

            if (truncated)
                continue;

            if (totalChars + text.Text.Length <= maxChars)
            {
                blocks.Add(text);
                totalChars += text.Text.Length;
                continue;
            }

            var room = maxChars - totalChars;
            var kept = room > marker.Length + 1
                ? text.Text[..(room - marker.Length - 1)] + "\n" + marker
                : marker;
            blocks.Add(new ToolResultText(kept));
            truncated = true;
        }

        return truncated
            ? new ToolExecutionResult(blocks, result.IsError, result.DisplaySummary)
            : result;
    }
}
