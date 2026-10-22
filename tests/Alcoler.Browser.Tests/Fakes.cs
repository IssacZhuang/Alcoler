using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Workspace;

namespace Alcoler.Browser.Tests;

internal sealed class FakeBrowserController : IBrowserController
{
    public List<(SessionId SessionId, BrowserAction Action)> Executed { get; } = [];

    public List<SessionId> Canceled { get; } = [];

    public List<SessionId> Takeovers { get; } = [];

    public Func<BrowserAction, BrowserActionResult>? ResultFactory { get; set; }

    public BrowserSessionStatus? Status { get; set; }

    public bool Disposed { get; private set; }

    public event EventHandler<BrowserStatusChangedEventArgs>? StatusChanged;

    public void RaiseStatusChanged(SessionId sessionId, BrowserSessionStatus status) =>
        StatusChanged?.Invoke(this, new BrowserStatusChangedEventArgs(sessionId, status));

    public Task<BrowserActionResult> ExecuteAsync(SessionId sessionId, BrowserAction action, CancellationToken cancellationToken)
    {
        Executed.Add((sessionId, action));
        return Task.FromResult(ResultFactory?.Invoke(action) ?? new BrowserActionResult(Ok: true));
    }

    public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        Canceled.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task TakeoverAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        Takeovers.Add(sessionId);
        return Task.CompletedTask;
    }

    public BrowserSessionStatus? GetStatus(SessionId sessionId) => Status;

    public void Dispose() => Disposed = true;
}

internal sealed class FakeToolWorkspace : IToolWorkspace
{
    public string Root => @"C:\fake-workspace";

    public List<(SessionId SessionId, string FileName, string MimeType, byte[] Data)> SavedArtifacts { get; } = [];

    public CancellationToken LastSaveCancellationToken { get; private set; }

    public string ResolvePath(string path) => Path.Combine(Root, path);

    public ValueTask<ArtifactReference> SaveArtifactAsync(
        SessionId sessionId, string fileName, string mimeType,
        ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastSaveCancellationToken = cancellationToken;
        SavedArtifacts.Add((sessionId, fileName, mimeType, data.ToArray()));
        return ValueTask.FromResult(new ArtifactReference(Path.Combine(Root, fileName), mimeType, fileName));
    }
}
