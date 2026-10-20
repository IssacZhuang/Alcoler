using System.Net;
using System.Reflection;
using System.Text;
using Alcoler.Adapters.ProviderUsage;
using Xunit;

namespace Alcoler.Kernel.Tests.ProviderUsage;

public sealed class ProviderUsageServiceTests
{
    private const string TestKey = "test-only-not-an-account-key";

    public static TheoryData<string, ProviderUsageAdapterId, string, string, double, string?> VendorFixtures => new()
    {
        {
            "https://open.bigmodel.cn/api/paas/v4", ProviderUsageAdapterId.Zhipu,
            "https://open.bigmodel.cn/api/monitor/usage/quota/limit",
            """{"success":true,"data":{"limits":[{"type":"TOKENS_LIMIT","unit":3,"percentage":27.5,"nextResetTime":1800000000000},{"type":"credit_limit","unit":6,"percentage":40,"nextResetTime":1799999000000}]}}""",
            27.5, null
        },
        {
            "https://api.kimi.com/coding/v1", ProviderUsageAdapterId.Kimi,
            "https://api.kimi.com/coding/v1/usages",
            """{"limits":[{"detail":{"limit":"100","remaining":"75","resetTime":"2027-01-15T08:00:00Z"}}],"usage":{"limit":1000,"remaining":900,"resetTime":1800000000}}""",
            25, null
        },
        {
            "https://api.minimaxi.com/v1", ProviderUsageAdapterId.MiniMax,
            "https://api.minimaxi.com/v1/api/openplatform/coding_plan/remains",
            """{"base_resp":{"status_code":0},"model_remains":[{"model_name":"video","current_interval_remaining_percent":1},{"model_name":"general","current_interval_remaining_percent":"80","end_time":1800000000000,"current_weekly_status":1,"current_weekly_remaining_percent":70,"weekly_end_time":1800500000}]}""",
            20, null
        },
        {
            "https://api.deepseek.com/v1", ProviderUsageAdapterId.DeepSeek,
            "https://api.deepseek.com/user/balance",
            """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"12.34"}]}""",
            12.34, "CNY"
        },
        {
            "https://api.siliconflow.cn/v1", ProviderUsageAdapterId.SiliconFlow,
            "https://api.siliconflow.cn/v1/user/info",
            """{"code":20000,"data":{"totalBalance":"43.21"}}""",
            43.21, "CNY"
        },
        {
            "https://openrouter.ai/api/v1", ProviderUsageAdapterId.OpenRouter,
            "https://openrouter.ai/api/v1/credits",
            """{"data":{"total_credits":"100","total_usage":"21.75"}}""",
            78.25, "USD"
        },
        {
            "https://api.stepfun.ai/v1", ProviderUsageAdapterId.StepFun,
            "https://api.stepfun.com/v1/accounts", """{"balance":"16.75"}""", 16.75, "CNY"
        },
        {
            "https://api.novita.ai/v3/openai", ProviderUsageAdapterId.Novita,
            "https://api.novita.ai/v3/user/balance", """{"availableBalance":"123456"}""", 12.3456, "USD"
        },
    };

    [Theory]
    [MemberData(nameof(VendorFixtures))]
    public async Task VendorFixture_UsesCanonicalEndpointAndNormalizesTiers(
        string baseUrl, ProviderUsageAdapterId adapter, string endpoint, string fixture, double expected, string? unit)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(fixture)));
        using var client = new HttpClient(handler);
        var service = new ProviderUsageService(client);

        var snapshot = await service.GetUsageAsync("provider-a", baseUrl, TestKey);

        Assert.Null(snapshot.Error);
        Assert.True(snapshot.CredentialValid);
        Assert.Equal("provider-a", snapshot.ProviderId);
        Assert.Equal(adapter, snapshot.AdapterId);
        Assert.Equal(ProviderUsageDetection.GetKind(adapter), snapshot.Kind);
        Assert.NotEqual(default, snapshot.QueriedAt);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(endpoint, request.Url);
        Assert.Equal("GET", request.Method);
        Assert.Equal(adapter == ProviderUsageAdapterId.Zhipu ? TestKey : $"Bearer {TestKey}", request.Authorization);
        Assert.True(request.CanBeCanceled);
        var tier = snapshot.Tiers[0];
        Assert.Equal(expected, (unit is null ? tier.UsedPercentage : tier.Remaining)!.Value, 6);
        Assert.Equal(unit, tier.Unit);
        if (unit is null)
        {
            Assert.Equal(ProviderUsageTierId.FiveHour, tier.Id);
            Assert.NotNull(tier.ResetsAt);
            Assert.Equal(ProviderUsageTierId.Weekly, snapshot.Tiers[1].Id);
            Assert.NotNull(snapshot.Tiers[1].ResetsAt);
        }
        else
        {
            Assert.Equal(ProviderUsageTierId.Balance, Assert.Single(snapshot.Tiers).Id);
        }
    }

    [Theory]
    [InlineData("https://bigmodel.cn/custom", ProviderUsageAdapterId.Zhipu, "https://open.bigmodel.cn/api/monitor/usage/quota/limit")]
    [InlineData("https://api.z.ai/api/coding/paas/v4", ProviderUsageAdapterId.Zhipu, "https://api.z.ai/api/monitor/usage/quota/limit")]
    [InlineData("https://api.minimax.cn", ProviderUsageAdapterId.MiniMax, "https://api.minimaxi.com/v1/api/openplatform/coding_plan/remains")]
    [InlineData("https://api.minimax.io/anthropic", ProviderUsageAdapterId.MiniMax, "https://api.minimax.io/v1/api/openplatform/coding_plan/remains")]
    [InlineData("https://api.siliconflow.com/v1", ProviderUsageAdapterId.SiliconFlow, "https://api.siliconflow.com/v1/user/info")]
    [InlineData("https://api.stepfun.com/v1", ProviderUsageAdapterId.StepFun, "https://api.stepfun.com/v1/accounts")]
    [InlineData(" http://API.DEEPSEEK.COM:8123/arbitrary?key=ignored#fragment ", ProviderUsageAdapterId.DeepSeek, "https://api.deepseek.com/user/balance")]
    public async Task HostAliases_AlwaysUseFixedHttpsEndpoint(string baseUrl, ProviderUsageAdapterId adapter, string endpoint)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json("{}")));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", baseUrl, TestKey);

        Assert.Equal(adapter, result.AdapterId);
        Assert.Equal(endpoint, Assert.Single(handler.Requests).Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("api.deepseek.com")]
    [InlineData("https://api.deepseek.com.attacker.example/v1")]
    [InlineData("https://api.minimax.cn.example.com")]
    [InlineData("https://api.minimax.io@attacker.example")]
    [InlineData("https://attacker.example@api.deepseek.com")]
    [InlineData("https://api.deepseek.com./v1")]
    [InlineData("https://evil-api.deepseek.com")]
    [InlineData("https://api.deepseek.com%2eattacker.example")]
    [InlineData("https://api.deepseek.com\\@attacker.example")]
    [InlineData("https://api.deepseеk.com")]
    [InlineData("https://api.moonshot.cn/v1")]
    [InlineData("ftp://api.deepseek.com")]
    [InlineData("file://api.deepseek.com")]
    public async Task UnsupportedOrSpoofedHost_PerformsNoNetwork(string? baseUrl)
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", baseUrl, TestKey);

        Assert.Equal(ProviderUsageErrorCode.Unsupported, result.Error?.Code);
        Assert.False(result.CredentialValid);
        Assert.Empty(result.Tiers);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task MissingApiKey_PerformsNoNetwork(string? key)
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.kimi.com", key);

        Assert.Equal(ProviderUsageErrorCode.MissingApiKey, result.Error?.Code);
        Assert.Equal(ProviderUsageAdapterId.Kimi, result.AdapterId);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(401, ProviderUsageErrorCode.AuthFailed, false)]
    [InlineData(403, ProviderUsageErrorCode.AuthFailed, false)]
    [InlineData(429, ProviderUsageErrorCode.BadResponse, true)]
    [InlineData(500, ProviderUsageErrorCode.BadResponse, true)]
    [InlineData(302, ProviderUsageErrorCode.BadResponse, true)]
    [InlineData(307, ProviderUsageErrorCode.BadResponse, true)]
    public async Task HttpErrors_AreStructuredAndNeverExposeBody(int status, ProviderUsageErrorCode code, bool valid)
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(TestKey) };
            response.Headers.Location = new Uri("https://attacker.example/steal");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.deepseek.com", TestKey);

        Assert.Equal(code, result.Error?.Code);
        Assert.Equal(valid, result.CredentialValid);
        Assert.DoesNotContain(TestKey, result.ToString());
        Assert.DoesNotContain("attacker.example", result.ToString());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not-json test-only-not-an-account-key")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"balance_infos\":[{\"total_balance\":\"NaN\"}]}")]
    [InlineData("{\"balance_infos\":[{\"total_balance\":\"Infinity\"}]}")]
    public async Task InvalidResponses_ReturnBadResponseWithoutEcho(string fixture)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(fixture)));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.deepseek.com", TestKey);

        Assert.Equal(ProviderUsageErrorCode.BadResponse, result.Error?.Code);
        Assert.DoesNotContain(TestKey, result.ToString());
        Assert.Empty(result.Tiers);
    }

    [Theory]
    [InlineData("https://api.z.ai", "{\"success\":false,\"msg\":\"test-only-not-an-account-key\"}")]
    [InlineData("https://api.minimax.io", "{\"base_resp\":{\"status_code\":1004,\"status_msg\":\"test-only-not-an-account-key\"}}")]
    public async Task BusinessErrors_DoNotEchoVendorMessages(string baseUrl, string fixture)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(fixture)));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", baseUrl, TestKey);

        Assert.Equal(ProviderUsageErrorCode.BadResponse, result.Error?.Code);
        Assert.DoesNotContain(TestKey, result.ToString());
    }

    [Fact]
    public async Task OversizedResponse_IsRejectedWithoutEcho()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(TestKey + new string('x', 1_048_576))));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.deepseek.com", TestKey);

        Assert.Equal(ProviderUsageErrorCode.BadResponse, result.Error?.Code);
        Assert.DoesNotContain(TestKey, result.ToString());
    }

    [Fact]
    public async Task NetworkFailure_DoesNotEchoException()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException(TestKey));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.deepseek.com", TestKey);

        Assert.Equal(ProviderUsageErrorCode.Network, result.Error?.Code);
        Assert.DoesNotContain(TestKey, result.ToString());
    }

    [Fact]
    public async Task CallerCancellation_IsLinkedAndPropagated()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var query = new ProviderUsageService(client).GetUsageAsync("p", "https://api.deepseek.com", TestKey, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.True(Assert.Single(handler.Requests).CanBeCanceled);
        Assert.Equal(TimeSpan.FromSeconds(15), ProviderUsageService.RequestTimeout);
    }

    [Fact]
    public async Task TimeoutFailure_IsNetworkError()
    {
        using var handler = new StubHandler((_, _) => throw new TaskCanceledException(TestKey));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.deepseek.com", TestKey);

        Assert.Equal(ProviderUsageErrorCode.Network, result.Error?.Code);
        Assert.DoesNotContain(TestKey, result.ToString());
    }

    [Fact]
    public async Task HeaderInjection_IsRejectedWithoutNetwork()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.deepseek.com", "secret\r\nInjected: value");

        Assert.Equal(ProviderUsageErrorCode.AuthFailed, result.Error?.Code);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Fact]
    public void DefaultTransport_DisablesRedirectsCookiesAndImplicitCredentials()
    {
        using var client = ProviderUsageService.CreateHttpClient();
        var field = typeof(HttpMessageInvoker).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => typeof(HttpMessageHandler).IsAssignableFrom(candidate.FieldType));
        var handler = Assert.IsType<HttpClientHandler>(field.GetValue(client));

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Zhipu_ExplicitWindowsBeatResetOrdering_AndMonthlyIsPreserved()
    {
        const string fixture = """{"data":{"limits":[{"type":"TOKENS_LIMIT","unit":6,"percentage":42,"nextResetTime":1800000000},{"type":"TOKENS_LIMIT","unit":3,"percentage":21,"nextResetTime":1800001000000},{"type":"CREDIT_LIMIT","unit":5,"percentage":63,"nextResetTime":"2027-02-01T00:00:00Z"}]}}""";
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(fixture)));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.z.ai", TestKey);

        Assert.Null(result.Error);
        Assert.Equal(new[] { ProviderUsageTierId.FiveHour, ProviderUsageTierId.Weekly, ProviderUsageTierId.Monthly },
            result.Tiers.Select(tier => tier.Id));
        Assert.Equal(21d, result.Tiers[0].UsedPercentage);
        Assert.Equal(42d, result.Tiers[1].UsedPercentage);
        Assert.Equal(63d, result.Tiers[2].UsedPercentage);
        Assert.True(result.Tiers[0].ResetsAt > result.Tiers[1].ResetsAt);
    }

    [Fact]
    public async Task Zhipu_LegacyUnclassifiedWindowWithoutResetBecomesFiveHour()
    {
        const string fixture = """{"data":{"limits":[{"type":"TOKENS_LIMIT","percentage":90,"nextResetTime":1800000000000},{"type":"tokens_limit","percentage":0}]}}""";
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(fixture)));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.z.ai", TestKey);

        Assert.Equal(0d, result.Tiers[0].UsedPercentage);
        Assert.Null(result.Tiers[0].ResetsAt);
        Assert.Equal(90d, result.Tiers[1].UsedPercentage);
    }

    [Fact]
    public async Task MiniMax_InactiveWeeklyWindowIsHidden()
    {
        const string fixture = """{"model_remains":[{"model_name":"general","current_interval_remaining_percent":9.5,"current_weekly_status":3,"current_weekly_remaining_percent":100}]}""";
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(fixture)));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.minimax.io", TestKey);

        var tier = Assert.Single(result.Tiers);
        Assert.Equal(ProviderUsageTierId.FiveHour, tier.Id);
        Assert.Equal(90.5, tier.UsedPercentage);
    }

    [Fact]
    public async Task SiliconFlowInternational_UsesUsd()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json("{\"data\":{\"totalBalance\":\"5.25\"}}")));
        using var client = new HttpClient(handler);
        var result = await new ProviderUsageService(client).GetUsageAsync("p", "https://api.siliconflow.com", TestKey);

        Assert.Equal("USD", Assert.Single(result.Tiers).Unit);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed record Request(string Url, string Method, string? Authorization, bool CanBeCanceled);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Request(request.RequestUri!.AbsoluteUri, request.Method.Method,
                request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null,
                cancellationToken.CanBeCanceled));
            return response(request, cancellationToken);
        }
    }
}
