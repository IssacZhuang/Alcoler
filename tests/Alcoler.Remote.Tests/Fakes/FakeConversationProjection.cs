using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Alcoler.Contracts.Projections;

namespace Alcoler.Remote.Tests.Fakes;

// One view per observer: the gateway disposes the projection when a stream
// connection ends, so views must not share channel state. The channel is attached
// at Snapshot time so a delta published between Snapshot and Deltas cannot be lost.
public sealed class FakeConversationProjection : IConversationProjection
{
    private readonly FakeSession _session;
    private Channel<ConversationDelta>? _channel;

    internal FakeConversationProjection(FakeSession session) => _session = session;

    public ConversationSnapshot Snapshot()
    {
        lock (_session.Gate)
        {
            _channel ??= AttachView();
            return new ConversationSnapshot(
                _session.Revision,
                _session.Sequence,
                [.. _session.Rows],
                _session.State);
        }
    }

    public async IAsyncEnumerable<ConversationDelta> Deltas(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<ConversationDelta> channel;
        lock (_session.Gate)
            channel = _channel ??= AttachView();
        await foreach (var delta in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return delta;
    }

    public async Task WaitUntilSequenceAsync(long sequence, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_session.Gate)
            {
                if (_session.Sequence >= sequence)
                    return;
            }
            await Task.Delay(10, cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_session.Gate)
        {
            if (_channel is null)
                return;
            _session.Views.Remove(_channel);
            _channel.Writer.TryComplete();
            _channel = null;
        }
    }

    private Channel<ConversationDelta> AttachView()
    {
        // Caller holds Gate.
        var channel = Channel.CreateUnbounded<ConversationDelta>(
            new UnboundedChannelOptions { SingleReader = true });
        _session.Views.Add(channel);
        return channel;
    }
}
