using System.Text.Json;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Browser;

public sealed record BrowserProtocolEvent(string Method, JsonElement Parameters, string? SessionId = null);

public interface IBrowserPage : IDisposable
{
    string Id { get; }

    event EventHandler<BrowserProtocolEvent>? ProtocolEvent;

    Task<JsonElement> SendAsync(
        string method, object? parameters, CancellationToken cancellationToken, string? sessionId = null);
}

public interface IBrowserHost : IDisposable
{
    Task<IBrowserPage> CreatePageAsync(SessionId sessionId, string tabId, CancellationToken cancellationToken);

    Task ClosePageAsync(string tabId, CancellationToken cancellationToken);

    Task ShowPageAsync(string tabId, CancellationToken cancellationToken);

    Task HidePageAsync(string tabId, CancellationToken cancellationToken);
}
