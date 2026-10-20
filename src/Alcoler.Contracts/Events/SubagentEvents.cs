using Alcoler.Contracts.Subagents;

namespace Alcoler.Contracts.Events;

public sealed record SubagentStartedEvent(SubagentState State) : SessionEvent;

public sealed record SubagentStatusChangedEvent(SubagentState State) : SessionEvent;
