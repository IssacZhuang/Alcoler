using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Stores;
using Alcoler.Ui;

namespace Alcoler.ViewModel;

public sealed class SubagentDocumentViewModel : IDisposable
{
    private readonly ProjectionStore _store;
    private IConversationProjection? _initialProjection;

    public SubagentDocumentViewModel(
        IAgentRuntime runtime,
        SessionId sessionId,
        string workspaceRoot,
        IConversationProjection projection,
        Func<IConversationProjection> subscribe,
        IUiScheduler scheduler)
    {
        _initialProjection = projection;
        ProjectionStore? store = null;
        ConversationViewModel? conversation = null;
        try
        {
            var snapshot = projection.Snapshot();
            _store = store = new ProjectionStore(
                () => Interlocked.Exchange(ref _initialProjection, null) ?? subscribe(),
                sessionId, scheduler, snapshot.State, TimeSpan.FromMilliseconds(30));
            _store.SetRevision(snapshot.Revision);
            Conversation = conversation = new ConversationViewModel(runtime, _store) { WorkspaceRoot = workspaceRoot };
            Conversation.ApplyUpdate(new ProjectionUpdate(
                snapshot.Sequence, snapshot.State, [], Reset: true, ResetRows: snapshot.Rows));
            _store.Start();
        }
        catch
        {
            conversation?.Dispose();
            store?.Dispose();
            Interlocked.Exchange(ref _initialProjection, null)?.Dispose();
            throw;
        }
    }

    public ConversationViewModel Conversation { get; }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        Conversation.Dispose();
        _store.Dispose();
        Interlocked.Exchange(ref _initialProjection, null)?.Dispose();
    }
}
