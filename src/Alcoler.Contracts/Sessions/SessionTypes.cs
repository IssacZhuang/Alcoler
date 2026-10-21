using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;

namespace Alcoler.Contracts.Sessions;

public enum SessionTitleSource
{
    Default,
    FirstInput,
    Generated,
    Custom,
}

public sealed record SessionOptions(
    string WorkspaceRoot,
    string? Title = null,
    PermissionMode PermissionMode = PermissionMode.Build,
    ModelSelection? ModelSelection = null,
    SessionId? ParentSessionId = null);

public sealed record SessionHandle(
    SessionId Id,
    string WorkspaceRoot,
    string Title);

public sealed record SessionSummary(
    SessionId Id,
    string Title,
    string WorkspaceRoot,
    PermissionMode PermissionMode,
    ModelSelection? ModelSelection,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsArchived = false,
    SessionId? ParentSessionId = null);
