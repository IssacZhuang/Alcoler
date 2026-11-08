using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Models;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

// Tool results reference images by artifact URI in stored history; the runtime hydrates
// them into image blocks on the followup request.
public class MediaHydrationTests
{
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static readonly ToolCallId ReadCallId = new("call-read");

    [Fact]
    public async Task ReadImageToolResult_IsHydratedIntoFollowupRequest()
    {
        await using var harness = new ScriptedCallHarness(
        [
            ReadImageCall(),
            [new ModelTextDelta("seen"), new ModelStreamFinished(ModelFinishReason.Stop)],
        ]);
        await using var runtime = harness.BuildRuntime(new ReadTool());
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await File.WriteAllBytesAsync(
            Path.Combine(harness.Root, "pixel.png"), Convert.FromBase64String(TinyPngBase64));

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "look at the pixel"), CancellationToken.None);
        Assert.True(ack.Accepted);

        await WaitForReplyAsync(projection, "seen");

        Assert.Equal(2, harness.Model.Requests.Count);
        Assert.Empty(harness.Model.Requests[0].Messages.SelectMany(ImageBlocks));

        var followup = harness.Model.Requests[1].Messages.Last();
        Assert.Equal(Contracts.Conversation.MessageRole.User, followup.Role);
        var image = Assert.Single(ImageBlocks(followup));
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(TinyPngBase64, image.Base64Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualReadToolContinuation_LoadsImageOnce_AndReleasesPayloadAfterRequest(bool inline)
    {
        await using var harness = TestHarness.Create();
        var model = new ImageContinuationModel(inline);
        CountingArtifactStore artifacts = null!;
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root, transform: options => options with
        {
            Artifacts = artifacts = new CountingArtifactStore(options.Artifacts),
        });
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await File.WriteAllBytesAsync(Path.Combine(harness.Root, "pixel.png"), Convert.FromBase64String(TinyPngBase64));
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "look at the pixel"), CancellationToken.None)).Accepted);
        await WaitForReplyAsync(projection, "seen");
        await AwaitCurrentTurnAsync(runtime.GetSession(session.Id)!);

        Assert.Equal(2, model.Calls);
        Assert.Equal(1, Assert.Single(artifacts.Loads).Value);
        Assert.True(artifacts.ExistenceChecks.Values.Sum() > 0);
        var payload = Assert.IsType<WeakReference>(model.Payload);
        await AssertPayloadCollectedAsync(payload);
        GC.KeepAlive(runtime);
        GC.KeepAlive(model);
    }

    [Theory]
    [InlineData(false, "image/avif", "download.jpg")]
    [InlineData(true, "image/avif", "download.jpg")]
    [InlineData(false, "IMAGE/AVIF", "download.jpg")]
    [InlineData(true, "IMAGE/AVIF", "download.jpg")]
    [InlineData(false, "image/svg+xml", "vector.svg")]
    [InlineData(true, "image/svg+xml", "vector.svg")]
    [InlineData(false, "image/bmp", "bitmap.bmp")]
    [InlineData(true, "image/bmp", "bitmap.bmp")]
    public async Task UnsupportedMediaToolContinuation_CompletesWithoutLoadingOrSendingImage(
        bool inline, string mimeType, string fileName)
    {
        await using var harness = TestHarness.Create();
        var model = new UnsupportedMediaContinuationModel(inline);
        CountingArtifactStore artifacts = null!;
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root, transform: options => options with
        {
            Artifacts = artifacts = new CountingArtifactStore(options.Artifacts),
        }, extraTools: [new MediaArtifactTool(mimeType, fileName)]);
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "download the image"), CancellationToken.None)).Accepted);
        await AwaitCurrentTurnAsync(runtime.GetSession(session.Id)!);

        var events = await runtime.Options.Store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(events, item => item.Event is TurnFailedEvent);
        Assert.Equal(TurnOutcome.Completed, Assert.Single(events.Select(item => item.Event).OfType<TurnCompletedEvent>()).Outcome);
        Assert.Equal(2, model.Requests.Count);
        Assert.Empty(model.Requests.SelectMany(request => request.Messages).SelectMany(ImageBlocks));
        var result = Assert.Single(model.Requests[1].Messages.SelectMany(message => message.Content).OfType<InputToolResultBlock>());
        Assert.Equal($"downloaded\n[media file: {fileName}]", result.Content);
        Assert.False(result.IsError);
        Assert.Null(result.Images);
        Assert.Empty(artifacts.Loads);
        Assert.Empty(artifacts.ExistenceChecks);
        Assert.Contains(projection.Snapshot().Rows.OfType<AssistantMessageRow>(),
            row => row.Parts.OfType<TextPart>().Any(part => part.Text == "continued"));
        var stored = await runtime.Options.Store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var media = Assert.Single(stored.SelectMany(item => item.Message.Parts).OfType<ToolResultPart>()
            .SelectMany(part => part.Content).OfType<ToolResultMedia>());
        Assert.Equal(mimeType, media.MimeType);
        Assert.Equal(fileName, media.FileName);
    }

    [Fact]
    public async Task HydratedImagePayload_IsCollectibleWhileSessionRemainsResident()
    {
        await using var harness = TestHarness.Create();
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        var artifact = await runtime.Options.Artifacts.SaveAsync(session.Id, "pixel.png", "image/png",
            Convert.FromBase64String(TinyPngBase64), CancellationToken.None);
        var history = new[]
        {
            new ConversationMessage(StrongId.NewMessageId(), MessageRole.User,
                [new FilePart(StrongId.NewPartId(), artifact.Uri, artifact.MimeType, artifact.FileName)],
                DateTimeOffset.UtcNow),
        };
        var payload = HydrateWithoutRetainingRequest(runtime.GetSession(session.Id)!, history);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(payload.IsAlive);
        GC.KeepAlive(runtime);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference HydrateWithoutRetainingRequest(
        SessionRuntime session, IReadOnlyList<ConversationMessage> history)
    {
        var method = typeof(SessionRuntime).GetMethod("LoadSessionMediaAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var task = (Task<Dictionary<string, Alcoler.Kernel.Context.MediaPayload>?>)method.Invoke(
            session, [history, CancellationToken.None])!;
        var media = task.GetAwaiter().GetResult()!;
        Assert.Equal(TinyPngBase64, Assert.Single(media).Value.Base64Data);
        return new WeakReference(Assert.Single(media).Value.Base64Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetAssembly_UsesOnlyMetadata_AndMatchesHydratedAccountingForMissingDuplicateAndUnsupportedMedia(bool inline)
    {
        await using var harness = TestHarness.Create();
        var model = new InlineImageModel(harness.ScriptedModel, inline);
        CountingArtifactStore artifacts = null!;
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root, transform: options => options with
        {
            Artifacts = artifacts = new CountingArtifactStore(options.Artifacts),
        });
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        var fixture = await CreateMediaFixtureAsync(artifacts, session.Id);
        var sessionRuntime = runtime.GetSession(session.Id)!;
        var budget = await AssembleAsync(sessionRuntime, fixture.History, model, hydrate: false);
        Assert.Empty(artifacts.Loads);
        Assert.Equal(3, artifacts.ExistenceChecks.Count);
        Assert.All(artifacts.ExistenceChecks.Values, count => Assert.Equal(1, count));
        Assert.DoesNotContain(fixture.PdfUri, artifacts.ExistenceChecks.Keys);
        Assert.All(fixture.UnsupportedUris, uri => Assert.DoesNotContain(uri, artifacts.ExistenceChecks.Keys));
        Assert.Equal(4, budget.Request.Messages.SelectMany(ImageBlocks).Count());
        Assert.All(budget.Request.Messages.SelectMany(ImageBlocks), image => Assert.Empty(image.Base64Data));

        var hydrated = await AssembleAsync(sessionRuntime, fixture.History, model, hydrate: true);
        AssertMediaFixtureRequest(hydrated.Request, fixture, inline);
        Assert.Equal(budget.EstimatedRequestTokens, hydrated.EstimatedRequestTokens);
        Assert.Equal(budget.Breakdown, hydrated.Breakdown);
        Assert.Equal(TokenEstimator.EstimateRequestTokens(budget.Request), TokenEstimator.EstimateRequestTokens(hydrated.Request));
        Assert.Equal(budget.Request.Messages.Select(message => (message.Role, message.CacheControl)),
            hydrated.Request.Messages.Select(message => (message.Role, message.CacheControl)));
        Assert.Equal(3, artifacts.Loads.Count);
        Assert.All(artifacts.Loads.Values, count => Assert.Equal(1, count));
        Assert.DoesNotContain(fixture.PdfUri, artifacts.Loads.Keys);
        Assert.All(fixture.UnsupportedUris, uri => Assert.DoesNotContain(uri, artifacts.Loads.Keys));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeAndMicrocompaction_DoNotReadHistoricalImagesUntilActualRequest(bool microcompact)
    {
        await using var harness = TestHarness.Create();
        var model = new ScriptedCallModel([Reply("remembered")]);
        var clock = new ManualTimeProvider();
        SessionId sessionId;
        MediaFixture fixture;
        await using (var initial = TestHarness.BuildRuntimeFor(model, harness.Root, transform: options => options with { Clock = clock }))
        {
            sessionId = (await initial.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None)).Id;
            fixture = await CreateMediaFixtureAsync(initial.Options.Artifacts, sessionId);
            var history = fixture.History.ToList();
            if (microcompact)
            {
                var groups = CompactPolicy.MicroCompactKeepRecentGroups + 1;
                var threshold = CompactPolicy.AutoCompactThreshold(model.Descriptor.Properties.ContextWindowTokens!.Value);
                var charactersPerGroup = CompactPolicy.MicroCompactTriggerTokens(threshold) * 3 / groups + 3;
                for (var index = 0; index < groups; index++)
                {
                    var call = new ToolCallId($"clearable-{index}");
                    history.Add(new ConversationMessage(StrongId.NewMessageId(), MessageRole.Assistant,
                        [new ToolCallPart(StrongId.NewPartId(), call, "Read", JsonSerializer.SerializeToElement(new { file_path = "old.txt" }))], clock.GetUtcNow()));
                    history.Add(new ConversationMessage(StrongId.NewMessageId(), MessageRole.User,
                        [new ToolResultPart(StrongId.NewPartId(), call, [new ToolResultText(new string('x', charactersPerGroup))])], clock.GetUtcNow()));
                }
                Assert.Single(MicroCompactor.Apply(history, clock.GetUtcNow(), clock.GetUtcNow(), threshold).ClearedParts);
            }
            await initial.Options.Store.AppendMessagesAsync(sessionId, history, 1, CancellationToken.None);
        }
        CountingArtifactStore artifacts = null!;
        await using var resumed = TestHarness.BuildRuntimeFor(model, harness.Root, transform: options => options with
        {
            Clock = clock,
            Artifacts = artifacts = new CountingArtifactStore(options.Artifacts),
        });
        await resumed.ResumeSessionAsync(sessionId, CancellationToken.None);
        Assert.Empty(artifacts.Loads);
        Assert.All(artifacts.ExistenceChecks.Values, count => Assert.Equal(1, count));
        using var projection = resumed.Subscribe(sessionId);
        Assert.True((await resumed.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), sessionId, 0,
            "what did you see?"), CancellationToken.None)).Accepted);
        await WaitForReplyAsync(projection, "remembered");
        await AwaitCurrentTurnAsync(resumed.GetSession(sessionId)!);
        var request = Assert.Single(model.Requests);
        AssertMediaFixtureRequest(request, fixture, inline: false);
        Assert.Equal(3, artifacts.Loads.Count);
        Assert.All(artifacts.Loads.Values, count => Assert.Equal(1, count));
        var events = await resumed.Options.Store.LoadDurableEventsAsync(sessionId, CancellationToken.None);
        Assert.Equal(microcompact, events.Any(item => item.Event is MicrocompactAppliedEvent));
        Assert.DoesNotContain(events, item => item.Event is CompactBoundaryAppliedEvent);
        if (microcompact)
            Assert.Contains(request.Messages.SelectMany(message => message.Content).OfType<InputToolResultBlock>(),
                result => result.Content == MicroCompactor.ClearedContentText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCompaction_HydratesOnlyImageBearingSummaryRequest(bool mediaSizeRetry)
    {
        await using var harness = TestHarness.Create();
        IReadOnlyList<ModelStreamEvent> summary =
        [new ModelTextStarted(), new ModelTextDelta("compact summary"), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop)];
        var model = new ScriptedCallModel(mediaSizeRetry
            ? [[new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest, "image too large", ProviderCode: "image_too_large"))], summary]
            : [summary]);
        SessionId sessionId;
        MediaFixture fixture;
        await using (var initial = TestHarness.BuildRuntimeFor(model, harness.Root))
        {
            sessionId = (await initial.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None)).Id;
            fixture = await CreateMediaFixtureAsync(initial.Options.Artifacts, sessionId);
            await initial.Options.Store.AppendMessagesAsync(sessionId, fixture.History, 1, CancellationToken.None);
        }
        CountingArtifactStore artifacts = null!;
        await using var resumed = TestHarness.BuildRuntimeFor(model, harness.Root, transform: options => options with
        {
            Artifacts = artifacts = new CountingArtifactStore(options.Artifacts),
        });
        await resumed.ResumeSessionAsync(sessionId, CancellationToken.None);
        Assert.Empty(artifacts.Loads);
        var ack = await resumed.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), sessionId, 0), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        Assert.Equal(mediaSizeRetry ? 2 : 1, model.Requests.Count);
        AssertMediaFixtureRequest(model.Requests[0], fixture, inline: false);
        if (mediaSizeRetry)
        {
            Assert.Empty(model.Requests[1].Messages.SelectMany(ImageBlocks));
            Assert.Contains(model.Requests[1].Messages.SelectMany(message => message.Content).OfType<InputTextBlock>(),
                text => text.Text == "[image]");
        }
        Assert.Equal(3, artifacts.Loads.Count);
        Assert.All(artifacts.Loads.Values, count => Assert.Equal(1, count));
        var events = await resumed.Options.Store.LoadDurableEventsAsync(sessionId, CancellationToken.None);
        Assert.Single(events, item => item.Event is CompactBoundaryAppliedEvent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("describe this pixel")]
    public async Task UserImages_AreArtifactBackedAndReplayedAfterResume(string text)
    {
        await using var harness = ScriptedCallHarness.Create(Reply("seen"), Reply("remembered"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            text, Images: [new("IMAGE/PNG", TinyPngBase64)]), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitForReplyAsync(projection, "seen");

        var input = Assert.Single(harness.Model.Requests[0].Messages, message => message.Content.OfType<InputImageBlock>().Any());
        Assert.Equal(MessageRole.User, input.Role);
        var image = Assert.Single(input.Content.OfType<InputImageBlock>());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(TinyPngBase64, image.Base64Data);
        if (text.Length > 0)
            Assert.Equal(text, Assert.Single(input.Content.OfType<InputTextBlock>()).Text);
        else
            Assert.Empty(input.Content.OfType<InputTextBlock>());

        var row = Assert.Single(projection.Snapshot().Rows.OfType<UserMessageRow>());
        Assert.Equal(text, row.Text);
        var reference = Assert.Single(row.Images!);
        Assert.StartsWith("alcoler-artifact://", reference.ArtifactUri);
        await using var store = harness.OpenStore();
        var stored = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var user = Assert.Single(stored, message => message.Message.Source == MessageSource.RealUser).Message;
        Assert.Equal(reference, Assert.Single(user.Parts.OfType<FilePart>()));
        Assert.DoesNotContain(TinyPngBase64, AlcolerJson.Serialize(user), StringComparison.Ordinal);
        await runtime.DisposeAsync();

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var replay = resumed.Subscribe(session.Id);
        Assert.Equal(reference, Assert.Single(Assert.Single(replay.Snapshot().Rows.OfType<UserMessageRow>()).Images!));
        Assert.True((await resumed.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "what did you see?"), CancellationToken.None)).Accepted);
        await WaitForReplyAsync(replay, "remembered");
        Assert.Equal(TinyPngBase64, Assert.Single(harness.Model.Requests[1].Messages.SelectMany(ImageBlocks)).Base64Data);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LargeUserImages_ResumePreservesProviderUsageAndAvoidsAutoCompaction(bool reportsContextUsage)
    {
        var firstUsage = new TokenUsage(InputTokens: 1500, OutputTokens: 60, CacheReadTokens: 900,
            ContextUsageTokens: reportsContextUsage ? 1500 : null);
        var secondUsage = new TokenUsage(InputTokens: 1600, OutputTokens: 70, CacheReadTokens: 1100,
            ContextUsageTokens: reportsContextUsage ? 1600 : null);
        var thirdUsage = new TokenUsage(InputTokens: 1700, OutputTokens: 80, CacheReadTokens: 1200,
            ContextUsageTokens: reportsContextUsage ? 1700 : null);
        await using var harness = ScriptedCallHarness.Create(
            Reply("first seen", firstUsage), Reply("second seen", secondUsage), Reply("still remembered", thirdUsage));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var images = Enumerable.Range(0, 2).Select(index =>
        {
            var payload = new byte[1024 * 1024];
            Convert.FromBase64String(TinyPngBase64).CopyTo(payload, 0);
            payload[^1] = (byte)(index + 1);
            return new InputImageBlock("image/png", Convert.ToBase64String(payload));
        }).ToArray();
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "look at these images", Images: images), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitForReplyAsync(projection, "first seen");
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "remember them"), CancellationToken.None)).Accepted);
        await WaitForReplyAsync(projection, "second seen");

        Assert.Equal(2, harness.Model.Requests.Count);
        Assert.All(harness.Model.Requests, request => Assert.Equal(images, request.Messages.SelectMany(ImageBlocks)));
        var prior = Assert.IsType<ContextUsageState>(projection.Snapshot().State.ContextUsage);
        Assert.Equal(1600, prior.UsedTokens);
        Assert.Equal(TokenEstimator.ProviderUsageSource, prior.TokenSource);
        Assert.Equal(secondUsage, prior.LastUsage);
        Assert.Equal(2000, prior.CacheReadTokens);
        Assert.Equal(3100, prior.CacheInputTokens);
        await using var store = harness.OpenStore();
        var stored = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var imageMessage = Assert.Single(stored, message => message.Message.Parts.OfType<FilePart>().Any()).Message;
        var references = imageMessage.Parts.OfType<FilePart>().ToArray();
        Assert.Equal(images.Length, references.Length);
        Assert.All(references, reference => Assert.StartsWith("alcoler-artifact://", reference.ArtifactUri));
        var serialized = AlcolerJson.Serialize(imageMessage);
        Assert.All(images, image => Assert.DoesNotContain(image.Base64Data, serialized, StringComparison.Ordinal));
        var lastAssistant = stored.Last(message => message.Message.Role == MessageRole.Assistant).Message;
        await runtime.DisposeAsync();

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var replay = resumed.Subscribe(session.Id);
        var restored = Assert.IsType<ContextUsageState>(replay.Snapshot().State.ContextUsage);
        var assistantTail = reportsContextUsage ? 0 : TokenEstimator.EstimateMessageTokens(lastAssistant);
        Assert.Equal(prior.UsedTokens + assistantTail, restored.UsedTokens);
        Assert.Equal(TokenEstimator.ProviderUsageSource, restored.TokenSource);
        Assert.Equal(secondUsage, restored.LastUsage);
        Assert.Equal(prior.CacheReadTokens, restored.CacheReadTokens);
        Assert.Equal(prior.CacheInputTokens, restored.CacheInputTokens);
        Assert.Equal(prior.OutputTokens, restored.OutputTokens);
        Assert.Equal(prior.GenerationDurationMs, restored.GenerationDurationMs);
        Assert.Equal(references, Assert.Single(replay.Snapshot().Rows.OfType<UserMessageRow>(), row => row.Images is { Count: > 0 }).Images!);
        var highWater = (await store.LoadDurableEventsAsync(session.Id, CancellationToken.None)).Max(entry => entry.Sequence);

        Assert.True((await resumed.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "what did you see?"), CancellationToken.None)).Accepted);
        await WaitForReplyAsync(replay, "still remembered");
        Assert.Equal(3, harness.Model.Requests.Count);
        Assert.Equal(images, harness.Model.Requests[2].Messages.SelectMany(ImageBlocks));
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(events, entry => entry.Event is CompactBoundaryAppliedEvent or MicrocompactAppliedEvent or TurnFailedEvent);
        var completedTurns = events.Select(entry => entry.Event).OfType<TurnCompletedEvent>().ToArray();
        Assert.Equal(3, completedTurns.Length);
        Assert.All(completedTurns, turn => Assert.Equal(TurnOutcome.Completed, turn.Outcome));
        var preRequest = events.Where(entry => entry.Sequence > highWater).Select(entry => entry.Event)
            .OfType<ContextUsageUpdatedEvent>().First().Usage;
        var continuedMessages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var newTail = continuedMessages.Skip(stored.Count).Select(message => message.Message)
            .TakeWhile(message => message.Role != MessageRole.Assistant).Sum(TokenEstimator.EstimateMessageTokens);
        Assert.Equal(restored.UsedTokens + newTail, preRequest.UsedTokens);
        Assert.Equal(TokenEstimator.ProviderUsageSource, preRequest.TokenSource);
        Assert.Equal(secondUsage, preRequest.LastUsage);
        Assert.Equal(prior.CacheReadTokens, preRequest.CacheReadTokens);
        Assert.Equal(prior.CacheInputTokens, preRequest.CacheInputTokens);
        var continued = Assert.IsType<ContextUsageState>(replay.Snapshot().State.ContextUsage);
        Assert.Equal(1700, continued.UsedTokens);
        Assert.Equal(thirdUsage, continued.LastUsage);
        Assert.Equal(3200, continued.CacheReadTokens);
        Assert.Equal(4800, continued.CacheInputTokens);
        Assert.All(new[] { prior, restored, preRequest, continued }, usage =>
            Assert.InRange(Assert.Single(usage.Breakdown, category => category.Source == ContextUsageSources.Messages).Characters, 1L, 10_000L));
    }

    [Theory]
    [InlineData("image/svg+xml", TinyPngBase64, "unsupported_image_mime_type")]
    [InlineData("image/avif", TinyPngBase64, "unsupported_image_mime_type")]
    [InlineData("IMAGE/AVIF", TinyPngBase64, "unsupported_image_mime_type")]
    [InlineData("image/bmp", TinyPngBase64, "unsupported_image_mime_type")]
    [InlineData("image/png", "not-base64!", "invalid_image_base64")]
    [InlineData("image/png", "", "invalid_image_base64")]
    [InlineData("image/png", "   ", "invalid_image_base64")]
    [InlineData("", TinyPngBase64, "unsupported_image_mime_type")]
    [InlineData("image/png", "data:image/png;base64,YQ==", "invalid_image_base64")]
    public async Task InvalidUserImage_IsRejectedBeforeAdmission(string mimeType, string base64, string reason)
    {
        await using var harness = ScriptedCallHarness.Create();
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "invalid image", Images: [new(mimeType, base64)]), CancellationToken.None);
        Assert.False(ack.Accepted);
        Assert.Equal(reason, ack.RejectionReason);
        Assert.Empty(harness.Model.Requests);
        Assert.Empty(projection.Snapshot().Rows);
        Assert.False(Directory.Exists(Path.Combine(harness.Root, "artifacts")));
        await using var store = harness.OpenStore();
        Assert.Empty(await store.LoadMessagesAsync(session.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserImageLimits_AreRejectedWithoutPersistingPayloads(bool tooMany)
    {
        await using var harness = ScriptedCallHarness.Create();
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        var images = tooMany
            ? Enumerable.Repeat(new InputImageBlock("image/png", TinyPngBase64), 9).ToArray()
            : new[] { new InputImageBlock("image/png", Convert.ToBase64String(new byte[20 * 1024 * 1024 + 1])) };
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "invalid images", Images: images), CancellationToken.None);
        Assert.False(ack.Accepted);
        Assert.Equal(tooMany ? "too_many_images" : "image_too_large", ack.RejectionReason);
        Assert.Empty(harness.Model.Requests);
        Assert.False(Directory.Exists(Path.Combine(harness.Root, "artifacts")));
    }

    [Fact]
    public async Task EightUserImages_AreDeliveredInOrder()
    {
        await using var harness = ScriptedCallHarness.Create(Reply("eight seen"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var images = Enumerable.Range(0, 8).Select(index => new InputImageBlock("image/png",
            Convert.ToBase64String([.. Convert.FromBase64String(TinyPngBase64), (byte)index]))).ToArray();
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "", Images: images), CancellationToken.None);
        Assert.True(ack.Accepted);
        await WaitForReplyAsync(projection, "eight seen");
        Assert.Equal(images, harness.Model.Requests[0].Messages.SelectMany(ImageBlocks));
        Assert.Equal(8, Assert.Single(projection.Snapshot().Rows.OfType<UserMessageRow>()).Images!.Count);
    }

    private sealed record MediaFixture(
        IReadOnlyList<ConversationMessage> History, string FirstUri, string SecondUri, string MissingUri, string PdfUri,
        IReadOnlyList<string> UnsupportedUris);

    private static async Task<MediaFixture> CreateMediaFixtureAsync(IArtifactStore artifacts, SessionId session)
    {
        var first = await artifacts.SaveAsync(session, "pixel.png", "image/png", Convert.FromBase64String(TinyPngBase64), CancellationToken.None);
        var second = await artifacts.SaveAsync(session, "second.jpg", "image/jpeg", new byte[] { 1, 2, 3 }, CancellationToken.None);
        var pdf = await artifacts.SaveAsync(session, "report.pdf", "application/pdf", new byte[] { 4 }, CancellationToken.None);
        var unsupported = new List<ArtifactReference>();
        foreach (var (mimeType, fileName) in new[]
        {
            ("IMAGE/AVIF", "avif.jpg"),
            ("image/svg+xml", "vector.svg"),
            ("image/bmp", "bitmap.bmp"),
        })
            unsupported.Add(await artifacts.SaveAsync(session, fileName, mimeType, new byte[] { 5 }, CancellationToken.None));
        var missing = $"alcoler-artifact://{session.Value}/missing.png";
        var at = DateTimeOffset.UtcNow;
        ConversationMessage[] history =
        [
            new(StrongId.NewMessageId(), MessageRole.User,
            [
                new FilePart(StrongId.NewPartId(), first.Uri, first.MimeType, first.FileName),
                new FilePart(StrongId.NewPartId(), first.Uri, first.MimeType, first.FileName),
                new FilePart(StrongId.NewPartId(), missing, "image/png", "missing.png"),
                new FilePart(StrongId.NewPartId(), pdf.Uri, pdf.MimeType, pdf.FileName),
                .. unsupported.Select(artifact => new FilePart(StrongId.NewPartId(), artifact.Uri, artifact.MimeType, artifact.FileName)),
            ], at, Source: MessageSource.RealUser),
            new(StrongId.NewMessageId(), MessageRole.Assistant,
                [new ToolCallPart(StrongId.NewPartId(), ReadCallId, "Read", JsonSerializer.SerializeToElement(new { file_path = "pixel.png" }))], at),
            new(StrongId.NewMessageId(), MessageRole.User,
                [new ToolResultPart(StrongId.NewPartId(), ReadCallId,
                [
                    new ToolResultText("caption"),
                    new ToolResultMedia(first.Uri, first.MimeType, first.FileName),
                    new ToolResultMedia(missing, "image/png", "missing.png"),
                    new ToolResultMedia(pdf.Uri, pdf.MimeType, pdf.FileName),
                    new ToolResultMedia(second.Uri, second.MimeType, second.FileName),
                    .. unsupported.Select(artifact => new ToolResultMedia(artifact.Uri, artifact.MimeType, artifact.FileName)),
                ])], at),
        ];
        return new MediaFixture(history, first.Uri, second.Uri, missing, pdf.Uri,
            unsupported.Select(artifact => artifact.Uri).ToArray());
    }

    private static void AssertMediaFixtureRequest(ModelRequest request, MediaFixture fixture, bool inline)
    {
        var images = request.Messages.SelectMany(ImageBlocks).ToArray();
        Assert.Equal(new[] { "image/png", "image/png", "image/png", "image/jpeg" }, images.Select(image => image.MimeType));
        Assert.Equal(new[] { TinyPngBase64, TinyPngBase64, TinyPngBase64, "AQID" }, images.Select(image => image.Base64Data));
        Assert.Same(images[0].Base64Data, images[1].Base64Data);
        Assert.Same(images[0].Base64Data, images[2].Base64Data);
        var result = Assert.Single(request.Messages.SelectMany(message => message.Content).OfType<InputToolResultBlock>(),
            item => item.Id == ReadCallId);
        Assert.Equal("caption\n[media file: missing.png]\n[media file: report.pdf]\n[media file: avif.jpg]\n[media file: vector.svg]\n[media file: bitmap.bmp]", result.Content);
        Assert.Equal(inline, result.Images is { Count: 2 });
        foreach (var fileName in new[] { "avif.jpg", "vector.svg", "bitmap.bmp" })
            Assert.Contains(request.Messages.SelectMany(message => message.Content).OfType<InputTextBlock>(),
                text => text.Text == $"[media file: {fileName}]");
        Assert.Contains(request.Messages.SelectMany(message => message.Content).OfType<InputTextBlock>(),
            text => text.Text == "[media file: missing.png]");
        Assert.DoesNotContain(fixture.MissingUri, images.Select(image => image.Base64Data));
    }

    private static Task<AssembledContext> AssembleAsync(SessionRuntime session, IReadOnlyList<ConversationMessage> history,
        IChatModel model, bool hydrate)
    {
        var method = typeof(SessionRuntime).GetMethod("AssembleContextAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task<AssembledContext>)method.Invoke(session,
            [history, new ModelCallOptions(), model, CancellationToken.None, hydrate])!;
    }

    private sealed class InlineImageModel(IChatModel inner, bool inline) : IChatModel
    {
        public ModelDescriptor Descriptor => inner.Descriptor;
        public bool InlineToolResultImages => inline;

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken) =>
            inner.StreamAsync(request, cancellationToken);
    }

    private sealed class CountingArtifactStore(IArtifactStore inner) : IArtifactStore
    {
        public ConcurrentDictionary<string, int> Loads { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, int> ExistenceChecks { get; } = new(StringComparer.Ordinal);

        public ValueTask<ArtifactReference> SaveAsync(SessionId sessionId, string fileName, string mimeType,
            ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            inner.SaveAsync(sessionId, fileName, mimeType, data, cancellationToken);

        public ValueTask<bool> ExistsAsync(string artifactUri, CancellationToken cancellationToken)
        {
            ExistenceChecks.AddOrUpdate(artifactUri, 1, (_, count) => count + 1);
            return inner.ExistsAsync(artifactUri, cancellationToken);
        }

        public ValueTask<ReadOnlyMemory<byte>?> TryLoadAsync(string artifactUri, CancellationToken cancellationToken)
        {
            Loads.AddOrUpdate(artifactUri, 1, (_, count) => count + 1);
            return inner.TryLoadAsync(artifactUri, cancellationToken);
        }
    }

    private sealed class MediaArtifactTool(string mimeType, string fileName) : ITool
    {
        public const string ToolName = "MediaArtifact";

        public ToolContract Contract { get; } = new(ToolName, "Returns a media artifact.",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }));

        public ToolMetadata Metadata { get; } = new(
            ReadOnly: true, Destructive: false, ConcurrentSafe: true, RequiresUserInteraction: false,
            SideEffectScope: SideEffectScope.None, RiskLevel: RiskLevel.Low, NeedsApproval: false,
            AllowedInPlanMode: true);

        public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var artifact = await context.Workspace.SaveArtifactAsync(context.SessionId, fileName, mimeType,
                new byte[] { 1, 2, 3 }, cancellationToken);
            return new ToolExecutionResult(
                [new ToolResultText("downloaded"), new ToolResultMedia(artifact.Uri, artifact.MimeType, artifact.FileName)]);
        }
    }

    private sealed class UnsupportedMediaContinuationModel(bool inline) : IChatModel
    {
        public ModelDescriptor Descriptor { get; } = new("scripted", "test-model", "Unsupported Media Continuation",
            new ModelProperties(ContextWindowTokens: 200_000));
        public bool InlineToolResultImages => inline;
        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                var callId = new ToolCallId("call-media");
                var input = JsonSerializer.SerializeToElement(new { });
                yield return new ModelToolInputStarted(callId, MediaArtifactTool.ToolName);
                yield return new ModelToolInputDelta(callId, "{}");
                yield return new ModelToolInputEnded(callId);
                yield return new ModelToolCallReady(new ToolCall(callId, MediaArtifactTool.ToolName, input));
                yield return new ModelStreamFinished(ModelFinishReason.ToolCalls);
                yield break;
            }
            if (request.Messages.SelectMany(ImageBlocks).Any())
            {
                yield return new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest,
                    "unsupported image MIME type", ProviderCode: "unsupported_image_mime_type"));
                yield break;
            }
            yield return new ModelTextDelta("continued");
            yield return new ModelStreamFinished(ModelFinishReason.Stop);
        }
    }

    private sealed class ImageContinuationModel(bool inline) : IChatModel
    {
        public ModelDescriptor Descriptor { get; } = new("scripted", "test-model", "Image Continuation",
            new ModelProperties(ContextWindowTokens: 200_000));
        public bool InlineToolResultImages => inline;
        public int Calls { get; private set; }
        public WeakReference? Payload { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1)
            {
                Assert.Empty(request.Messages.SelectMany(ImageBlocks));
                foreach (var item in ReadImageCall())
                    yield return item;
                yield break;
            }
            Assert.Equal(2, Calls);
            var image = Assert.Single(request.Messages.SelectMany(ImageBlocks));
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(TinyPngBase64, image.Base64Data);
            var result = Assert.Single(request.Messages.SelectMany(message => message.Content).OfType<InputToolResultBlock>());
            Assert.Equal(inline, result.Images is { Count: 1 });
            Assert.Equal("", result.Content);
            Payload = new WeakReference(image.Base64Data);
            yield return new ModelTextDelta("seen");
            yield return new ModelStreamFinished(ModelFinishReason.Stop);
        }
    }

    // Task completion can precede unwinding the producer's request-owning stack.
    private static async Task AssertPayloadCollectedAsync(WeakReference payload)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (PayloadAliveAfterCollection(payload))
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
                "The completed model request retained its image payload.");
            await Task.Delay(10);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool PayloadAliveAfterCollection(WeakReference payload)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return payload.IsAlive;
    }

    private static Task AwaitCurrentTurnAsync(SessionRuntime session) =>
        (Task)typeof(SessionRuntime).GetField("_turnTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;

    private static IReadOnlyList<ModelStreamEvent> Reply(string text, TokenUsage? usage = null) =>
        [new ModelTextDelta(text), new ModelStreamFinished(ModelFinishReason.Stop, usage)];

    private static async Task WaitForReplyAsync(IConversationProjection projection, string text)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (projection.Snapshot() is var snapshot && (snapshot.State.Phase != TurnPhase.Idle ||
            !snapshot.Rows.OfType<AssistantMessageRow>().Any(row => row.Parts.OfType<TextPart>().Any(part => part.Text == text))))
            await Task.Delay(10, timeout.Token);
    }

    private static IReadOnlyList<ModelStreamEvent> ReadImageCall()
    {
        var input = JsonSerializer.SerializeToElement(new { file_path = "pixel.png" });
        return
        [
            new ModelToolInputStarted(ReadCallId, "Read"),
            new ModelToolInputDelta(ReadCallId, JsonSerializer.Serialize(input)),
            new ModelToolInputEnded(ReadCallId),
            new ModelToolCallReady(new ToolCall(ReadCallId, "Read", input)),
            new ModelStreamFinished(ModelFinishReason.ToolCalls),
        ];
    }

    private static IEnumerable<InputImageBlock> ImageBlocks(ModelInputMessage message) =>
        message.Content.OfType<InputImageBlock>().Concat(message.Content.OfType<InputToolResultBlock>()
            .SelectMany(result => result.Images ?? []));
}
