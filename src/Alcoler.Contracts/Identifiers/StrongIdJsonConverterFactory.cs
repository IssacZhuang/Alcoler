using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alcoler.Contracts.Identifiers;

public sealed class StrongIdJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsValueType && typeof(IStrongId).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        typeToConvert switch
        {
            _ when typeToConvert == typeof(SessionId) => new StrongIdJsonConverter<SessionId>(static v => new SessionId(v)),
            _ when typeToConvert == typeof(MessageId) => new StrongIdJsonConverter<MessageId>(static v => new MessageId(v)),
            _ when typeToConvert == typeof(PartId) => new StrongIdJsonConverter<PartId>(static v => new PartId(v)),
            _ when typeToConvert == typeof(TurnId) => new StrongIdJsonConverter<TurnId>(static v => new TurnId(v)),
            _ when typeToConvert == typeof(CommandId) => new StrongIdJsonConverter<CommandId>(static v => new CommandId(v)),
            _ when typeToConvert == typeof(InteractionId) => new StrongIdJsonConverter<InteractionId>(static v => new InteractionId(v)),
            _ when typeToConvert == typeof(ToolCallId) => new StrongIdJsonConverter<ToolCallId>(static v => new ToolCallId(v)),
            _ when typeToConvert == typeof(SubagentId) => new StrongIdJsonConverter<SubagentId>(static v => new SubagentId(v)),
            _ when typeToConvert == typeof(BackgroundJobId) => new StrongIdJsonConverter<BackgroundJobId>(static v => new BackgroundJobId(v)),
            _ => throw new NotSupportedException($"No strong id converter is registered for {typeToConvert.Name}."),
        };

    private sealed class StrongIdJsonConverter<T>(Func<string, T> create) : JsonConverter<T> where T : struct, IStrongId
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value is null
                ? throw new JsonException($"Expected string for {typeof(T).Name}.")
                : create(value);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
