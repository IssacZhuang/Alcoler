using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class ModelStepAccumulatorTests
{
    private static ValueTask Emit(SessionEvent _) => ValueTask.CompletedTask;

    private static async Task ApplyAsync(ModelStepAccumulator accumulator, params ModelStreamEvent[] events)
    {
        foreach (var streamEvent in events)
            await accumulator.ApplyAsync(streamEvent, Emit);
    }

    [Fact]
    public async Task MultipleReasoningBlocks_KeepSeparateSignatures()
    {
        var accumulator = new ModelStepAccumulator(new TurnId("turn-1"));
        await ApplyAsync(accumulator,
            new ModelReasoningStarted(),
            new ModelReasoningDelta("first"),
            new ModelReasoningEnded("sig-1"),
            new ModelReasoningStarted(),
            new ModelReasoningDelta("second"),
            new ModelReasoningEnded("sig-2"));

        var message = accumulator.BuildAssistantMessage(DateTimeOffset.UtcNow);

        var first = Assert.IsType<ReasoningPart>(message.Parts[0]);
        var second = Assert.IsType<ReasoningPart>(message.Parts[1]);
        Assert.Equal("first", first.Text);
        Assert.Equal("sig-1", first.ProviderSignature);
        Assert.Equal("second", second.Text);
        Assert.Equal("sig-2", second.ProviderSignature);
    }

    [Fact]
    public async Task ReasoningWithoutSignature_KeepsNullSignature()
    {
        var accumulator = new ModelStepAccumulator(new TurnId("turn-1"));
        await ApplyAsync(accumulator,
            new ModelReasoningDelta("open thought"),
            new ModelReasoningEnded());

        var message = accumulator.BuildAssistantMessage(DateTimeOffset.UtcNow);

        var reasoning = Assert.IsType<ReasoningPart>(message.Parts.Single());
        Assert.Null(reasoning.ProviderSignature);
    }

    [Fact]
    public async Task SignatureWithoutText_IsDropped()
    {
        var accumulator = new ModelStepAccumulator(new TurnId("turn-1"));
        await ApplyAsync(accumulator,
            new ModelReasoningStarted(),
            new ModelReasoningEnded("sig-only"));

        var message = accumulator.BuildAssistantMessage(DateTimeOffset.UtcNow);

        Assert.Empty(message.Parts);
    }
}
