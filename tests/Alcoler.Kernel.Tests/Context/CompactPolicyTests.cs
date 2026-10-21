using Alcoler.Kernel.Context;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class CompactPolicyTests
{
    [Fact]
    public void Constants_ReplicatePlanValues()
    {
        Assert.Equal(200_000, CompactPolicy.DefaultContextWindowTokens);
        Assert.Equal(21_000, CompactPolicy.AutoCompactOutputReserveTokens);
        Assert.Equal(32_000, CompactPolicy.AutoCompactRequestedReserveTokens);
        Assert.Equal(13_000, CompactPolicy.AutoCompactBufferTokens);
        Assert.Equal(3, CompactPolicy.MaxConsecutiveAutoCompactFailures);
        Assert.Equal(3, CompactPolicy.RapidRefillToolTurnThreshold);
        Assert.Equal(3, CompactPolicy.MaxConsecutiveRapidRefills);
        Assert.Equal(60, CompactPolicy.MicroCompactIdleMinutes);
        Assert.Equal(5, CompactPolicy.MicroCompactKeepRecentGroups);
        Assert.Equal(256, CompactPolicy.MicroCompactMinSavingsTokens);
        Assert.Equal(20_000, CompactPolicy.SummaryMaxOutputTokens);
        Assert.Equal(100, CompactPolicy.CompactToolKeepMaxCount);
        Assert.Equal(3, CompactPolicy.AutoCompactMaxAttempts);
        Assert.Equal(3, CompactPolicy.PromptTooLongMaxRetries);
    }

    [Theory]
    [InlineData(200_000, 179_000)]
    [InlineData(25_000, 4_000)]
    [InlineData(21_000, 0)]
    [InlineData(10_000, 0)]
    [InlineData(0, 0)]
    public void EffectiveContextWindow_ClampsReserveToWindow(int window, int expected)
    {
        Assert.Equal(expected, CompactPolicy.EffectiveContextWindow(window));
    }

    [Theory]
    [InlineData(200_000, 166_000)]
    [InlineData(34_000, 0)]
    [InlineData(30_000, 0)]
    [InlineData(0, 0)]
    public void AutoCompactThreshold_SubtractsBufferFromEffectiveWindow(int window, int expected)
    {
        Assert.Equal(expected, CompactPolicy.AutoCompactThreshold(window));
    }

    [Theory]
    [InlineData(166_000, 149_400)]
    [InlineData(100_000, 90_000)]
    [InlineData(10_000, 8_000)]
    [InlineData(2_000, 0)]
    [InlineData(0, 0)]
    public void MicroCompactTriggerTokens_IsMinOfNineTenthsAndMinusTwoThousand(int threshold, int expected)
    {
        Assert.Equal(expected, CompactPolicy.MicroCompactTriggerTokens(threshold));
    }

    [Fact]
    public void ShouldAutoCompact_Disabled_ReturnsDisabled()
    {
        var result = CompactPolicy.ShouldAutoCompact(new ShouldAutoCompactInput(
            EffectiveTokens: 190_000,
            ContextWindowTokens: 200_000,
            AssistantLedRoundCount: 5,
            ConsecutiveFailures: 0,
            ConsecutiveRapidRefills: 0,
            Disabled: true));

        Assert.False(result.Compact);
        Assert.Equal("disabled", result.Reason);
    }

    [Fact]
    public void ShouldAutoCompact_FewerThanTwoAssistantLedRounds_ReturnsNotEnoughMessages()
    {
        var result = CompactPolicy.ShouldAutoCompact(new ShouldAutoCompactInput(
            190_000, 200_000, AssistantLedRoundCount: 1, ConsecutiveFailures: 0, ConsecutiveRapidRefills: 0));

        Assert.False(result.Compact);
        Assert.Equal("not_enough_messages", result.Reason);
    }

    [Fact]
    public void ShouldAutoCompact_TooManyConsecutiveFailures_ReturnsCircuitBreaker()
    {
        var result = CompactPolicy.ShouldAutoCompact(new ShouldAutoCompactInput(
            190_000, 200_000, AssistantLedRoundCount: 5, ConsecutiveFailures: 3, ConsecutiveRapidRefills: 0));

        Assert.False(result.Compact);
        Assert.Equal("circuit_breaker", result.Reason);
    }

    [Fact]
    public void ShouldAutoCompact_TooManyConsecutiveRapidRefills_ReturnsCircuitBreaker()
    {
        var result = CompactPolicy.ShouldAutoCompact(new ShouldAutoCompactInput(
            190_000, 200_000, AssistantLedRoundCount: 5, ConsecutiveFailures: 0, ConsecutiveRapidRefills: 3));

        Assert.False(result.Compact);
        Assert.Equal("circuit_breaker", result.Reason);
    }

    [Fact]
    public void ShouldAutoCompact_BelowThreshold_ReturnsBelowThreshold()
    {
        var result = CompactPolicy.ShouldAutoCompact(new ShouldAutoCompactInput(
            165_999, 200_000, AssistantLedRoundCount: 5, ConsecutiveFailures: 0, ConsecutiveRapidRefills: 0));

        Assert.False(result.Compact);
        Assert.Equal("below_threshold", result.Reason);
    }

    [Fact]
    public void ShouldAutoCompact_AtOrAboveThreshold_Compacts()
    {
        var atThreshold = CompactPolicy.ShouldAutoCompact(new ShouldAutoCompactInput(
            166_000, 200_000, AssistantLedRoundCount: 2, ConsecutiveFailures: 0, ConsecutiveRapidRefills: 0));
        var aboveThreshold = CompactPolicy.ShouldAutoCompact(new ShouldAutoCompactInput(
            170_000, 200_000, AssistantLedRoundCount: 5, ConsecutiveFailures: 0, ConsecutiveRapidRefills: 0));

        Assert.True(atThreshold.Compact);
        Assert.Equal("above_threshold", atThreshold.Reason);
        Assert.True(aboveThreshold.Compact);
        Assert.Equal("above_threshold", aboveThreshold.Reason);
    }
}
