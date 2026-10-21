using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;

namespace Alcoler.Contracts.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ConversationMessage))]
[JsonSerializable(typeof(MessagePart))]
[JsonSerializable(typeof(ToolResultBlock))]
[JsonSerializable(typeof(CompactBoundary))]
[JsonSerializable(typeof(ModelSelection))]
[JsonSerializable(typeof(ModelDescriptor))]
[JsonSerializable(typeof(ModelRequest))]
[JsonSerializable(typeof(ModelInputMessage))]
[JsonSerializable(typeof(ModelContentBlock))]
[JsonSerializable(typeof(ModelStreamEvent))]
[JsonSerializable(typeof(TokenUsage))]
[JsonSerializable(typeof(ModelFailure))]
[JsonSerializable(typeof(ToolCall))]
[JsonSerializable(typeof(SessionEvent))]
[JsonSerializable(typeof(SequencedEvent))]
[JsonSerializable(typeof(InteractionRequest))]
[JsonSerializable(typeof(InteractionAnswer))]
[JsonSerializable(typeof(ConversationCommand))]
[JsonSerializable(typeof(CommandAck))]
[JsonSerializable(typeof(ConversationSnapshot))]
[JsonSerializable(typeof(ConversationDelta))]
[JsonSerializable(typeof(ConversationRow))]
[JsonSerializable(typeof(RowOp))]
[JsonSerializable(typeof(ConversationState))]
[JsonSerializable(typeof(ShellEnvironment))]
[JsonSerializable(typeof(SessionRecord))]
[JsonSerializable(typeof(StoredMessage))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(SessionOptions))]
[JsonSerializable(typeof(SessionHandle))]
[JsonSerializable(typeof(QueuedInput))]
public sealed partial class AlcolerJsonContext : JsonSerializerContext;

public static class AlcolerJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = AlcolerJsonContext.Default,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new StrongIdJsonConverterFactory());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
