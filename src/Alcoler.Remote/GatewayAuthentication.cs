using Microsoft.AspNetCore.Http;

namespace Alcoler.Remote;

internal static class GatewayAuthentication
{
    private static readonly object DeviceItemKey = new();
    private static readonly byte[] UnauthorizedBody =
        GatewayJson.Object(static writer => writer.WriteString("error", "unauthorized"));

    public static async Task InvokeAsync(HttpContext context, DeviceRegistry registry, Func<Task> next)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments("/api") ||
            request.Path == "/api/info" ||
            request.Path == "/api/pair")
        {
            await next().ConfigureAwait(false);
            return;
        }
        var token = ExtractToken(request);
        var device = string.IsNullOrEmpty(token) ? null : registry.Authenticate(token);
        if (device is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(UnauthorizedBody).ConfigureAwait(false);
            return;
        }
        context.Items[DeviceItemKey] = device;
        await next().ConfigureAwait(false);
    }

    // Browsers cannot set headers on a WebSocket handshake, so the token may also
    // arrive as a query parameter.
    private static string? ExtractToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header["Bearer ".Length..].Trim();
            if (token.Length > 0)
                return token;
        }
        return request.Query.TryGetValue("token", out var query) ? query.ToString() : null;
    }

    public static DeviceRegistry.DeviceRecord? Device(HttpContext context) =>
        context.Items.TryGetValue(DeviceItemKey, out var value) ? value as DeviceRegistry.DeviceRecord : null;
}
