using System.Text.Json;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Contracts.Browser;

public enum BrowserSessionState
{
    Idle,
    Launched,
    Busy,
    Failed,
}

public abstract record BrowserAction
{
    public abstract string Verb { get; }

    public string? TabId { get; init; }

    public bool UserInitiated { get; init; }
}

public sealed record BrowserCommandAction(string Command, JsonElement Input) : BrowserAction
{
    public override string Verb => Command;
}

public sealed record BrowserNavigateAction(string Url) : BrowserAction
{
    public override string Verb => "navigate";
}

public sealed record BrowserClickAction(string Selector) : BrowserAction
{
    public override string Verb => "click";
}

public sealed record BrowserTypeAction(string Selector, string Text, bool Submit = false) : BrowserAction
{
    public override string Verb => "type";
}

public sealed record BrowserScreenshotAction() : BrowserAction
{
    public override string Verb => "screenshot";
}

public sealed record BrowserPressAction(string Key) : BrowserAction
{
    public override string Verb => "press";
}

public sealed record BrowserScrollAction(int DeltaX, int DeltaY) : BrowserAction
{
    public override string Verb => "scroll";
}

public sealed record BrowserWaitAction(int Milliseconds) : BrowserAction
{
    public override string Verb => "wait";
}

public sealed record BrowserReadTextAction() : BrowserAction
{
    public override string Verb => "read_text";
}

public sealed record BrowserActionResult(
    bool Ok,
    string? Url = null,
    string? Title = null,
    string? Text = null,
    byte[]? ScreenshotPng = null,
    string? Error = null,
    string? TabId = null,
    string? StructuredOutput = null,
    bool SideEffectUncertain = false,
    byte[]? Media = null,
    string? MediaType = null,
    string? MediaFileName = null);

public sealed record BrowserSessionStatus(
    BrowserSessionState State,
    string? Url = null,
    string? Title = null,
    string? LatestScreenshotUri = null,
    DateTimeOffset UpdatedAt = default,
    IReadOnlyList<BrowserTabStatus>? Tabs = null,
    string? ActiveTabId = null,
    bool IsUserControlled = false,
    string? Error = null);

public sealed record BrowserTabStatus(
    string Id,
    string Url,
    string Title,
    bool IsActive = false,
    bool IsLoading = false,
    bool IsUserControlled = false);

public sealed record BrowserStatusChangedEventArgs(SessionId SessionId, BrowserSessionStatus Status);

/// <summary>
/// Browser automation surface shared by the tool layer and the workbench pane.
/// Implementations must allow concurrent calls for different sessions; decisions
/// and approvals never live here, only navigation/act results.
/// </summary>
public interface IBrowserController : IDisposable
{
    event EventHandler<BrowserStatusChangedEventArgs>? StatusChanged;

    Task<BrowserActionResult> ExecuteAsync(SessionId sessionId, BrowserAction action, CancellationToken cancellationToken);

    Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken);

    Task TakeoverAsync(SessionId sessionId, CancellationToken cancellationToken);

    BrowserSessionStatus? GetStatus(SessionId sessionId);
}
