using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;

namespace Alcoler.Contracts.Commands;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SendTextCommand), "send_text")]
[JsonDerivedType(typeof(StopCommand), "stop")]
[JsonDerivedType(typeof(CompactCommand), "compact")]
[JsonDerivedType(typeof(ResolveInteractionCommand), "resolve_interaction")]
[JsonDerivedType(typeof(SwitchModelCommand), "switch_model")]
[JsonDerivedType(typeof(SetModeCommand), "set_mode")]
[JsonDerivedType(typeof(RenameSessionCommand), "rename_session")]
[JsonDerivedType(typeof(DeleteSessionCommand), "delete_session")]
public abstract record ConversationCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision);

public sealed record SendTextCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    string Text,
    PermissionMode? Mode = null,
    ModelSelection? ModelSelection = null) : ConversationCommand(CommandId, SessionId, BaseRevision);

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

public sealed record SetModeCommand(
    CommandId CommandId,
    SessionId SessionId,
    long BaseRevision,
    PermissionMode Mode) : ConversationCommand(CommandId, SessionId, BaseRevision);

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
    public static CommandAck AcceptedAt(CommandId commandId, long revision) =>
        new(commandId, true, revision);

    public static CommandAck Rejected(CommandId commandId, long revision, string reason) =>
        new(commandId, false, revision, reason);
}
