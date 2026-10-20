using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

public abstract record ToolExecutionNotification;

public sealed record ToolExecutionStarted(ToolCall Call) : ToolExecutionNotification;

public sealed record ToolExecutionPermissionAsked(ToolCall Call, PermissionRequest Request) : ToolExecutionNotification;

public sealed record ToolExecutionResultNotification(ToolCall Call, ToolExecutionResult Result, TimeSpan Duration)
    : ToolExecutionNotification;
