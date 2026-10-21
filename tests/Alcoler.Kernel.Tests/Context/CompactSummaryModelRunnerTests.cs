using System.Runtime.CompilerServices;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.Models;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class CompactSummaryModelRunnerTests
{
    private static readonly ModelRequest Request = new([], [], new ModelCallOptions(MaxOutputTokens: 20_000));
    private static readonly ModelRunnerOptions Options = new() { BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero };
    private static readonly ModelStreamEvent[] Generated = [new ModelTextDelta("fallback"), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop)];

    [Fact]
    public async Task HiddenPartialBeforeBlockEnd_IsDiscardedForFreshNonStreamingScope()
    {
        var model = new Model([new ModelTextDelta("discard"), new ModelStreamFailed(new ModelFailure(ModelFailureKind.NetworkError, "broken", Phase: ModelFailurePhase.ResponseBody))]);

        var result = await CompactSummaryModelRunner.RunAsync(model, Request, CancellationToken.None, Options);

        Assert.Null(result.Failure);
        Assert.Equal("fallback", result.Text);
        Assert.Equal(1, model.StreamCalls);
        Assert.Equal(1, model.GenerateCalls);
        Assert.Equal(2, model.ScopeCount);
        Assert.NotEqual(model.StreamScope, model.GenerateScope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedContentEnd_PreventsStreamReplayAndHttpFallback(bool reasoning)
    {
        ModelStreamEvent end = reasoning ? new ModelReasoningEnded() : new ModelTextEnded();
        var model = new Model([new ModelTextDelta("committed"), end,
            new ModelStreamFailed(new ModelFailure(ModelFailureKind.NetworkError, "tail broke", Phase: ModelFailurePhase.ResponseBody))]);

        var result = await CompactSummaryModelRunner.RunAsync(model, Request, CancellationToken.None, Options);

        Assert.Equal(ModelFailureKind.NetworkError, result.Failure?.Kind);
        Assert.Equal(1, model.StreamCalls);
        Assert.Equal(0, model.GenerateCalls);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, true)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    public async Task SetupHttpRejection_AllowsFallbackOnlyFor404(int status, bool fallback)
    {
        var model = new Model([new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest, "HTTP rejection",
            Phase: ModelFailurePhase.RequestSetup, HttpStatusCode: status))]);

        var result = await CompactSummaryModelRunner.RunAsync(model, Request, CancellationToken.None, Options);

        Assert.Equal(fallback ? 1 : 0, model.GenerateCalls);
        Assert.Equal(fallback ? "fallback" : string.Empty, result.Text);
        Assert.Equal(fallback, result.Failure is null);
    }

    [Theory]
    [InlineData(ModelFinishReason.Other, true)]
    [InlineData(ModelFinishReason.Length, false)]
    public async Task CompletedContextPressure_DoesNotFallBack(ModelFinishReason reason, bool contextExceeded)
    {
        var model = new Model([new ModelStreamFinished(reason, ContextWindowExceeded: contextExceeded)]);

        var result = await CompactSummaryModelRunner.RunAsync(model, Request, CancellationToken.None, Options);

        Assert.Equal(ModelFailureKind.ContextOverflow, result.Failure?.Kind);
        Assert.Equal(0, model.GenerateCalls);
    }

    [Fact]
    public async Task MediaTooLarge_DoesNotFallBack()
    {
        var model = new Model([new ModelStreamFailed(new ModelFailure(ModelFailureKind.ProviderError, "image too large", Phase: ModelFailurePhase.ResponseBody))]);
        var result = await CompactSummaryModelRunner.RunAsync(model, Request, CancellationToken.None, Options);
        Assert.NotNull(result.Failure);
        Assert.Equal(1, model.StreamCalls);
        Assert.Equal(0, model.GenerateCalls);
    }

    [Fact]
    public void MediaRetryProjection_ReplacesStandaloneAndEmbeddedImagesWithoutMutatingRequest()
    {
        var image = new InputImageBlock("image/png", "payload");
        var result = new InputToolResultBlock(StrongId.NewToolCallId(), "read text", true, [image, image]);
        var message = new ModelInputMessage(MessageRole.User, [new InputTextBlock("keep"), image, result], new CacheControl(), "provider", "model");
        var request = Request with { Messages = [message] };

        var projected = CompactSummaryModelRunner.ProjectMediaForRetry(request);

        Assert.Equal("[image]", Assert.IsType<InputTextBlock>(projected.Messages[0].Content[1]).Text);
        var projectedResult = Assert.IsType<InputToolResultBlock>(projected.Messages[0].Content[2]);
        Assert.Equal("read text\n\n[image]\n\n[image]", projectedResult.Content);
        Assert.Null(projectedResult.Images);
        Assert.True(projectedResult.IsError);
        Assert.Equal(result.Id, projectedResult.Id);
        Assert.Same(image, request.Messages[0].Content[1]);
        Assert.Equal(2, result.Images!.Count);
        Assert.Same(request.Tools, projected.Tools);
        Assert.Same(request.Options, projected.Options);
        Assert.Equal(message.CacheControl, projected.Messages[0].CacheControl);
        Assert.Equal(message.ProviderId, projected.Messages[0].ProviderId);
        Assert.Equal(message.ModelId, projected.Messages[0].ModelId);
        Assert.Same(projected, CompactSummaryModelRunner.ProjectMediaForRetry(projected));
        Assert.Same(Request, CompactSummaryModelRunner.ProjectMediaForRetry(Request));
    }

    [Fact]
    public async Task EmptyOtherFinish_FallsBackButRawStopReasonMakesResponseComplete()
    {
        var malformed = new Model([new ModelStreamFinished(ModelFinishReason.Other)]);
        Assert.Equal("fallback", (await CompactSummaryModelRunner.RunAsync(malformed, Request, CancellationToken.None, Options)).Text);
        var complete = new Model([new ModelStreamFinished(ModelFinishReason.Other, RawFinishReason: "end_turn")]);
        Assert.Null((await CompactSummaryModelRunner.RunAsync(complete, Request, CancellationToken.None, Options)).Failure);
        Assert.Equal(0, complete.GenerateCalls);
    }

    [Fact]
    public async Task CancelledRequest_DoesNotGenerate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var model = new Model([]);
        var result = await CompactSummaryModelRunner.RunAsync(model, Request, cancellation.Token, Options);
        Assert.Equal(ModelFailureKind.Cancelled, result.Failure?.Kind);
        Assert.Equal(0, model.GenerateCalls);
    }

    private sealed class Model(IReadOnlyList<ModelStreamEvent> events) : IChatModel
    {
        private readonly IReadOnlyList<ModelStreamEvent> _events = events;
        public ModelDescriptor Descriptor { get; } = new("test", "test", "test", new ModelProperties());
        public bool SupportsNonStreamingGeneration => true;
        public int StreamCalls { get; private set; }
        public int GenerateCalls { get; private set; }
        public int ScopeCount { get; private set; }
        public int StreamScope { get; private set; }
        public int GenerateScope { get; private set; }
        public IChatModel CreateRequestScope() => new Scope(this, ++ScopeCount);
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException();

        private sealed class Scope(Model owner, int id) : IChatModel
        {
            public ModelDescriptor Descriptor => owner.Descriptor;
            public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.CompletedTask;
                owner.StreamCalls++;
                owner.StreamScope = id;
                foreach (var streamEvent in owner._events)
                    yield return streamEvent;
            }
            public ValueTask<IReadOnlyList<ModelStreamEvent>> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
            {
                owner.GenerateCalls++;
                owner.GenerateScope = id;
                return ValueTask.FromResult<IReadOnlyList<ModelStreamEvent>>(Generated);
            }
        }
    }
}
