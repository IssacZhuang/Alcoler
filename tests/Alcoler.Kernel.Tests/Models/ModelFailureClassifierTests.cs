using Alcoler.Adapters.Models;
using Alcoler.Contracts.Models;
using Xunit;

using Alcoler.Kernel.Models;

namespace Alcoler.Kernel.Tests.Models;

public class ModelFailureClassifierTests
{
    [Fact]
    public void Unauthorized_MapsToAuthenticationFailed()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(
            401, null, """{"error":{"type":"authentication_error","message":"invalid x-api-key"}}""");

        Assert.Equal(ModelFailureKind.AuthenticationFailed, failure.Kind);
        Assert.Equal("authentication_error", failure.ProviderCode);
        Assert.Equal("invalid x-api-key", failure.Message);
    }

    [Fact]
    public void Forbidden_MapsToAuthenticationFailed()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(403, null, "forbidden");
        Assert.Equal(ModelFailureKind.AuthenticationFailed, failure.Kind);
    }

    [Fact]
    public void RateLimited_ParsesRetryAfterSeconds()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(
            429, "23", """{"error":{"message":"rate limited"}}""");

        Assert.Equal(ModelFailureKind.RateLimited, failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(23), failure.RetryAfter);
        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public void RateLimited_ParsesRetryAfterHttpDate()
    {
        var date = DateTimeOffset.UtcNow.AddSeconds(40).ToString("R");
        var failure = ModelFailureClassifier.FromHttpResponse(429, date, "slow down");

        Assert.Equal(ModelFailureKind.RateLimited, failure.Kind);
        Assert.NotNull(failure.RetryAfter);
        Assert.InRange(failure.RetryAfter!.Value.TotalSeconds, 30, 50);
    }

    [Fact]
    public void RateLimited_WithoutRetryAfter_LeavesItNull()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(429, "not-a-date-or-number", "rate limited");
        Assert.Null(failure.RetryAfter);
    }

    [Fact]
    public void PromptTooLong_AnthropicStyle_ParsesTokenGap()
    {
        var body = """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 210325 tokens > 200000 maximum"}}""";

        var failure = ModelFailureClassifier.FromHttpResponse(400, null, body);

        Assert.Equal(ModelFailureKind.ContextOverflow, failure.Kind);
        Assert.Equal(10325, failure.DeclaredTokenGap);
        Assert.False(failure.IsRetryable);
    }

    [Fact]
    public void ContextLengthExceeded_OpenAiStyle_ParsesTokenGap()
    {
        var body = """{"error":{"message":"This model's maximum context length is 8192 tokens. However, you requested 10456 tokens (1016 in the messages, 9440 in the completion)","type":"invalid_request_error","code":null}}""";

        var failure = ModelFailureClassifier.FromHttpResponse(400, null, body);

        Assert.Equal(ModelFailureKind.ContextOverflow, failure.Kind);
        Assert.Equal(2264, failure.DeclaredTokenGap);
    }

    [Fact]
    public void GreaterThanMaximumVariant_ParsesTokenGap()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(
            400, null, "prompt is too long: 900 > 800 maximum allowed");

        Assert.Equal(ModelFailureKind.ContextOverflow, failure.Kind);
        Assert.Equal(100, failure.DeclaredTokenGap);
    }

    [Fact]
    public void ContextOverflow_WithoutNumbers_LeavesGapNull()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(
            400, null, "maximum context exceeded for this conversation");

        Assert.Equal(ModelFailureKind.ContextOverflow, failure.Kind);
        Assert.Null(failure.DeclaredTokenGap);
    }

    [Fact]
    public void PlainBadRequest_MapsToInvalidRequest()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(
            400, null, """{"error":{"message":"bad","type":"invalid_request_error","code":"missing_field"}}""");

        Assert.Equal(ModelFailureKind.InvalidRequest, failure.Kind);
        Assert.Equal("missing_field", failure.ProviderCode);
    }

    [Fact]
    public void NotFound_MapsToInvalidRequest()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(404, null, "model not found");
        Assert.Equal(ModelFailureKind.InvalidRequest, failure.Kind);
    }

    [Fact]
    public void ServerError_MapsToProviderErrorAndIsRetryable()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(
            503, null, """{"error":{"type":"overloaded_error","message":"overloaded"}}""");

        Assert.Equal(ModelFailureKind.ProviderError, failure.Kind);
        Assert.Equal("overloaded_error", failure.ProviderCode);
        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public void Overloaded_529_MapsToRateLimitedAndIsRetryable()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(
            529, "12", """{"error":{"type":"overloaded_error","message":"overloaded"}}""");

        Assert.Equal(ModelFailureKind.RateLimited, failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), failure.RetryAfter);
        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public void UnknownClientError_MapsToInvalidRequestAndIsNotRetryable()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(418, null, "teapot");

        Assert.Equal(ModelFailureKind.InvalidRequest, failure.Kind);
        Assert.False(failure.IsRetryable);
    }

    [Fact]
    public void RetryAfterMsHeader_WinsOverRetryAfterSeconds()
    {
        var failure = ModelFailureClassifier.FromHttpResponse(
            429, "30", "slow down", retryAfterMsHeader: "1500");

        Assert.Equal(TimeSpan.FromMilliseconds(1500), failure.RetryAfter);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    public void ShouldRetryHeaderFalse_DemotesRetryAfterToNull(string shouldRetry)
    {
        var failure = ModelFailureClassifier.FromHttpResponse(
            429, "30", "slow down", retryAfterMsHeader: "1500", shouldRetryHeader: shouldRetry);

        Assert.Null(failure.RetryAfter);
    }

    [Fact]
    public void ContextOverflow_IsDetectedFromServerErrorBody()
    {
        var body = """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 210325 tokens > 200000 maximum"}}""";

        var failure = ModelFailureClassifier.FromHttpResponse(500, null, body);

        Assert.Equal(ModelFailureKind.ContextOverflow, failure.Kind);
        Assert.Equal(10325, failure.DeclaredTokenGap);
        Assert.False(failure.IsRetryable);
    }

    [Fact]
    public void HttpRequestException_MapsToNetworkError()
    {
        var failure = ModelFailureClassifier.FromException(new HttpRequestException("connection refused"));

        Assert.Equal(ModelFailureKind.NetworkError, failure.Kind);
        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public void TaskCanceledWithoutToken_MapsToTimeout()
    {
        using var unFiredCts = new CancellationTokenSource();
        var failure = ModelFailureClassifier.FromException(
            new TaskCanceledException("timed out"), unFiredCts.Token);

        Assert.Equal(ModelFailureKind.Timeout, failure.Kind);
    }

    [Fact]
    public void CancellationWithFiredToken_MapsToCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var failure = ModelFailureClassifier.FromException(
            new OperationCanceledException(cts.Token), cts.Token);

        Assert.Equal(ModelFailureKind.Cancelled, failure.Kind);
    }

    [Fact]
    public void JsonException_MapsToStreamProtocolError()
    {
        var failure = ModelFailureClassifier.FromException(
            new System.Text.Json.JsonException("bad payload"));

        Assert.Equal(ModelFailureKind.StreamProtocolError, failure.Kind);
        Assert.False(failure.IsRetryable);
    }
}
