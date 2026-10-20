namespace Alcoler.Contracts.Identifiers;

public interface IStrongId
{
    string Value { get; }
}

public readonly record struct SessionId(string Value) : IStrongId
{
    public override string ToString() => Value;
}

public readonly record struct MessageId(string Value) : IStrongId
{
    public override string ToString() => Value;
}

public readonly record struct PartId(string Value) : IStrongId
{
    public override string ToString() => Value;
}

public readonly record struct TurnId(string Value) : IStrongId
{
    public override string ToString() => Value;
}

public readonly record struct CommandId(string Value) : IStrongId
{
    public override string ToString() => Value;
}

public readonly record struct InteractionId(string Value) : IStrongId
{
    public override string ToString() => Value;
}

public readonly record struct ToolCallId(string Value) : IStrongId
{
    public override string ToString() => Value;
}

public readonly record struct SubagentId(string Value) : IStrongId
{
    public override string ToString() => Value;
}

public static class StrongId
{
    public static SessionId NewSessionId() => new($"sess_{Guid.NewGuid():N}");
    public static MessageId NewMessageId() => new($"msg_{Guid.NewGuid():N}");
    public static PartId NewPartId() => new($"part_{Guid.NewGuid():N}");
    public static TurnId NewTurnId() => new($"turn_{Guid.NewGuid():N}");
    public static CommandId NewCommandId() => new(Guid.CreateVersion7().ToString("N"));
    public static InteractionId NewInteractionId() => new($"ia_{Guid.NewGuid():N}");
    public static ToolCallId NewToolCallId() => new($"call_{Guid.NewGuid():N}");
    public static SubagentId NewSubagentId() => new($"sub_{Guid.NewGuid():N}");
}
