using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;

namespace Alcoler.Contracts.Commands;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SendTextCommand), "send_text")]
[JsonDerivedType(typeof(SendQueuedNowCommand), "send_queued_now")]
[JsonDerivedType(typeof(RemoveQueuedInputCommand), "remove_queued_input")]
[JsonDerivedType(typeof(StopCommand), "stop")]
[JsonDerivedType(typeof(CompactCommand), "compact")]
[JsonDerivedType(typeof(RewindConversationCommand), "rewind_conversation")]
[JsonDerivedType(typeof(ResolveInteractionCommand), "resolve_interaction")]
[JsonDerivedType(typeof(SwitchModelCommand), "switch_model")]
[JsonDerivedType(typeof(SetModeCommand), "set_mode")]
[JsonDerivedType(typeof(SetWorkflowsCommand), "set_workflows")]
[JsonDerivedType(typeof(ExitPlanModeCommand), "exit_plan_mode")]
[JsonDerivedType(typeof(RenameSessionCommand), "rename_session")]
[JsonDerivedType(typeof(DeleteSessionCommand), "delete_session")]
public abstract record ConversationCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision);

public enum SendTextDelivery { Queue, Guide }

public sealed record SendTextCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    string Text,
    PermissionMode? Mode = null,
    ModelSelection? ModelSelection = null,
    SendTextDelivery Delivery = SendTextDelivery.Queue,
    IReadOnlyList<InputImageBlock>? Images = null,
    IReadOnlyList<string>? Workflows = null,
    bool? PlanEnabled = null) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record SendQueuedNowCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    CommandId TargetCommandId) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record RemoveQueuedInputCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    CommandId TargetCommandId) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record StopCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    string? Reason = null) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record CompactCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    string? Instructions = null) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record RewindConversationCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    MessageId TargetMessageId) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record ResolveInteractionCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    InteractionId InteractionId,
    PermissionDecision? PermissionDecision = null,
    InteractionAnswer? InteractionAnswer = null) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record SwitchModelCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    ModelSelection Selection) : ConversationCommand(CommandId, SessionId, BaseRevision);

// Mode null leaves the permission mode untouched; PlanEnabled null leaves plan mode untouched.
public sealed record SetModeCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    PermissionMode? Mode = null,
    bool? PlanEnabled = null) : ConversationCommand(CommandId, SessionId, BaseRevision);

// Replaces the full active-workflow set; null entries and duplicates are rejected upstream.
public sealed record SetWorkflowsCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    IReadOnlyList<string> Active) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record ExitPlanModeCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    string Plan) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record RenameSessionCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    string Title) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record DeleteSessionCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision) : ConversationCommand(CommandId, SessionId, BaseRevision);

public sealed record CommandAck(
    CommandId CommandId,
    bool Accepted,
    long Revision,
    string? RejectionReason = null)
{
    // Store tail captured when the ack was produced; every event the command caused is at
    // or below this sequence, so a projection can await it before reading state.
    public long LastEventSequence { get; init; }

    public static CommandAck AcceptedAt(CommandId commandId, long revision) =>
        new(commandId, true, revision);

    public static CommandAck Rejected(CommandId commandId, long revision, string reason) =>
        new(commandId, false, revision, reason);
}
