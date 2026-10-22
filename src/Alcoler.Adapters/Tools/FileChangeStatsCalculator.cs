using System.Buffers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

internal static class FileChangeStatsCalculator
{
    private const int MaxLcsCells = 400_000;

    private readonly record struct LineRange(int Start, int Length);

    public static FileChangeStats Compute(string? before, string? after)
    {
        var beforeText = before ?? string.Empty;
        var afterText = after ?? string.Empty;
        var beforeRanges = RentLineRanges(beforeText, out var beforeCount);
        var afterRanges = RentLineRanges(afterText, out var afterCount);
        try
        {
            var prefix = 0;
            while (prefix < beforeCount && prefix < afterCount
                && LinesEqual(beforeText, beforeRanges[prefix], afterText, afterRanges[prefix]))
            {
                prefix++;
            }

            var beforeTail = beforeCount - 1;
            var afterTail = afterCount - 1;
            while (beforeTail >= prefix && afterTail >= prefix
                && LinesEqual(beforeText, beforeRanges[beforeTail], afterText, afterRanges[afterTail]))
            {
                beforeTail--;
                afterTail--;
            }

            var trimmedBefore = beforeTail + 1 - prefix;
            var trimmedAfter = afterTail + 1 - prefix;
            if (trimmedBefore == 0)
                return new FileChangeStats(trimmedAfter, 0);
            if (trimmedAfter == 0)
                return new FileChangeStats(0, trimmedBefore);
            if (trimmedBefore * (long)trimmedAfter > MaxLcsCells)
                return new FileChangeStats(trimmedAfter, trimmedBefore);

            var lcs = new int[trimmedAfter + 1];
            for (var beforeIndex = 1; beforeIndex <= trimmedBefore; beforeIndex++)
            {
                var previousDiagonal = 0;
                for (var afterIndex = 1; afterIndex <= trimmedAfter; afterIndex++)
                {
                    var previousRow = lcs[afterIndex];
                    lcs[afterIndex] = LinesEqual(
                        beforeText, beforeRanges[prefix + beforeIndex - 1],
                        afterText, afterRanges[prefix + afterIndex - 1])
                        ? previousDiagonal + 1
                        : Math.Max(lcs[afterIndex], lcs[afterIndex - 1]);
                    previousDiagonal = previousRow;
                }
            }

            var unchanged = lcs[trimmedAfter];
            return new FileChangeStats(trimmedAfter - unchanged, trimmedBefore - unchanged);
        }
        finally
        {
            if (beforeRanges.Length > 0)
                ArrayPool<LineRange>.Shared.Return(beforeRanges);
            if (afterRanges.Length > 0)
                ArrayPool<LineRange>.Shared.Return(afterRanges);
        }
    }

    private static bool LinesEqual(string left, LineRange leftRange, string right, LineRange rightRange) =>
        left.AsSpan(leftRange.Start, leftRange.Length).SequenceEqual(right.AsSpan(rightRange.Start, rightRange.Length));

    private static LineRange[] RentLineRanges(string content, out int count)
    {
        if (content.Length == 0)
        {
            count = 0;
            return [];
        }

        var total = 1;
        foreach (var c in content)
        {
            if (c == '\n')
                total++;
        }
        if (content[^1] == '\n')
            total--;
        count = total;

        var ranges = ArrayPool<LineRange>.Shared.Rent(total);
        var position = 0;
        for (var lineIndex = 0; lineIndex < total; lineIndex++)
        {
            var start = position;
            var newline = content.AsSpan(position).IndexOf('\n');
            int end;
            if (newline < 0)
            {
                end = content.Length;
                position = content.Length;
            }
            else
            {
                end = position + newline;
                position = end + 1;
            }
            var length = end - start;
            if (length > 0 && content[end - 1] == '\r')
                length--;
            ranges[lineIndex] = new LineRange(start, length);
        }
        return ranges;
    }
}
