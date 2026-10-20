using System.Text.Json;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

// Tool results reference images by artifact URI in stored history; the runtime hydrates
// them into image blocks on the followup request, mirroring ZCode's media replay.
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
