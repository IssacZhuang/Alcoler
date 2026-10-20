using Alcoler.Contracts.Models;

namespace Alcoler.Kernel.Tools;

public sealed record ScheduledToolCall(ToolCall Call, bool StopTurnAfterResult = false);
