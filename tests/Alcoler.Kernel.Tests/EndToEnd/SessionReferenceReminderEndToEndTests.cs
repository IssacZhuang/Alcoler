using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class SessionReferenceReminderEndToEndTests
{
    [Fact]
    public async Task UserInputWithSessionReference_InjectsReminderIntoRequestButNotStore()
    {
        await using var harness = ScriptedCallHarness.Create(
            [new ModelTextDelta("done"), new ModelStreamFinished(ModelFinishReason.Stop)]);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "continue from #sess_abc123 please"),
            CancellationToken.None);

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle);

        var request = Assert.Single(harness.Model.Requests);
        var reminders = RequestTexts(request)
            .Split('\n')
            .Count(line => line.Contains("The user referenced prior Alcoler session(s)", StringComparison.Ordinal));
        Assert.Equal(1, reminders);
        Assert.Contains("- sess_abc123", RequestTexts(request), StringComparison.Ordinal);
        await using var store = harness.OpenStore();
        var stored = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(stored, message => message.Message.Parts.OfType<Contracts.Conversation.AttachmentPart>()
            .Any(part => part.Content.Contains("referenced prior Alcoler session", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UserInputWithoutSessionReference_InjectsNoReminder()
    {
        await using var harness = ScriptedCallHarness.Create(
            [new ModelTextDelta("done"), new ModelStreamFinished(ModelFinishReason.Stop)]);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "just keep working"),
            CancellationToken.None);

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle);

        var request = Assert.Single(harness.Model.Requests);
        Assert.DoesNotContain("The user referenced prior Alcoler session(s)", RequestTexts(request), StringComparison.Ordinal);
    }

    private static string RequestTexts(ModelRequest request) =>
        string.Join("\n", request.Messages.SelectMany(message => message.Content.OfType<InputTextBlock>()).Select(block => block.Text));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("condition was not met within 30 seconds");
            await Task.Delay(20);
        }
    }
}
