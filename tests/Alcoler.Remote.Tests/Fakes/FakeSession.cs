using System.Threading.Channels;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;

namespace Alcoler.Remote.Tests.Fakes;

// Shared mutable state for one fake session. Every mutation happens under Gate so
// snapshot construction, command application, and delta fan-out are serialized.
internal sealed class FakeSession
{
    public required object Gate { get; init; }
    public required SessionId Id { get; init; }
    public required string WorkspaceRoot { get; init; }
    public required string Title { get; init; }
    public required PermissionMode Mode { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ConversationState State { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Archived { get; set; }
    public long Revision { get; set; }
    public long Sequence { get; set; }
    public List<ConversationRow> Rows { get; } = [];
    public List<Channel<ConversationDelta>> Views { get; } = [];
    public Func<IConversationProjection>? ProjectionFactory { get; set; }

    public ConversationDelta Publish(IReadOnlyList<RowOp> ops)
    {
        // Caller holds Gate.
        Sequence += 1;
        var delta = new ConversationDelta(Sequence, ops);
        foreach (var view in Views)
            view.Writer.TryWrite(delta);
        return delta;
    }
}
