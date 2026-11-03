using System.Net;
using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("api-gateway")]
public sealed class SessionEndpointTests
{
    private readonly GatewayHarness _harness;

    public SessionEndpointTests(GatewayHarness harness) => _harness = harness;

    [Fact]
    public async Task Models_WithoutConfiguredCatalog_ReturnsEmptyCatalog()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.GetWithTokenAsync("api/models", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("""{"models":[],"defaultSelection":null}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Models_ReadsCurrentCatalogOnEveryAuthenticatedRequest()
    {
        var calls = 0;
        var catalog = new RemoteModelCatalog(
            [
                new RemoteModelChoice("provider-a", "Friendly Provider", "model-a", "Alpha", ["low", "high"]),
                new RemoteModelChoice("provider-b", "Other Provider", "model-b", "Beta", []),
            ],
            new ModelSelection("provider-a", "model-a", new ModelCallOptions("high", 8192)));
        var harness = new ModelCatalogGatewayHarness(() =>
        {
            Interlocked.Increment(ref calls);
            return Volatile.Read(ref catalog);
        });
        await harness.InitializeAsync();
        try
        {
            using var unauthorized = await harness.Client.GetAsync("api/models");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(0, Volatile.Read(ref calls));
            var (_, token) = await harness.PairAsync();

            using var first = await harness.GetWithTokenAsync("api/models", token);

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
            Assert.Equal(
                """{"models":[{"providerId":"provider-a","providerLabel":"Friendly Provider","modelId":"model-a","displayName":"Alpha","reasoningLevels":["low","high"]},{"providerId":"provider-b","providerLabel":"Other Provider","modelId":"model-b","displayName":"Beta","reasoningLevels":[]}],"defaultSelection":{"providerId":"provider-a","modelId":"model-a","options":{"reasoningLevel":"high","maxOutputTokens":8192}}}""",
                await first.Content.ReadAsStringAsync());
            Assert.Equal(1, Volatile.Read(ref calls));

            Volatile.Write(ref catalog, new RemoteModelCatalog(
                [new RemoteModelChoice("provider-c", "Updated Provider", "model-c", "Gamma", ["medium"])], null));

            using var second = await harness.GetWithTokenAsync("api/models", token);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal("no-store", second.Headers.CacheControl?.ToString());
            Assert.Equal(
                """{"models":[{"providerId":"provider-c","providerLabel":"Updated Provider","modelId":"model-c","displayName":"Gamma","reasoningLevels":["medium"]}],"defaultSelection":null}""",
                await second.Content.ReadAsStringAsync());
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("api/slash-catalog")]
    [InlineData("api/sessions/sess_unknown/slash-catalog")]
    public async Task SlashCatalog_WithoutConfiguredCallback_ReturnsEmptyEntries(string path)
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.GetWithTokenAsync(path, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("""{"entries":[]}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SlashCatalog_AuthenticatesBeforeInvokingCallbackAndReadsLiveEntries()
    {
        var calls = 0;
        var workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "remote-slash-workspace"));
        var catalog = new RemoteSlashCatalog(
            [new("plan", "Plan without making changes", "plan"), new("report", "Research topics", "skill")]);
        var harness = new SlashCatalogGatewayHarness((sessionId, workspaceRoot, cancellationToken) =>
        {
            Assert.Null(sessionId);
            Assert.Equal(workspace, workspaceRoot);
            Assert.True(cancellationToken.CanBeCanceled);
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<RemoteSlashCatalog?>(Volatile.Read(ref catalog));
        });
        await harness.InitializeAsync();
        try
        {
            foreach (var path in new[] { "api/slash-catalog?workspaceRoot=relative", "api/sessions/sess_unknown/slash-catalog" })
            {
                using var unauthorized = await harness.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                Assert.Equal("no-store", unauthorized.Headers.CacheControl?.ToString());
            }
            Assert.Equal(0, Volatile.Read(ref calls));
            var (deviceId, token) = await harness.PairAsync();
            var draftPath = "api/slash-catalog?workspaceRoot=" + Uri.EscapeDataString(workspace);

            using (var first = await harness.GetWithTokenAsync(draftPath, token))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
                Assert.Equal(
                    """{"entries":[{"name":"plan","description":"Plan without making changes","kind":"plan"},{"name":"report","description":"Research topics","kind":"skill"}]}""",
                    await first.Content.ReadAsStringAsync());
            }
            Assert.Equal(1, Volatile.Read(ref calls));
            Volatile.Write(ref catalog, new RemoteSlashCatalog([new("review", "Inspect changes", "skill")]));

            using (var second = await harness.GetWithTokenAsync(draftPath, token))
            {
                Assert.Equal(HttpStatusCode.OK, second.StatusCode);
                Assert.Equal("no-store", second.Headers.CacheControl?.ToString());
                Assert.Equal("""{"entries":[{"name":"review","description":"Inspect changes","kind":"skill"}]}""",
                    await second.Content.ReadAsStringAsync());
            }
            Assert.Equal(2, Volatile.Read(ref calls));

            await harness.Gateway.RevokeDeviceAsync(deviceId);
            using var revoked = await harness.GetWithTokenAsync(draftPath, token);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task SlashCatalog_SessionRoutePassesOnlyTrustedSessionIdAndMapsUnknownSession()
    {
        var knownId = new SessionId("sess_catalog");
        var calls = new List<SessionId?>();
        var harness = new SlashCatalogGatewayHarness((sessionId, workspaceRoot, _) =>
        {
            calls.Add(sessionId);
            Assert.Null(workspaceRoot);
            return ValueTask.FromResult<RemoteSlashCatalog?>(sessionId == knownId
                ? new RemoteSlashCatalog([new("workspace-skill", "Known session skill", "skill")])
                : null);
        });
        await harness.InitializeAsync();
        try
        {
            var (_, token) = await harness.PairAsync();
            using var known = await harness.GetWithTokenAsync(
                $"api/sessions/{knownId.Value}/slash-catalog?workspaceRoot=relative-client-override", token);
            Assert.Equal(HttpStatusCode.OK, known.StatusCode);
            Assert.Equal("no-store", known.Headers.CacheControl?.ToString());
            Assert.Equal("""{"entries":[{"name":"workspace-skill","description":"Known session skill","kind":"skill"}]}""",
                await known.Content.ReadAsStringAsync());

            using var unknown = await harness.GetWithTokenAsync("api/sessions/sess_missing/slash-catalog", token);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal("no-store", unknown.Headers.CacheControl?.ToString());
            Assert.Equal("""{"error":"not_found","reason":"session"}""", await unknown.Content.ReadAsStringAsync());
            Assert.Equal(new SessionId?[] { knownId, new("sess_missing") }, calls);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task SlashCatalog_DraftAllowsBlankAndAbsoluteWorkspaceButRejectsMalformedPathsBeforeCallback()
    {
        var workspaces = new List<string?>();
        var harness = new SlashCatalogGatewayHarness((sessionId, workspaceRoot, _) =>
        {
            Assert.Null(sessionId);
            workspaces.Add(workspaceRoot);
            return ValueTask.FromResult<RemoteSlashCatalog?>(new RemoteSlashCatalog([]));
        });
        await harness.InitializeAsync();
        try
        {
            var (_, token) = await harness.PairAsync();
            foreach (var query in new[] { "", "?workspaceRoot=", "?workspaceRoot=%20%20%09" })
            {
                using var blank = await harness.GetWithTokenAsync("api/slash-catalog" + query, token);
                Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
            }
            Assert.Equal(new string?[] { null, null, null }, workspaces);
            var workspace = Path.Combine(Path.GetTempPath(), "remote-slash-draft", "..", "selected");
            using (var absolute = await harness.GetWithTokenAsync(
                "api/slash-catalog?workspaceRoot=" + Uri.EscapeDataString(workspace), token))
            {
                Assert.Equal(HttpStatusCode.OK, absolute.StatusCode);
                Assert.Equal(Path.GetFullPath(workspace), workspaces[^1]);
            }
            var callsBeforeInvalid = workspaces.Count;
            foreach (var invalid in new[] { "relative", "../workspace", @"C:relative", @"\rooted", "file:///workspace", Path.GetTempPath() + "invalid\0path" })
            {
                using var response = await harness.GetWithTokenAsync(
                    "api/slash-catalog?workspaceRoot=" + Uri.EscapeDataString(invalid), token);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                Assert.Equal("""{"error":"bad_request","reason":"workspaceRoot"}""", await response.Content.ReadAsStringAsync());
            }
            using var multiple = await harness.GetWithTokenAsync(
                "api/slash-catalog?workspaceRoot=&workspaceRoot=relative", token);
            Assert.Equal(HttpStatusCode.BadRequest, multiple.StatusCode);
            Assert.Equal(callsBeforeInvalid, workspaces.Count);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateSession_RoundTripsWorkspaceTitleAndYoloMode()
    {
        var (_, token) = await _harness.PairAsync();
        var workspaceRoot = $@"C:\remote-tests\{Guid.NewGuid():N}";

        using var created = await _harness.SendWithTokenAsync(
            HttpMethod.Post,
            "api/sessions",
            token,
            GatewayHarness.Serialize(new { workspaceRoot, title = "Remote suite", mode = "yolo" }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdBody = await created.Content.ReadAsStringAsync();
        using var handle = JsonDocument.Parse(createdBody);
        var sessionId = handle.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(sessionId));
        Assert.Equal(workspaceRoot, handle.RootElement.GetProperty("workspaceRoot").GetString());
        Assert.Equal("Remote suite", handle.RootElement.GetProperty("title").GetString());

        using var list = await _harness.GetWithTokenAsync("api/sessions", token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listed = await list.ReadJsonAsync();
        Assert.Equal(JsonValueKind.Array, listed.RootElement.ValueKind);
        JsonElement? entry = EnumerateSessions(listed.RootElement)
            .FirstOrDefault(summary => summary.GetProperty("id").GetString() == sessionId);
        Assert.NotNull(entry);
        Assert.Equal(workspaceRoot, entry.Value.GetProperty("workspaceRoot").GetString());
        Assert.Equal("Remote suite", entry.Value.GetProperty("title").GetString());
        // Enum members serialize in Pascal case on the gateway wire (AlcolerJson shape).
        var permissionMode = entry.Value.GetProperty("permissionMode").GetString();
        Assert.True(
            string.Equals("yolo", permissionMode, StringComparison.OrdinalIgnoreCase),
            $"permissionMode was {permissionMode}");
        Assert.False(entry.Value.GetProperty("isArchived").GetBoolean());
    }

    [Fact]
    public async Task CreateSession_PassesPlanAndWorkflowIntentWithBackwardCompatibleDefaults()
    {
        var runtime = new SessionCreationRuntime();
        var harness = new SessionCreationGatewayHarness(runtime);
        await harness.InitializeAsync();
        try
        {
            var (_, token) = await harness.PairAsync();
            var workspaceRoot = Path.Combine(Path.GetTempPath(), "remote-session-intent");
            using (var planned = await harness.SendWithTokenAsync(HttpMethod.Post, "api/sessions", token,
                GatewayHarness.Serialize(new { workspaceRoot, title = "Planned", mode = "edit", planEnabled = true, activeWorkflows = new[] { "orchestrator" } })))
            {
                Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
            }
            var intent = Assert.Single(runtime.Options);
            Assert.Equal(workspaceRoot, intent.WorkspaceRoot);
            Assert.Equal("Planned", intent.Title);
            Assert.Equal(PermissionMode.Edit, intent.PermissionMode);
            Assert.True(intent.PlanEnabled);
            Assert.Equal(new[] { "orchestrator" }, intent.ActiveWorkflows);

            using (var legacy = await harness.SendWithTokenAsync(HttpMethod.Post, "api/sessions", token,
                GatewayHarness.Serialize(new { workspaceRoot })))
            {
                Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
            }
            Assert.Equal(PermissionMode.Build, runtime.Options[1].PermissionMode);
            Assert.False(runtime.Options[1].PlanEnabled);
            Assert.Null(runtime.Options[1].ActiveWorkflows);

            using var cleared = await harness.SendWithTokenAsync(HttpMethod.Post, "api/sessions", token,
                GatewayHarness.Serialize(new { workspaceRoot, planEnabled = false, activeWorkflows = Array.Empty<string>() }));
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            Assert.False(runtime.Options[2].PlanEnabled);
            Assert.Empty(Assert.IsType<string[]>(runtime.Options[2].ActiveWorkflows));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateSession_RejectsMalformedIntentBeforeInvokingRuntime()
    {
        var runtime = new SessionCreationRuntime();
        var harness = new SessionCreationGatewayHarness(runtime);
        await harness.InitializeAsync();
        try
        {
            var (_, token) = await harness.PairAsync();
            var malformed = new[]
            {
                "null", "[]", "{}", "{", """{"workspaceRoot":null}""", """{"workspaceRoot":42}""", """{"workspaceRoot":" "}""",
                """{"workspaceRoot":"workspace","title":42}""", """{"workspaceRoot":"workspace","title":{}}""",
                """{"workspaceRoot":"workspace","mode":false}""", """{"workspaceRoot":"workspace","mode":[]}""",
                """{"workspaceRoot":"workspace","planEnabled":null}""", """{"workspaceRoot":"workspace","planEnabled":"true"}""",
                """{"workspaceRoot":"workspace","planEnabled":1}""", """{"workspaceRoot":"workspace","planEnabled":{}}""",
                """{"workspaceRoot":"workspace","activeWorkflows":null}""", """{"workspaceRoot":"workspace","activeWorkflows":"orchestrator"}""",
                """{"workspaceRoot":"workspace","activeWorkflows":true}""", """{"workspaceRoot":"workspace","activeWorkflows":{}}""",
                """{"workspaceRoot":"workspace","activeWorkflows":[null]}""", """{"workspaceRoot":"workspace","activeWorkflows":[1]}""",
                """{"workspaceRoot":"workspace","activeWorkflows":[{}]}""", """{"workspaceRoot":"workspace","activeWorkflows":[""]}""",
                """{"workspaceRoot":"workspace","activeWorkflows":[" \t "]}""",
                """{"workspaceRoot":"workspace","activeWorkflows":["orchestrator","orchestrator","orchestrator","orchestrator"]}""",
            };
            foreach (var json in malformed)
            {
                using var response = await harness.SendWithTokenAsync(HttpMethod.Post, "api/sessions", token, json);
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400 for {json}; got {(int)response.StatusCode}.");
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                Assert.Empty(runtime.Options);
            }
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateSession_WorkflowValidationFailureIsBadRequestWithoutPrivateDetails()
    {
        var runtime = new SessionCreationRuntime { RejectOptions = true };
        var harness = new SessionCreationGatewayHarness(runtime);
        await harness.InitializeAsync();
        try
        {
            var (_, token) = await harness.PairAsync();
            using var response = await harness.SendWithTokenAsync(HttpMethod.Post, "api/sessions", token,
                GatewayHarness.Serialize(new { workspaceRoot = "workspace", activeWorkflows = new[] { "unknown-workflow" } }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("""{"error":"bad_request","reason":"session_options"}""", await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "unknown-workflow" }, Assert.Single(runtime.Options).ActiveWorkflows);
            Assert.DoesNotContain("session_created", await File.ReadAllTextAsync(Path.Combine(harness.DataDirectory, "audit.jsonl")), StringComparison.Ordinal);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateSession_WithUnknownMode_IsRejected()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.SendWithTokenAsync(
            HttpMethod.Post,
            "api/sessions",
            token,
            GatewayHarness.Serialize(new { workspaceRoot = $@"C:\remote-tests\{Guid.NewGuid():N}", mode = "turbo" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateSession_WithoutWorkspaceRoot_IsRejected()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.SendWithTokenAsync(
            HttpMethod.Post, "api/sessions", token, GatewayHarness.Serialize(new { title = "no workspace" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListSessions_WithUnknownKind_IsRejected()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.GetWithTokenAsync("api/sessions?kind=bogus", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListSessions_IncludesSessionActivity()
    {
        var (_, token) = await _harness.PairAsync();
        var sessionId = await _harness.CreateSessionAsync(token, title: "activity");

        using (var idleResponse = await _harness.GetWithTokenAsync("api/sessions", token))
        {
            Assert.Equal(HttpStatusCode.OK, idleResponse.StatusCode);
            using var idle = await idleResponse.ReadJsonAsync();
            var entry = FindSession(idle.RootElement, sessionId.Value);
            Assert.NotNull(entry);
            var activity = entry.Value.GetProperty("activity");
            Assert.Equal(JsonValueKind.Object, activity.ValueKind);
            Assert.Equal("Idle", activity.GetProperty("phase").GetString());
            Assert.Equal(JsonValueKind.Null, activity.GetProperty("lastOutcome").ValueKind);
        }

        _harness.Runtime.SeedPendingPermission(sessionId);

        using var pendingResponse = await _harness.GetWithTokenAsync("api/sessions", token);
        Assert.Equal(HttpStatusCode.OK, pendingResponse.StatusCode);
        using var pending = await pendingResponse.ReadJsonAsync();
        var pendingEntry = FindSession(pending.RootElement, sessionId.Value);
        Assert.NotNull(pendingEntry);
        var pendingActivity = pendingEntry.Value.GetProperty("activity");
        Assert.Equal(JsonValueKind.Object, pendingActivity.ValueKind);
        Assert.Equal("AwaitingPermission", pendingActivity.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task ArchiveSession_RemovesFromActive_AndShowsInArchivedKind()
    {
        var (_, token) = await _harness.PairAsync();
        var sessionId = await _harness.CreateSessionAsync(token, title: "to archive");

        using var archive = await _harness.SendWithTokenAsync(
            HttpMethod.Post, $"api/sessions/{sessionId.Value}/archive", token);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        using var activeResponse = await _harness.GetWithTokenAsync("api/sessions", token);
        using var active = await activeResponse.ReadJsonAsync();
        Assert.DoesNotContain(
            sessionId.Value,
            string.Join(",", EnumerateSessions(active.RootElement).Select(s => s.GetProperty("id").GetString())),
            StringComparison.Ordinal);

        using var archivedResponse = await _harness.GetWithTokenAsync("api/sessions?kind=archived", token);
        using var archived = await archivedResponse.ReadJsonAsync();
        JsonElement? entry = EnumerateSessions(archived.RootElement)
            .FirstOrDefault(summary => summary.GetProperty("id").GetString() == sessionId.Value);
        Assert.NotNull(entry);
        Assert.True(entry.Value.GetProperty("isArchived").GetBoolean());
    }

    [Fact]
    public async Task ResumeSession_UnknownSession_IsNotFound()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.SendWithTokenAsync(
            HttpMethod.Post, "api/sessions/sess_does_not_exist/resume", token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.Equal("not_found", document.RootElement.GetProperty("error").GetString());
        Assert.Equal("session", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ResumeSession_KnownSession_ReturnsHandle()
    {
        var (_, token) = await _harness.PairAsync();
        var sessionId = await _harness.CreateSessionAsync(token, title: "resumable");

        using var response = await _harness.SendWithTokenAsync(
            HttpMethod.Post, $"api/sessions/{sessionId.Value}/resume", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.Equal(sessionId.Value, document.RootElement.GetProperty("id").GetString());
        Assert.Equal("resumable", document.RootElement.GetProperty("title").GetString());
    }

    private sealed class ModelCatalogGatewayHarness(Func<RemoteModelCatalog> modelCatalog) : GatewayHarness
    {
        protected override RemoteGatewayOptions CreateOptions(int port) => new()
        {
            Runtime = () => Runtime,
            Port = port,
            DataDirectory = DataDirectory,
            ProductVersion = Version,
            ModelCatalog = modelCatalog,
        };
    }

    private sealed class SlashCatalogGatewayHarness(
        Func<SessionId?, string?, CancellationToken, ValueTask<RemoteSlashCatalog?>> slashCatalog) : GatewayHarness
    {
        protected override RemoteGatewayOptions CreateOptions(int port) => new()
        {
            Runtime = () => Runtime,
            Port = port,
            DataDirectory = DataDirectory,
            ProductVersion = Version,
            SlashCatalog = slashCatalog,
        };
    }

    private sealed class SessionCreationGatewayHarness(IAgentRuntime runtime) : GatewayHarness
    {
        protected override RemoteGatewayOptions CreateOptions(int port) => new()
        {
            Runtime = () => runtime,
            Port = port,
            DataDirectory = DataDirectory,
            ProductVersion = Version,
        };
    }

    private sealed class SessionCreationRuntime : IAgentRuntime
    {
        public List<SessionOptions> Options { get; } = [];
        public bool RejectOptions { get; init; }

        public ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken)
        {
            Options.Add(options);
            return RejectOptions
                ? ValueTask.FromException<SessionHandle>(new InvalidOperationException("Private workflow validation detail"))
                : ValueTask.FromResult(new SessionHandle(StrongId.NewSessionId(), options.WorkspaceRoot, options.Title ?? "Session"));
        }

        public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
            string? workspaceRoot, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<int> UnloadIdleSessionsAsync(
            TimeSpan idleThreshold, SessionId? keepOpen, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IConversationProjection Subscribe(SessionId sessionId) => throw new NotSupportedException();

        public IConversationProjection? TryObserve(SessionId sessionId) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IEnumerable<JsonElement> EnumerateSessions(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : [];

    private static JsonElement? FindSession(JsonElement root, string sessionId) =>
        EnumerateSessions(root)
            .Where(summary => summary.GetProperty("id").GetString() == sessionId)
            .Select(summary => (JsonElement?)summary)
            .FirstOrDefault();
}
