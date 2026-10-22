using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class FileChangeStatsCalculatorTests
{
    [Theory]
    [InlineData(null, "one\ntwo", 2, 0)]
    [InlineData("", "one\ntwo\n", 2, 0)]
    [InlineData("one\ntwo", "one\ntwo", 0, 0)]
    [InlineData("one\r\ntwo", "one\ntwo", 0, 0)]
    [InlineData("one\ntwo\nthree", "one\nTWO\nthree", 1, 1)]
    [InlineData("one\ntwo\nthree", "one\nthree", 0, 1)]
    [InlineData("one\ntwo", "one\ntwo\nthree\nfour", 2, 0)]
    [InlineData("one\ntwo\nthree", "", 0, 3)]
    public void Compute_CountsChangedLines(string? before, string after, int added, int removed)
    {
        Assert.Equal(new FileChangeStats(added, removed), FileChangeStatsCalculator.Compute(before, after));
    }

    [Fact]
    public void Compute_UsesLcsForReorderedContent()
    {
        var stats = FileChangeStatsCalculator.Compute("one\ntwo\nthree\nfour", "one\nfour\nthree\ntwo");

        Assert.Equal(new FileChangeStats(2, 2), stats);
    }
}
