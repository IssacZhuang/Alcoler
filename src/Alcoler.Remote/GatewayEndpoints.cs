using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SessionOptions = Alcoler.Contracts.Sessions.SessionOptions;

namespace Alcoler.Remote;

internal static class GatewayEndpoints
{
    private const string ArtifactSchemePrefix = "alcoler-artifact://";

    private static readonly Dictionary<Type, string> CommandDiscriminators = typeof(ConversationCommand)
        .GetCustomAttributes<JsonDerivedTypeAttribute>()
        .ToDictionary(
            attribute => attribute.DerivedType,
            attribute => attribute.TypeDiscriminator?.ToString() ?? attribute.DerivedType.Name);

    public static void Map(WebApplication app, GatewayState state)
    {
        MapInfo(app, state);
        MapModels(app, state);
        MapSlashCatalog(app, state);
        MapSessions(app, state);
        MapCommands(app, state);
        MapArtifacts(app, state);
        MapPair(app, state);
        MapStream(app, state);
    }

    private static void MapInfo(WebApplication app, GatewayState state)
    {
        var version = state.Options.ProductVersion ?? "dev";
        app.MapGet("/api/info", () => GatewayJson.Plain(GatewayJson.Object(writer =>
        {
            writer.WriteString("product", "Alcoler");
            writer.WriteString("version", version);
        })));
    }

    private static void MapModels(WebApplication app, GatewayState state)
    {
        app.MapGet("/api/models", () => GatewayJson.Data(
            state.Options.ModelCatalog?.Invoke() ?? new RemoteModelCatalog([], null)));
    }

    private static void MapSlashCatalog(WebApplication app, GatewayState state)
    {
        app.MapGet("/api/slash-catalog", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (!TryReadDraftWorkspace(request, out var workspaceRoot))
                return GatewayJson.Error(400, "bad_request", "workspaceRoot");
            var catalog = state.Options.SlashCatalog is { } callback
                ? await callback(null, workspaceRoot, cancellationToken)
                : new RemoteSlashCatalog([]);
            return catalog is null
                ? GatewayJson.Error(404, "not_found", "session")
                : GatewayJson.Data(catalog);
        });

        app.MapGet("/api/sessions/{sessionId}/slash-catalog", async (string sessionId, CancellationToken cancellationToken) =>
        {
            var catalog = state.Options.SlashCatalog is { } callback
                ? await callback(new SessionId(sessionId), null, cancellationToken)
                : new RemoteSlashCatalog([]);
            return catalog is null
                ? GatewayJson.Error(404, "not_found", "session")
                : GatewayJson.Data(catalog);
        });
    }

    private static bool TryReadDraftWorkspace(HttpRequest request, out string? workspaceRoot)
    {
        workspaceRoot = null;
        var values = request.Query["workspaceRoot"];
        if (values.Count > 1)
            return false;
        var value = values.ToString().Trim();
        if (value.Length == 0)
            return true;
        try
        {
            if (!Path.IsPathFullyQualified(value) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return false;
            workspaceRoot = Path.GetFullPath(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void MapSessions(WebApplication app, GatewayState state)
    {
        app.MapGet("/api/sessions", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (!TryParseListRequest(request, out var limit, out var kind))
                return GatewayJson.Error(400, "bad_request", "query");
            var runtime = state.Options.Runtime();
            IReadOnlyList<SessionSummary> sessions;
            try
            {
                sessions = kind == SessionListKind.Archived
                    ? await runtime.ListSessionsAsync(null, limit, SessionListKind.Archived, cancellationToken)
                    : await runtime.ListSessionsAsync(null, limit, cancellationToken);
            }
            catch (NotSupportedException)
            {
                return GatewayJson.Error(501, "not_supported", "archived_sessions");
            }
            return GatewayJson.Plain(WriteSessionList(sessions, runtime as ISessionActivityRuntime));
        });

        app.MapPost("/api/sessions", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            CreateSessionRequest? payload;
            try
            {
                payload = await TryReadCreateRequest(request, cancellationToken);
            }
            catch (JsonException)
            {
                payload = null;
            }
            if (payload is null || string.IsNullOrWhiteSpace(payload.WorkspaceRoot) || !TryMapMode(payload.Mode, out var permissionMode))
                return GatewayJson.Error(400, "bad_request", "body");
            SessionHandle handle;
            try
            {
                handle = await state.Options.Runtime().CreateSessionAsync(new SessionOptions(
                    payload.WorkspaceRoot, payload.Title, permissionMode,
                    ActiveWorkflows: payload.ActiveWorkflows, PlanEnabled: payload.PlanEnabled), cancellationToken);
            }
            catch (InvalidOperationException)
            {
                return GatewayJson.Error(400, "bad_request", "session_options");
            }
            state.Audit.Write("session_created", sessionId: handle.Id.Value, detail: payload.WorkspaceRoot);
            return GatewayJson.Data(handle);
        });

        app.MapPost("/api/sessions/{sessionId}/resume", async (string sessionId, CancellationToken cancellationToken) =>
        {
            var runtime = state.Options.Runtime();
            try
            {
                var handle = await runtime.ResumeSessionAsync(new SessionId(sessionId), cancellationToken);
                return GatewayJson.Data(handle);
            }
            catch (InvalidOperationException)
            {
                return GatewayJson.Error(404, "not_found", "session");
            }
        });

        app.MapPost("/api/sessions/{sessionId}/archive", async (string sessionId, HttpRequest request, CancellationToken cancellationToken) =>
        {
            var archived = true;
            if (request.ContentLength is > 0)
            {
                try
                {
                    using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("archived", out var value))
                    {
                        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return GatewayJson.Error(400, "bad_request", "archived");
                        archived = value.GetBoolean();
                    }
                }
                catch (JsonException)
                {
                    return GatewayJson.Error(400, "bad_request", "body");
                }
            }
            var runtime = state.Options.Runtime();
            try
            {
                await runtime.SetSessionArchivedAsync(new SessionId(sessionId), archived, cancellationToken);
            }
            catch (NotSupportedException)
            {
                return GatewayJson.Error(501, "not_supported", "archive");
            }
            return Results.NoContent();
        });
    }

    private static void MapCommands(WebApplication app, GatewayState state)
    {
        app.MapPost("/api/sessions/{sessionId}/commands", async (string sessionId, HttpContext context, CancellationToken cancellationToken) =>
        {
            ConversationCommand? command;
            try
            {
                command = await JsonSerializer.DeserializeAsync<ConversationCommand>(
                    context.Request.Body, GatewayJson.Options, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // A missing discriminator surfaces as NotSupportedException; an
                // unknown one as JsonException. Both are malformed client input.
                command = null;
            }
            if (command is null)
                return GatewayJson.Error(400, "bad_request", "command");
            if (!string.Equals(command.SessionId.Value, sessionId, StringComparison.Ordinal))
                return GatewayJson.Error(400, "session_mismatch");
            var ack = await state.Options.Runtime().SubmitAsync(command, cancellationToken);
            state.Audit.Write(
                "command",
                deviceId: GatewayAuthentication.Device(context)?.Id,
                sessionId: sessionId,
                detail: $"{DiscriminatorOf(command)}:accepted={(ack.Accepted ? "true" : "false")}");
            return GatewayJson.Data(ack);
        });
    }

    private static void MapArtifacts(WebApplication app, GatewayState state)
    {
        app.MapGet("/api/artifacts", async (HttpRequest request, HttpContext context, CancellationToken cancellationToken) =>
        {
            var loader = state.Options.ArtifactLoader;
            if (loader is null)
                return GatewayJson.Error(501, "not_supported", "artifact_loader");
            var uri = request.Query["uri"].ToString();
            if (!uri.StartsWith(ArtifactSchemePrefix, StringComparison.OrdinalIgnoreCase))
                return GatewayJson.Error(400, "bad_request", "uri");
            var bytes = await loader(uri, cancellationToken);
            if (bytes is null)
                return GatewayJson.Error(404, "not_found", "artifact");
            state.Audit.Write("artifact_access", deviceId: GatewayAuthentication.Device(context)?.Id, detail: uri);
            var mime = request.Query["mime"].ToString();
            if (mime.Length == 0)
                mime = "application/octet-stream";
            return GatewayJson.Raw(bytes.Value.ToArray(), mime);
        });
    }

    private static void MapPair(WebApplication app, GatewayState state)
    {
        app.MapPost("/api/pair", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var now = DateTimeOffset.UtcNow;
            if (!state.PairRateLimit.IsAllowed(client, now))
                return GatewayJson.Error(429, "rate_limited");
            string? pairingToken = null;
            string? deviceName = null;
            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    pairingToken = ReadString(document.RootElement, "pairingToken");
                    deviceName = ReadString(document.RootElement, "deviceName");
                }
            }
            catch (JsonException)
            {
                return GatewayJson.Error(400, "bad_request", "body");
            }
            if (string.IsNullOrEmpty(pairingToken) || !state.PairingTokens.TryRedeem(pairingToken, now))
            {
                state.PairRateLimit.RecordFailure(client, now);
                state.Audit.Write("pair_failed");
                return GatewayJson.Error(401, "unauthorized", "pairing_token");
            }
            var name = ResolveDeviceName(deviceName, context.Request.Headers.UserAgent.ToString());
            var (device, token) = state.Devices.Register(name, now);
            state.Audit.Write("pair_succeeded", deviceId: device.Id);
            return GatewayJson.Plain(GatewayJson.Object(writer =>
            {
                writer.WriteString("deviceId", device.Id);
                writer.WriteString("deviceToken", token);
            }));
        });
    }

    private static void MapStream(WebApplication app, GatewayState state)
    {
        app.MapGet("/api/sessions/{sessionId}/stream", async (string sessionId, HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await GatewayJson.Error(400, "bad_request", "websocket").ExecuteAsync(context);
                return;
            }
            var socket = await context.WebSockets.AcceptWebSocketAsync();
            await SessionStream.RunAsync(state, new SessionId(sessionId), socket, context.RequestAborted);
        });
    }

    private static byte[] WriteSessionList(
        IReadOnlyList<SessionSummary> sessions,
        ISessionActivityRuntime? activityRuntime)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var session in sessions)
            {
                // Round-trip through GatewayJson so SessionSummary keeps the exact wire shape
                // produced by the contract source generator; activity is appended after it.
                using var document = JsonDocument.Parse(GatewayJson.Serialize(session));
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                    property.WriteTo(writer);
                WriteActivity(writer, activityRuntime?.GetSessionActivity(session.Id));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }

    private static void WriteActivity(Utf8JsonWriter writer, SessionActivitySnapshot? activity)
    {
        if (activity is null)
        {
            writer.WriteNull("activity");
            return;
        }
        writer.WriteStartObject("activity");
        writer.WriteString("phase", activity.Phase.ToString());
        if (activity.LastOutcome is { } outcome)
            writer.WriteString("lastOutcome", outcome.ToString());
        else
            writer.WriteNull("lastOutcome");
        writer.WriteEndObject();
    }

    private static bool TryParseListRequest(HttpRequest request, out int limit, out SessionListKind kind)
    {
        limit = 100;
        kind = SessionListKind.Active;
        var kindText = request.Query["kind"].ToString();
        if (kindText.Length == 0)
            kindText = "active";
        switch (kindText)
        {
            case "active":
                kind = SessionListKind.Active;
                break;
            case "archived":
                kind = SessionListKind.Archived;
                break;
            default:
                return false;
        }
        var limitText = request.Query["limit"].ToString();
        if (limitText.Length > 0 && !int.TryParse(limitText, out limit))
            return false;
        limit = Math.Clamp(limit, 1, 500);
        return true;
    }

    private static async Task<CreateSessionRequest?> TryReadCreateRequest(HttpRequest request, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var workspaceRoot = ReadString(root, "workspaceRoot");
        if (workspaceRoot is null
            || !TryReadOptionalString(root, "title", out var title)
            || !TryReadOptionalString(root, "mode", out var mode))
            return null;
        var planEnabled = false;
        if (root.TryGetProperty("planEnabled", out var plan))
        {
            if (plan.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            planEnabled = plan.GetBoolean();
        }
        string[]? activeWorkflows = null;
        if (root.TryGetProperty("activeWorkflows", out var workflows))
        {
            if (workflows.ValueKind != JsonValueKind.Array || workflows.GetArrayLength() > 3)
                return null;
            activeWorkflows = new string[workflows.GetArrayLength()];
            for (var index = 0; index < activeWorkflows.Length; index++)
            {
                var workflow = workflows[index];
                if (workflow.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(workflow.GetString()))
                    return null;
                activeWorkflows[index] = workflow.GetString()!;
            }
        }
        return new CreateSessionRequest(workspaceRoot, title, mode, planEnabled, activeWorkflows);
    }

    private static bool TryReadOptionalString(JsonElement parent, string name, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return true;
    }

    private static bool TryMapMode(string? mode, out PermissionMode permissionMode)
    {
        switch (mode)
        {
            case null:
            case "":
            case "build":
                permissionMode = PermissionMode.Build;
                return true;
            case "edit":
                permissionMode = PermissionMode.Edit;
                return true;
            case "yolo":
                permissionMode = PermissionMode.Yolo;
                return true;
            default:
                permissionMode = PermissionMode.Build;
                return false;
        }
    }

    private static string ResolveDeviceName(string? requested, string userAgent)
    {
        var source = (!string.IsNullOrWhiteSpace(requested) ? requested : userAgent).Trim();
        if (source.Length == 0)
            return "Device";
        return source.Length <= 120 ? source : source[..120];
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string DiscriminatorOf(ConversationCommand command) =>
        CommandDiscriminators.TryGetValue(command.GetType(), out var discriminator)
            ? discriminator
            : command.GetType().Name;

    private sealed record CreateSessionRequest(
        string WorkspaceRoot, string? Title, string? Mode, bool PlanEnabled, IReadOnlyList<string>? ActiveWorkflows);
}
