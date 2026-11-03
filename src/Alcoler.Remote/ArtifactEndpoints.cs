using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Alcoler.Remote;

internal static class ArtifactEndpoints
{
    private const string ArtifactSchemePrefix = "alcoler-artifact://";

    public static void Map(WebApplication app, GatewayState state)
    {
        var thumbnails = app.Services.GetRequiredService<ArtifactThumbnailService>();
        app.Lifetime.ApplicationStopping.Register(thumbnails.Dispose);
        app.MapGet("/api/artifacts", async (HttpRequest request, HttpContext context, CancellationToken cancellationToken) =>
        {
            var loader = state.Options.ArtifactLoader;
            if (loader is null)
                return GatewayJson.Error(501, "not_supported", "artifact_loader");
            var uris = request.Query["uri"];
            var uri = uris.ToString();
            if (uris.Count != 1 || !uri.StartsWith(ArtifactSchemePrefix, StringComparison.OrdinalIgnoreCase))
                return GatewayJson.Error(400, "bad_request", "uri");
            if (request.Query.TryGetValue("thumbnail", out var variant))
            {
                if (variant.Count != 1 || variant[0] != "1")
                    return GatewayJson.Error(400, "bad_request", "thumbnail");
                if (request.Query.Keys.Any(key => key is not ("uri" or "mime" or "thumbnail" or "token")))
                    return GatewayJson.Error(400, "bad_request", "query");
                if (!thumbnails.IsConfigured)
                    return GatewayJson.Error(501, "not_supported", "artifact_thumbnail");
                var result = await thumbnails.GetAsync(uri, cancellationToken);
                if (result.Thumbnail is not { } thumbnail)
                {
                    return result.Status switch
                    {
                        ArtifactThumbnailStatus.NotFound => GatewayJson.Error(404, "not_found", "artifact"),
                        ArtifactThumbnailStatus.Unsupported => GatewayJson.Error(415, "unsupported_media_type", "thumbnail"),
                        ArtifactThumbnailStatus.TooLarge => GatewayJson.Error(413, "payload_too_large", "thumbnail"),
                        ArtifactThumbnailStatus.Busy => GatewayJson.Error(429, "rate_limited", "thumbnail"),
                        _ => GatewayJson.Error(500, "internal_error", "thumbnail"),
                    };
                }
                state.Audit.Write("artifact_thumbnail_access", deviceId: GatewayAuthentication.Device(context)?.Id, detail: uri);
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return GatewayJson.Raw(thumbnail.Bytes, thumbnail.MimeType);
            }
            var bytes = await loader(uri, cancellationToken);
            if (bytes is null)
                return GatewayJson.Error(404, "not_found", "artifact");
            state.Audit.Write("artifact_access", deviceId: GatewayAuthentication.Device(context)?.Id, detail: uri);
            var mime = request.Query["mime"].ToString();
            if (mime.Length == 0)
                mime = "application/octet-stream";
            return GatewayJson.Raw(bytes.Value, mime, enableRanges: true);
        });
    }
}
