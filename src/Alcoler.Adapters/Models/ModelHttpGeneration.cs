using System.Text.Json;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Models;

namespace Alcoler.Adapters.Models;

internal static class ModelHttpGeneration
{
    public static async ValueTask<IReadOnlyList<ModelStreamEvent>> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> buildRequest,
        Func<JsonElement, IReadOnlyList<ModelStreamEvent>> decode,
        CancellationToken cancellationToken)
    {
        var phase = ModelFailurePhase.RequestSetup;
        int? statusCode = null;
        try
        {
            using var request = buildRequest();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return [new ModelStreamFailed(ModelFailureClassifier.FromHttpResponse(statusCode.Value,
                    SseHttp.Header(response, "retry-after"), error, SseHttp.Header(response, "retry-after-ms"), SseHttp.Header(response, "x-should-retry")) with
                {
                    Phase = phase,
                    HttpStatusCode = statusCode,
                })];
            }
            phase = ModelFailurePhase.ResponseBody;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return decode(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = ex is ArgumentException && phase == ModelFailurePhase.RequestSetup
                ? new ModelFailure(ModelFailureKind.InvalidRequest, ex.Message)
                : ex is InvalidOperationException or KeyNotFoundException or FormatException
                    ? new ModelFailure(ModelFailureKind.StreamProtocolError, "Invalid model response payload.")
                    : ModelFailureClassifier.FromException(ex, cancellationToken);
            return [new ModelStreamFailed(failure with { Phase = phase, HttpStatusCode = statusCode })];
        }
    }
}
