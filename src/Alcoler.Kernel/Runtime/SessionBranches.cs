using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Storage;

namespace Alcoler.Kernel.Runtime;

internal static class SessionBranches
{
    // A branch is the kept prefix plus every message appended after its cut; messages
    // between that prefix and the cut belong to a discarded branch.
    public static IReadOnlyList<StoredMessage> SelectActive(
        IReadOnlyList<StoredMessage> messages,
        SessionBranch? branch)
    {
        if (branch is null)
        {
            return messages;
        }

        var byId = messages.ToDictionary(stored => stored.Message.Id);
        var kept = new List<StoredMessage>(branch.KeptMessageIds.Count);
        foreach (var messageId in branch.KeptMessageIds)
        {
            if (byId.TryGetValue(messageId, out var stored))
            {
                kept.Add(stored);
            }
        }

        var cutIndex = -1;
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].Message.Id == branch.CutAfterMessageId)
            {
                cutIndex = index;
                break;
            }
        }
        if (cutIndex < 0)
        {
            return messages;
        }

        var seen = kept.Select(stored => stored.Message.Id).ToHashSet();
        foreach (var stored in messages.Skip(cutIndex + 1))
        {
            if (seen.Add(stored.Message.Id))
            {
                kept.Add(stored);
            }
        }
        return kept;
    }

    // Completed turn tails are recovered from the unfiltered log so rewind cuts that
    // drop their terminal events still leave the surviving prefix forkable.
    public static IReadOnlyDictionary<MessageId, TurnId> FindCompletedTurnTails(
        IReadOnlyList<SequencedEvent> durable,
        IReadOnlySet<MessageId> activeMessageIds)
    {
        var tails = new Dictionary<MessageId, TurnId>();
        MessageId? pendingAssistant = null;
        foreach (var entry in durable.OrderBy(entry => entry.Sequence))
        {
            switch (entry.Event)
            {
                case AssistantMessageCommittedEvent assistant:
                    pendingAssistant = activeMessageIds.Contains(assistant.Message.Id)
                        ? assistant.Message.Id
                        : null;
                    break;

                case UserMessageCommittedEvent user when SessionRuntime.IsRealUserTurn(user.Message):
                    pendingAssistant = null;
                    break;

                case TurnCompletedEvent completed:
                    if (completed.Outcome == TurnOutcome.Completed && pendingAssistant is { } tail)
                    {
                        tails[tail] = completed.TurnId;
                    }
                    pendingAssistant = null;
                    break;

                case TurnFailedEvent:
                    pendingAssistant = null;
                    break;
            }
        }
        return tails;
    }
}
