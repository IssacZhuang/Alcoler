using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alcoler.Adapters.Mcp;

internal static class McpProtocol
{
    public const string JsonRpcVersion = "2.0";
    public const string LatestVersion = "2025-06-18";
    public const string FallbackVersion = "2024-11-05";
    public const string ClientName = "Alcoler";
    public const string ClientVersion = "0.1.0";

    public const string InitializeMethod = "initialize";
    public const string InitializedNotification = "notifications/initialized";
    public const string ListToolsMethod = "tools/list";
    public const string CallToolMethod = "tools/call";
}

internal sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    long Id,
    string Method,
    JsonElement Params);

internal sealed record JsonRpcNotification(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    string Method);

internal sealed record McpInitializeParams(
    string ProtocolVersion,
    McpClientCapabilities Capabilities,
    McpImplementation ClientInfo);

internal sealed record McpClientCapabilities;

internal sealed record McpToolListParams(string? Cursor);

internal sealed record McpToolCallParams(string Name, JsonElement? Arguments);

internal sealed record McpInitializeResult(
    string? ProtocolVersion,
    McpImplementation? ServerInfo,
    JsonElement? Capabilities);

internal sealed record McpImplementation(string? Name, string? Version);

internal sealed record McpToolListResult(IReadOnlyList<McpToolDefinition>? Tools, string? NextCursor);

internal sealed record McpToolDefinition(
    string? Name,
    string? Description,
    JsonElement? InputSchema,
    McpToolAnnotations? Annotations);

internal sealed record McpToolAnnotations(
    bool? ReadOnlyHint,
    bool? DestructiveHint,
    bool? IdempotentHint,
    bool? OpenWorldHint);

internal sealed record McpToolCallResult(IReadOnlyList<McpContentPart>? Content, bool? IsError);

internal sealed record McpContentPart(string? Type, string? Text);

internal readonly record struct McpToolCallOutcome(IReadOnlyList<string> TextParts, bool IsError);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(McpInitializeParams))]
[JsonSerializable(typeof(McpClientCapabilities))]
[JsonSerializable(typeof(McpToolListParams))]
[JsonSerializable(typeof(McpToolCallParams))]
[JsonSerializable(typeof(McpImplementation))]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpToolListResult))]
[JsonSerializable(typeof(McpToolDefinition))]
[JsonSerializable(typeof(McpToolAnnotations))]
[JsonSerializable(typeof(McpToolCallResult))]
[JsonSerializable(typeof(McpContentPart))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
