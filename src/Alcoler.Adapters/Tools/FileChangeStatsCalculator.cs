using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

internal static class FileChangeStatsCalculator
{
    private const int MaxLcsCells = 400_000;

    public static FileChangeStats Compute(string? before, string after)
    {
        var beforeLines = SplitLines(before);
        var afterLines = SplitLines(after);

        var prefix = 0;
        while (prefix < beforeLines.Length && prefix < afterLines.Length
            && beforeLines[prefix] == afterLines[prefix])
        {
            prefix++;
        }

        var beforeTail = beforeLines.Length - 1;
        var afterTail = afterLines.Length - 1;
        while (beforeTail >= prefix && afterTail >= prefix
            && beforeLines[beforeTail] == afterLines[afterTail])
        {
            beforeTail--;
            afterTail--;
        }

        var trimmedBefore = beforeLines[prefix..(beforeTail + 1)];
        var trimmedAfter = afterLines[prefix..(afterTail + 1)];
        if (trimmedBefore.Length == 0)
            return new FileChangeStats(trimmedAfter.Length, 0);
        if (trimmedAfter.Length == 0)
            return new FileChangeStats(0, trimmedBefore.Length);
        if (trimmedBefore.Length * (long)trimmedAfter.Length > MaxLcsCells)
            return new FileChangeStats(trimmedAfter.Length, trimmedBefore.Length);

        var lcs = new int[trimmedAfter.Length + 1];
        for (var beforeIndex = 1; beforeIndex <= trimmedBefore.Length; beforeIndex++)
        {
            var previousDiagonal = 0;
            for (var afterIndex = 1; afterIndex <= trimmedAfter.Length; afterIndex++)
            {
                var previousRow = lcs[afterIndex];
                lcs[afterIndex] = trimmedBefore[beforeIndex - 1] == trimmedAfter[afterIndex - 1]
                    ? previousDiagonal + 1
                    : Math.Max(lcs[afterIndex], lcs[afterIndex - 1]);
                previousDiagonal = previousRow;
            }
        }

        var unchanged = lcs[trimmedAfter.Length];
        return new FileChangeStats(trimmedAfter.Length - unchanged, trimmedBefore.Length - unchanged);
    }

    private static string[] SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return [];
        var lines = content.Split('\n');
        var count = lines.Length;
        if (lines[count - 1].Length == 0)
            count--;
        var result = new string[count];
        for (var index = 0; index < count; index++)
            result[index] = lines[index].EndsWith('\r') ? lines[index][..^1] : lines[index];
        return result;
    }
}
