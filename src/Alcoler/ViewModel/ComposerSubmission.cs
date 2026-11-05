using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Models;

namespace Alcoler.ViewModel;

internal static class ComposerSubmission
{
    internal sealed record Request<TState>
    {
        public required ComposerViewModel Composer { get; init; }

        // Returning true consumes the parsed command; no send follows.
        public Func<ComposerCommand, Task<bool>>? ApplyCommandAsync { get; init; }

        // Captures submit inputs after command application and before sending starts.
        public required Func<TState> CaptureState { get; init; }

        // Runs after IsSending turns on and before the submit window opens.
        public Action? BeginSending { get; init; }

        public required Func<TState, string, IReadOnlyList<InputImageBlock>, Task<SendTextCommand>> BuildCommandAsync { get; init; }

        public required Func<SendTextCommand, Task<CommandAck>> SubmitCommandAsync { get; init; }

        // Ack side effects preceding the shared sent-history and composer clearing.
        public Action<TState>? OnAccepted { get; init; }

        public required Action<string, CommandAck> OnRejected { get; init; }

        public required Func<Exception, string> DescribeFailure { get; init; }

        public required Action<string, string> ReportFailure { get; init; }

        // Runs inside the submit window after the accepted or rejected outcome.
        public Func<Task>? AfterOutcomeAsync { get; init; }

        // Runs in finally before IsSending is cleared.
        public Action? Release { get; init; }
    }

    internal static async Task SubmitAsync<TState>(Request<TState> request)
    {
        var composer = request.Composer;
        if (!composer.CanSend)
        {
            return;
        }
        if (ComposerCommands.TryParse(composer.Draft, out var command))
        {
            if (request.ApplyCommandAsync is { } applyCommand && await applyCommand(command))
            {
                return;
            }
            if (command.Kind == ComposerCommandKind.Plan)
            {
                composer.PlanEnabled = true;
            }
            if (command.Prompt.Length == 0)
            {
                composer.InputError = null;
                composer.Draft = string.Empty;
                return;
            }
            composer.InputError = null;
            composer.Draft = command.Prompt;
        }
        var text = composer.Draft;
        var state = request.CaptureState();
        composer.IsSending = true;
        request.BeginSending?.Invoke();
        try
        {
            var images = composer.SnapshotImages();
            var send = await request.BuildCommandAsync(state, text, images);
            var ack = await request.SubmitCommandAsync(send);
            if (ack.Accepted)
            {
                request.OnAccepted?.Invoke(state);
                composer.RecordSent(text);
                composer.ClearIfSendable(text, images);
            }
            else
            {
                request.OnRejected(text, ack);
            }
            if (request.AfterOutcomeAsync is { } afterOutcome)
            {
                await afterOutcome();
            }
        }
        catch (Exception exception)
        {
            request.ReportFailure(text, request.DescribeFailure(exception));
        }
        finally
        {
            request.Release?.Invoke();
            composer.IsSending = false;
        }
    }
}
