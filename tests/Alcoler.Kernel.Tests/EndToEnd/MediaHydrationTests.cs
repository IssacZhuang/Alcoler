using System.Text.Json;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
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

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (projection.Snapshot() is { State.Phase: not TurnPhase.Idle })
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.Equal(2, harness.Model.Requests.Count);
        Assert.Empty(harness.Model.Requests[0].Messages.SelectMany(ImageBlocks));

        var followup = harness.Model.Requests[1].Messages.Last();
        Assert.Equal(Contracts.Conversation.MessageRole.User, followup.Role);
        var image = Assert.Single(ImageBlocks(followup));
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(TinyPngBase64, image.Base64Data);
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
        message.Content.OfType<InputImageBlock>();
}
