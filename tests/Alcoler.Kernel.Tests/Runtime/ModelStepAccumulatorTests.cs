using System.Text.Json;
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
    public async Task MetadataOnlyReasoning_IsPreservedInFullAndPartialMessages()
    {
        var accumulator = new ModelStepAccumulator(new TurnId("turn-1"));
        await ApplyAsync(accumulator,
            new ModelReasoningStarted(),
            new ModelReasoningEnded("sig-only"),
            new ModelReasoningStarted(),
            new ModelReasoningEnded(RedactedData: "opaque-redacted"),
            new ModelReasoningStarted(),
            new ModelReasoningEnded(EncryptedContent: "opaque-encrypted", ProviderItemId: "item-1"));

        foreach (var message in new[]
                 {
                     accumulator.BuildAssistantMessage(DateTimeOffset.UtcNow),
                     accumulator.BuildPartialAssistantMessage(DateTimeOffset.UtcNow),
                 })
        {
            var reasoning = message.Parts.OfType<ReasoningPart>().ToArray();
            Assert.Equal(3, reasoning.Length);
            Assert.All(reasoning, part => Assert.Equal("", part.Text));
            Assert.Equal("sig-only", reasoning[0].ProviderSignature);
            Assert.Equal("opaque-redacted", reasoning[1].RedactedData);
            Assert.Equal("opaque-encrypted", reasoning[2].EncryptedContent);
            Assert.Equal("item-1", reasoning[2].ProviderItemId);
        }
    }

    [Fact]
    public async Task EmptyReasoningBlockWithoutMetadata_IsDropped()
    {
        var accumulator = new ModelStepAccumulator(new TurnId("turn-1"));
        await ApplyAsync(accumulator, new ModelReasoningStarted(), new ModelReasoningEnded());

        Assert.Empty(accumulator.BuildAssistantMessage(DateTimeOffset.UtcNow).Parts);
    }

    [Fact]
    public async Task DuplicateReadyToolCallId_KeepsFirstCallAndTrimsNames()
    {
        var accumulator = new ModelStepAccumulator(new TurnId("turn-1"));
        var first = new ToolCall(new ToolCallId("same"), "  Read \t", JsonSerializer.SerializeToElement(new { path = "first" }));
        await ApplyAsync(accumulator,
            new ModelToolCallReady(first),
            new ModelToolCallReady(first with { Name = "Write", Input = JsonSerializer.SerializeToElement(new { path = "second" }) }),
            new ModelToolCallReady(new ToolCall(new ToolCallId("different"), " Read ", first.Input)));

        Assert.Collection(accumulator.ToolCalls,
            call =>
            {
                Assert.Equal(first.Id, call.Id);
                Assert.Equal("Read", call.Name);
                Assert.Equal("first", call.Input.GetProperty("path").GetString());
            },
            call =>
            {
                Assert.Equal(new ToolCallId("different"), call.Id);
                Assert.Equal("Read", call.Name);
            });
        var message = accumulator.BuildAssistantMessage(DateTimeOffset.UtcNow);
        Assert.Equal(2, message.Parts.OfType<ToolCallPart>().Count());
        Assert.Empty(accumulator.BuildPartialAssistantMessage(DateTimeOffset.UtcNow).Parts.OfType<ToolCallPart>());
    }
}
