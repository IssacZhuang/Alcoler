using System.Text.Json;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Browser;

public sealed record BrowserProtocolEvent(string Method, JsonElement Parameters, string? SessionId = null);

public sealed record BrowserNewTabRequestedEventArgs(SessionId SessionId, string OpenerTabId, string Url, bool IsUserInitiated = false, string? TargetId = null);

public interface IBrowserPage : IDisposable
{
    string Id { get; }

    event EventHandler<BrowserProtocolEvent>? ProtocolEvent;

    Task<JsonElement> SendAsync(
        string method, object? parameters, CancellationToken cancellationToken, string? sessionId = null);
}

public interface IBrowserHost : IDisposable
{
    event EventHandler<BrowserNewTabRequestedEventArgs>? NewTabRequested;

    Task<IBrowserPage> CreatePageAsync(SessionId sessionId, string tabId, CancellationToken cancellationToken, string? adoptTargetId = null);

    Task ClosePageAsync(string tabId, CancellationToken cancellationToken);

    Task ShowPageAsync(string tabId, CancellationToken cancellationToken);

    Task HidePageAsync(string tabId, CancellationToken cancellationToken);
}
