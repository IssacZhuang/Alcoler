using System.Collections.Concurrent;
using System.Text.Json;
using Alcoler.Adapters.Mcp;

namespace Alcoler.Kernel.Tests.Mcp;

internal static class McpJson
{
    public static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

internal static class Definitions
{
    public const string ObjectSchema = """{"type":"object"}""";

    public static McpToolDefinition Tool(
        string? name,
        string? description = null,
        string? schemaJson = ObjectSchema,
        string? annotationsJson = null)
    {
        var annotations = annotationsJson is null
            ? null
            : JsonSerializer.Deserialize(annotationsJson, McpJsonContext.Default.McpToolAnnotations);
        return new McpToolDefinition(name, description, schemaJson is null ? null : McpJson.Element(schemaJson), annotations);
    }
}

internal sealed record McpToolPage(IReadOnlyList<McpToolDefinition> Tools, string? NextCursor = null);

internal sealed record ScriptedCallResponse(string? ResultJson = null, int? ErrorCode = null, string? ErrorMessage = null)
{
    public static ScriptedCallResponse TextParts(bool isError, params string[] texts)
    {
        var content = string.Join(",", texts.Select(text => $"{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(text)}}}"));
        return new ScriptedCallResponse($"{{\"content\":[{content}],\"isError\":{(isError ? "true" : "false")}}}");
    }
}

internal sealed class ScriptedMcpServer : IAsyncDisposable
{
    private readonly IMcpTransport _transport;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _listIndex;
    private bool _junkSent;

    public ScriptedMcpServer(IMcpTransport transport)
    {
        _transport = transport;
        _loop = Task.Run(RunAsync);
    }

    public string ProtocolVersion { get; set; } = "2025-06-18";
    public bool TerminateResponsesWithCarriageReturn { get; set; }
    public IReadOnlyList<string> JunkLinesBeforeFirstResponse { get; set; } = [];
    public List<McpToolPage> Pages { get; } = [];
    public HashSet<string> IgnoreMethods { get; } = new(StringComparer.Ordinal);
    public Func<JsonElement?, ScriptedCallResponse>? CallResponder { get; set; }
    public ConcurrentQueue<JsonElement> ReceivedMessages { get; } = [];
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SendRawAsync(string message) => _transport.SendAsync(message, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            string? line;
            try
            {
                line = await _transport.ReceiveAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (line is null)
                break;
            using var document = JsonDocument.Parse(line.TrimEnd('\r'));
            var root = document.RootElement.Clone();
            ReceivedMessages.Enqueue(root);
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method)
                continue;
            if (IgnoreMethods.Contains(method))
                continue;
            try
            {
                await HandleAsync(method, root).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                break;
            }
        }
        Closed.TrySetResult();
    }

    private async Task HandleAsync(string method, JsonElement root)
    {
        switch (method)
        {
            case "initialize":
                await SendAsync(
                    $"{{\"jsonrpc\":\"2.0\",\"id\":{IdOf(root)},\"result\":{{\"protocolVersion\":\"{ProtocolVersion}\"," +
                    $"\"capabilities\":{{\"tools\":{{\"listChanged\":false}}}},\"serverInfo\":{{\"name\":\"scripted\",\"version\":\"1.0.0\"}}}}}}").ConfigureAwait(false);
                return;
            case "tools/list":
                var page = _listIndex < Pages.Count ? Pages[_listIndex] : new McpToolPage([]);
                _listIndex++;
                var tools = string.Join(",", page.Tools.Select(ToolJson));
                var nextCursor = page.NextCursor is null ? "" : $",\"nextCursor\":{JsonSerializer.Serialize(page.NextCursor)}";
                await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{IdOf(root)},\"result\":{{\"tools\":[{tools}]{nextCursor}}}}}").ConfigureAwait(false);
                return;
            case "tools/call":
                JsonElement? callParams = null;
                if (root.TryGetProperty("params", out var parameters) && parameters.ValueKind is JsonValueKind.Object)
                    callParams = parameters.Clone();
                var response = CallResponder?.Invoke(callParams) ?? ScriptedCallResponse.TextParts(false, "ok");
                if (response.ErrorCode is { } code)
                {
                    await SendAsync(
                        $"{{\"jsonrpc\":\"2.0\",\"id\":{IdOf(root)},\"error\":{{\"code\":{code}," +
                        $"\"message\":{JsonSerializer.Serialize(response.ErrorMessage ?? "scripted error")}}}}}").ConfigureAwait(false);
                }
                else
                {
                    await SendAsync(
                        $"{{\"jsonrpc\":\"2.0\",\"id\":{IdOf(root)},\"result\":{response.ResultJson ?? "{}"}}}").ConfigureAwait(false);
                }
                return;
            default:
                if (root.TryGetProperty("id", out _))
                {
                    await SendAsync(
                        $"{{\"jsonrpc\":\"2.0\",\"id\":{IdOf(root)},\"error\":{{\"code\":-32601,\"message\":\"method not found\"}}}}").ConfigureAwait(false);
                }
                return;
        }
    }

    private static string ToolJson(McpToolDefinition tool)
    {
        var members = new List<string> { $"\"name\":{JsonSerializer.Serialize(tool.Name)}" };
        if (tool.Description is not null)
            members.Add($"\"description\":{JsonSerializer.Serialize(tool.Description)}");
        if (tool.InputSchema is { } schema)
            members.Add($"\"inputSchema\":{schema.GetRawText()}");
        if (tool.Annotations is not null)
            members.Add($"\"annotations\":{JsonSerializer.Serialize(tool.Annotations, McpJsonContext.Default.McpToolAnnotations)}");
        return "{" + string.Join(",", members) + "}";
    }

    private static string IdOf(JsonElement request) =>
        request.TryGetProperty("id", out var id) ? id.GetRawText() : "null";

    private async Task SendAsync(string message)
    {
        if (!_junkSent)
        {
            _junkSent = true;
            foreach (var junk in JunkLinesBeforeFirstResponse)
                await _transport.SendAsync(junk, CancellationToken.None).ConfigureAwait(false);
        }
        await _transport.SendAsync(
            TerminateResponsesWithCarriageReturn ? message + "\r" : message,
            CancellationToken.None).ConfigureAwait(false);
    }
}

internal sealed class FailingTransport : IMcpTransport
{
    public Task SendAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> ReceiveAsync(CancellationToken cancellationToken) =>
        throw new IOException("pipe broken");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class McpTestWait
{
    public static async Task<IReadOnlyList<JsonElement>> MessagesAsync(
        ScriptedMcpServer server,
        string method,
        int count,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var matches = server.ReceivedMessages
                .Where(message => message.TryGetProperty("method", out var name) && name.GetString() == method)
                .ToArray();
            if (matches.Length >= count)
                return matches;
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new TimeoutException($"The scripted MCP server did not receive {count} '{method}' message(s).");
    }
}

internal static class McpTestHarness
{
    public static (McpServerConnection Connection, ScriptedMcpServer Server) Connect(Action<ScriptedMcpServer>? configure = null)
    {
        var (client, serverTransport) = MemoryMcpTransport.CreatePair();
        var server = new ScriptedMcpServer(serverTransport);
        configure?.Invoke(server);
        return (new McpServerConnection("test", client), server);
    }
}
