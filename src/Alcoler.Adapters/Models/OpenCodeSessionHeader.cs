using System.Net.Http;
using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

internal static class OpenCodeSessionHeader
{
    internal const string Name = "x-opencode-session";

    private static readonly string UserAgent = "Alcoler/" +
        (typeof(OpenCodeSessionHeader).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    // OpenCode Go rejects requests that carry no stable per-conversation session id,
    // and requires a client-specific user agent instead of a generic HTTP library one.
    internal static void Add(HttpRequestMessage message, ModelProperties properties, ModelRequest request)
    {
        if (!properties.RequiresOpenCodeSession)
            return;
        message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (request.CacheAffinity?.SessionId is { Length: > 0 } sessionId)
            message.Headers.TryAddWithoutValidation(Name, sessionId);
    }
}
