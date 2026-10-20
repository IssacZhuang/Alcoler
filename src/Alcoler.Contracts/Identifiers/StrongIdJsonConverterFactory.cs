using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alcoler.Contracts.Identifiers;

public sealed class StrongIdJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsValueType
        && typeof(IStrongId).IsAssignableFrom(typeToConvert)
        && GetConstructor(typeToConvert) is not null;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var ctor = GetConstructor(typeToConvert) ?? throw new InvalidOperationException($"No string constructor on {typeToConvert.Name}.");
        return (JsonConverter)Activator.CreateInstance(
            typeof(StrongIdJsonConverter<>).MakeGenericType(typeToConvert),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            args: [ctor],
            culture: null)!;
    }

    private static ConstructorInfo? GetConstructor(Type type) =>
        type.GetConstructor([typeof(string)]);

    private sealed class StrongIdJsonConverter<T>(ConstructorInfo ctor) : JsonConverter<T> where T : struct, IStrongId
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value is null
                ? throw new JsonException($"Expected string for {typeof(T).Name}.")
                : (T)ctor.Invoke([value]);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
