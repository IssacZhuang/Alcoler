using System.Text;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Models;

namespace Alcoler.Kernel.Context;

public sealed record CompactSummaryModelResult(string Text, ModelFailure? Failure);

public static class CompactSummaryModelRunner
{
    public static async Task<CompactSummaryModelResult> RunAsync(
        IChatModel model, ModelRequest request, CancellationToken cancellationToken,
        ModelRunnerOptions? runnerOptions = null)
    {
        var state = new StreamState();
        var options = (runnerOptions ?? new ModelRunnerOptions()) with { CommitOnFirstEvent = true };
        try
        {
            await foreach (var streamEvent in ModelStreamRunner.RunAsync(model, request, options, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
                state.Apply(streamEvent);
        }
        catch (Exception exception)
        {
            state.Failure = ModelFailureClassifier.FromException(exception, cancellationToken);
        }

        var failure = state.Result().Failure;
        if (failure is not null && !cancellationToken.IsCancellationRequested
            && model.SupportsNonStreamingGeneration && !state.CommittedBlock && CanFallback(failure))
        {
            var generated = new StreamState();
            try
            {
                var events = await model.CreateRequestScope().GenerateAsync(request, cancellationToken).ConfigureAwait(false);
                foreach (var streamEvent in events)
                    generated.Apply(streamEvent);
            }
            catch (Exception exception)
            {
                generated.Failure = ModelFailureClassifier.FromException(exception, cancellationToken);
            }
            return generated.Result();
        }
        return state.Result();
    }

    private static bool CanFallback(ModelFailure failure) =>
        failure.Kind is not (ModelFailureKind.Cancelled or ModelFailureKind.ContextOverflow)
        && !IsMediaSizeFailure(failure)
        && (failure.Phase != ModelFailurePhase.RequestSetup || failure.HttpStatusCode == 404)
        && (failure.Phase == ModelFailurePhase.ResponseBody || failure.HttpStatusCode == 404
            || failure.Kind is not (ModelFailureKind.InvalidRequest or ModelFailureKind.AuthenticationFailed));

    public static ModelRequest ProjectMediaForRetry(ModelRequest request)
    {
        var changed = false;
        var messages = request.Messages.Select(message =>
        {
            var changedMessage = false;
            var content = message.Content.Select(block =>
            {
                switch (block)
                {
                    case InputImageBlock:
                        changedMessage = true;
                        return (ModelContentBlock)new InputTextBlock("[image]");
                    case InputToolResultBlock { Images.Count: > 0 } result:
                        changedMessage = true;
                        var placeholders = string.Join("\n\n", result.Images.Select(_ => "[image]"));
                        return result with
                        {
                            Content = result.Content.Length > 0 ? result.Content + "\n\n" + placeholders : placeholders,
                            Images = null,
                        };
                    default:
                        return block;
                }
            }).ToList();
            if (!changedMessage)
                return message;
            changed = true;
            return message with { Content = content };
        }).ToList();
        return changed ? request with { Messages = messages } : request;
    }

    public static bool IsMediaSizeFailure(ModelFailure failure)
    {
        var code = failure.ProviderCode?.Trim().ToLowerInvariant();
        if (code is "media_too_large" or "media_payload_too_large" or "image_too_large" or "document_too_large")
            return true;
        var message = failure.Message.ToLowerInvariant();
        return (message.Contains("media") || message.Contains("image") || message.Contains("document"))
            && (message.Contains("too large") || message.Contains("exceed"));
    }

    private sealed class StreamState
    {
        private readonly StringBuilder _pending = new();
        private readonly StringBuilder _committed = new();
        private ModelStreamFinished? _finish;
        private bool _usedTools;

        public bool CommittedBlock { get; private set; }
        public ModelFailure? Failure { get; set; }

        public void Apply(ModelStreamEvent streamEvent)
        {
            switch (streamEvent)
            {
                case ModelTextStarted:
                    _pending.Clear();
                    break;
                case ModelTextDelta text:
                    _pending.Append(text.Delta);
                    break;
                case ModelTextEnded:
                    _committed.Append(_pending);
                    _pending.Clear();
                    CommittedBlock = true;
                    break;
                case ModelReasoningEnded or ModelToolInputEnded:
                    CommittedBlock = true;
                    break;
                case ModelToolCallReady:
                    CommittedBlock = true;
                    _usedTools = true;
                    break;
                case ModelStreamFinished finish:
                    _finish = finish;
                    break;
                case ModelStreamFailed failed:
                    Failure = failed.Failure;
                    break;
            }
        }

        public ModelFailure? CompletionFailure()
        {
            if (Failure is not null)
                return Failure;
            if (_finish is null)
                return new ModelFailure(ModelFailureKind.StreamProtocolError, "Compact summary stream ended before finish");
            if (_finish.ContextWindowExceeded
                || _finish.FinishReason == ModelFinishReason.Length && string.IsNullOrWhiteSpace(_committed.ToString()))
                return new ModelFailure(ModelFailureKind.ContextOverflow, "Model context window exceeded during compaction");
            if (!CommittedBlock && _finish.FinishReason == ModelFinishReason.Other && string.IsNullOrWhiteSpace(_finish.RawFinishReason))
                return new ModelFailure(ModelFailureKind.StreamProtocolError, "Compact summary stream ended without a complete provider response");
            return null;
        }

        public CompactSummaryModelResult Result()
        {
            var failure = CompletionFailure();
            if (failure is null && _usedTools)
                failure = new ModelFailure(ModelFailureKind.InvalidRequest, "Tool use is not allowed during compaction");
            return new CompactSummaryModelResult(_committed.ToString(), failure);
        }
    }
}
