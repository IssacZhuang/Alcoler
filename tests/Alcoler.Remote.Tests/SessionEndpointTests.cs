using System.Net;
using System.Text.Json;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("api-gateway")]
public sealed class SessionEndpointTests
{
    private readonly GatewayHarness _harness;

    public SessionEndpointTests(GatewayHarness harness) => _harness = harness;

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

    private static IEnumerable<JsonElement> EnumerateSessions(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : [];

    private static JsonElement? FindSession(JsonElement root, string sessionId) =>
        EnumerateSessions(root)
            .Where(summary => summary.GetProperty("id").GetString() == sessionId)
            .Select(summary => (JsonElement?)summary)
            .FirstOrDefault();
}
