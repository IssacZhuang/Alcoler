using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Serialization;

public class SerializationSmokeTests
{
    [Fact]
    public void ConversationMessage_RoundTrips_AllPartTypes()
    {
        var input = JsonSerializer.SerializeToElement(new { path = "a.cs" });
        var message = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
        [
            new TextPart(StrongId.NewPartId(), "hello"),
                new ReasoningPart(StrongId.NewPartId(), "thinking", "sig-1"),
                new ToolCallPart(StrongId.NewPartId(), StrongId.NewToolCallId(), "Read", input),
                new ToolResultPart(StrongId.NewPartId(), StrongId.NewToolCallId(), [new ToolResultText("content")]),
                new FilePart(StrongId.NewPartId(), "alcoler-artifact://s/f.png", "image/png", "f.png"),
                new AttachmentPart(StrongId.NewPartId(), AttachmentKind.ContextPrefix, "# agents md"),
        ],
            DateTimeOffset.UtcNow);

        var json = AlcolerJson.Serialize(message);
        var roundTripped = AlcolerJson.Deserialize<ConversationMessage>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(message.Id, roundTripped.Id);
        Assert.Equal(6, roundTripped.Parts.Count);
        Assert.IsType<ReasoningPart>(roundTripped.Parts[1]);
        Assert.IsType<ToolCallPart>(roundTripped.Parts[2]);
        Assert.Equal("Read", ((ToolCallPart)roundTripped.Parts[2]).ToolName);
    }

    [Fact]
    public void ToolResultPart_StructuredOutput_RoundTripsAndReadsLegacyJsonWithoutIt()
    {
        var part = new ToolResultPart(
            StrongId.NewPartId(), StrongId.NewToolCallId(), [new ToolResultText("content")],
            StructuredOutput: """{"status":"success","messageCount":3}""");
        var message = new ConversationMessage(StrongId.NewMessageId(), MessageRole.User, [part], DateTimeOffset.UtcNow);

        var roundTripped = AlcolerJson.Deserialize<ConversationMessage>(AlcolerJson.Serialize(message));
        Assert.Equal(part.StructuredOutput, Assert.IsType<ToolResultPart>(roundTripped!.Parts[0]).StructuredOutput);

        var legacyJson = AlcolerJson.Serialize(message with
        {
            Parts = [part with { StructuredOutput = null }],
        }).Replace(",\"structuredOutput\":null", "", StringComparison.Ordinal);
        Assert.DoesNotContain("structuredOutput", legacyJson, StringComparison.Ordinal);
        Assert.Null(Assert.IsType<ToolResultPart>(
            AlcolerJson.Deserialize<ConversationMessage>(legacyJson)!.Parts[0]).StructuredOutput);
    }

    [Fact]
    public void SessionEvent_RoundTrips_WithDiscriminator()
    {
        SessionEvent sessionEvent = new ToolCallResultEvent(
            StrongId.NewTurnId(),
            StrongId.NewToolCallId(),
            "Bash",
            new ToolExecutionResult([new ToolResultText("ok")]),
            TimeSpan.FromSeconds(1));

        var json = AlcolerJson.Serialize(sessionEvent);
        var roundTripped = AlcolerJson.Deserialize<SessionEvent>(json);

        var result = Assert.IsType<ToolCallResultEvent>(roundTripped);
        Assert.Equal("Bash", result.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Duration);
    }

    [Fact]
    public void Command_RoundTrips_WithDiscriminator()
    {
        ConversationCommand command = new SendTextCommand(
            StrongId.NewCommandId(),
            StrongId.NewSessionId(),
            BaseRevision: 42,
            Text: "do it");

        var json = AlcolerJson.Serialize(command);
        var roundTripped = AlcolerJson.Deserialize<ConversationCommand>(json);

        var sendText = Assert.IsType<SendTextCommand>(roundTripped);
        Assert.Equal(42, sendText.BaseRevision);
        Assert.Equal("do it", sendText.Text);

        var target = StrongId.NewCommandId();
        ConversationCommand remove = new RemoveQueuedInputCommand(StrongId.NewCommandId(), StrongId.NewSessionId(), 7, target);
        var removeRoundTrip = Assert.IsType<RemoveQueuedInputCommand>(AlcolerJson.Deserialize<ConversationCommand>(
            AlcolerJson.Serialize(remove)));
        Assert.Equal(7, removeRoundTrip.BaseRevision);
        Assert.Equal(target, removeRoundTrip.TargetCommandId);
    }

    [Fact]
    public void ImageCommandAndArtifactMetadata_RoundTripAndReadLegacyJson()
    {
        var image = new InputImageBlock("image/png", "aW1hZ2U=");
        var command = new SendTextCommand(StrongId.NewCommandId(), StrongId.NewSessionId(), 0, "", Images: [image]);
        var restored = Assert.IsType<SendTextCommand>(AlcolerJson.Deserialize<ConversationCommand>(
            AlcolerJson.Serialize<ConversationCommand>(command)));
        Assert.Equal(image, Assert.Single(restored.Images!));
        var legacyCommandJson = AlcolerJson.Serialize<ConversationCommand>(command with { Images = null }).Replace(
            ",\"images\":null", "", StringComparison.Ordinal);
        Assert.Null(Assert.IsType<SendTextCommand>(AlcolerJson.Deserialize<ConversationCommand>(legacyCommandJson)).Images);

        var file = new FilePart(StrongId.NewPartId(), "alcoler-artifact://session/pixel.png", "image/png", "pixel.png");
        var queued = new QueuedInput(command.CommandId, "", DateTimeOffset.UtcNow, [file],
            PermissionMode.Edit, new ModelSelection("provider", "model"), SendTextDelivery.Guide);
        var queuedReplay = Assert.IsType<InputQueuedEvent>(AlcolerJson.Deserialize<SessionEvent>(
            AlcolerJson.Serialize<SessionEvent>(new InputQueuedEvent(queued)))).Input;
        Assert.Equal(file, Assert.Single(queuedReplay.Images!));
        Assert.Equal(queued.Mode, queuedReplay.Mode);
        Assert.Equal(queued.ModelSelection, queuedReplay.ModelSelection);
        Assert.Equal(SendTextDelivery.Guide, queuedReplay.Delivery);
        var legacyQueue = AlcolerJson.Deserialize<QueuedInput>(
            $$"""{"commandId":"{{command.CommandId.Value}}","text":"old","queuedAt":"2026-01-01T00:00:00Z"}""");
        Assert.NotNull(legacyQueue);
        Assert.Null(legacyQueue.Images);
        Assert.Null(legacyQueue.Mode);
        Assert.Null(legacyQueue.ModelSelection);
        Assert.Equal(SendTextDelivery.Queue, legacyQueue.Delivery);

        ConversationRow row = new UserMessageRow("user", "", DateTimeOffset.UtcNow, [file]);
        var rowReplay = Assert.IsType<UserMessageRow>(AlcolerJson.Deserialize<ConversationRow>(AlcolerJson.Serialize(row)));
        Assert.Equal(file, Assert.Single(rowReplay.Images!));
        var legacyRow = Assert.IsType<UserMessageRow>(AlcolerJson.Deserialize<ConversationRow>(
            """{"$type":"user_message","rowId":"old","text":"hello","at":"2026-01-01T00:00:00Z"}"""));
        Assert.Null(legacyRow.Images);
    }

    [Fact]
    public void Snapshot_RoundTrips_RowsAndState()
    {
        var snapshot = new ConversationSnapshot(
            Revision: 7,
            Sequence: 100,
        [
            new UserMessageRow("u1", "hi", DateTimeOffset.UtcNow),
                new ToolCallRow("t1", StrongId.NewToolCallId(), "Grep", JsonSerializer.SerializeToElement(new { }), ToolCallStatus.Succeeded, [new ToolResultText("x")], IsError: false, TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow),
        ],
            ConversationState.Initial("t", PermissionMode.Build, null, 200_000));

        var json = AlcolerJson.Serialize(snapshot);
        var roundTripped = AlcolerJson.Deserialize<ConversationSnapshot>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(7, roundTripped.Revision);
        Assert.IsType<ToolCallRow>(roundTripped.Rows[1]);
        Assert.Equal(ToolCallStatus.Succeeded, ((ToolCallRow)roundTripped.Rows[1]).Status);
    }
}
