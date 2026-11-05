using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Contracts.Models;

using Alcoler.Kernel.Models;

namespace Alcoler.Adapters.Models;

/// <summary>
/// Shared request/send/SSE-pump skeleton for streaming model calls. All per-model
/// behavior lives in the delegates; exception filters and event order are wire contract
/// shared by every model. Exactly one of <paramref name="complete"/> (the state
/// synthesizes its own terminal events) and <paramref name="endWithoutTerminalFailure"/>
/// (terminal is an observed marker) must be provided.
/// </summary>
internal static class SsePump
{
    internal static async IAsyncEnumerable<ModelStreamEvent> RunAsync<TState>(
        HttpClient httpClient,
        Func<HttpRequestMessage> buildRequest,
        TState state,
        Action<TState, JsonElement, SseFrame, List<ModelStreamEvent>> dispatch,
        Func<Exception, int, ModelFailure> dispatchFailure,
        Func<TState, bool>? isTerminal = null,
        Action<TState, List<ModelStreamEvent>>? complete = null,
        Func<int, ModelFailure>? endWithoutTerminalFailure = null,
        Action<HttpResponseMessage>? onResponse = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        HttpRequestMessage? built = null;
        ModelFailure? validationFailure = null;
        try { built = buildRequest(); }
        catch (ArgumentException ex)
        {
            validationFailure = new ModelFailure(ModelFailureKind.InvalidRequest, ex.Message,
                Phase: ModelFailurePhase.RequestSetup);
        }
        if (validationFailure is not null)
        {
            yield return new ModelStreamFailed(validationFailure);
            yield break;
        }
        using var httpRequest = built!;
        HttpResponseMessage? response = null;
        ModelFailure? sendFailure = null;
        try
        {
            response = await httpClient.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sendFailure = ModelFailureClassifier.FromException(ex, cancellationToken) with
                { Phase = ModelFailurePhase.RequestSetup };
        }
        if (sendFailure is not null)
        {
            yield return new ModelStreamFailed(sendFailure);
            yield break;
        }

        using (response!)
        {
            onResponse?.Invoke(response!);
            var events = new List<ModelStreamEvent>();
            await foreach (var frame in SseHttp.ReadFramesAsync(response!, cancellationToken).ConfigureAwait(false))
            {
                if (frame.Event == SseHttp.FailureEventName)
                {
                    yield return new ModelStreamFailed(SseHttp.DeserializeFailure(frame.Data));
                    yield break;
                }
                if (frame.Data.Length == 0)
                    continue;
                if (frame.Data is "[DONE]")
                {
                    if (complete is null)
                        continue;
                    events.Clear();
                    complete(state, events);
                    foreach (var e in events)
                        yield return e;
                    yield break;
                }

                ModelFailure? failure = null;
                try
                {
                    using var document = JsonDocument.Parse(frame.Data);
                    events.Clear();
                    dispatch(state, document.RootElement, frame, events);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    failure = dispatchFailure(ex, (int)response!.StatusCode);
                }
                if (failure is not null)
                {
                    yield return new ModelStreamFailed(failure);
                    yield break;
                }
                foreach (var e in events)
                    yield return e;
                if (isTerminal is not null && isTerminal(state))
                    yield break;
            }
            if (complete is not null)
            {
                events.Clear();
                complete(state, events);
                foreach (var e in events)
                    yield return e;
            }
            else
                yield return new ModelStreamFailed(endWithoutTerminalFailure!((int)response!.StatusCode));
        }
    }
}
