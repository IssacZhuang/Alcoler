using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Alcoler.Remote;

public sealed class RemoteGateway : IAsyncDisposable
{
    private static readonly byte[] ForbiddenBody = GatewayJson.Object(static writer =>
    {
        writer.WriteString("error", "forbidden");
        writer.WriteString("reason", "non-private-source");
    });
    private static readonly byte[] PlaceholderShell = Encoding.UTF8.GetBytes(
        """<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Alcoler Remote</title></head><body><p>mobile UI not built</p></body></html>""");

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private RemoteGatewayOptions _options;
    private GatewayState? _state;
    private WebApplication? _app;
    private volatile bool _running;
    private IReadOnlyList<string> _boundAddresses = [];

    internal Action<IWebHostBuilder>? WebHostConfiguration { get; set; }

    public RemoteGateway(RemoteGatewayOptions options) => _options = options;

    public bool IsRunning => _running;

    public IReadOnlyList<string> BoundAddresses => _boundAddresses;

    public IReadOnlyList<RemoteDevice> Devices => CurrentState().Devices.List();

    public DateTimeOffset? PairingTokenExpiresAt => CurrentState().PairingTokens.ExpiresAt;

    public string GeneratePairingToken() => CurrentState().PairingTokens.Issue(DateTimeOffset.UtcNow);

    public Task RevokeDeviceAsync(string deviceId)
    {
        var state = CurrentState();
        if (state.Devices.Revoke(deviceId))
            state.Audit.Write("device_revoked", deviceId: deviceId);
        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RestartAsync(RemoteGatewayOptions options)
    {
        await _lifecycle.WaitAsync();
        try
        {
            var wasRunning = _running;
            await StopCoreAsync();
            lock (_stateLock)
            {
                _options = options;
                _state = null;
            }
            if (wasRunning)
                await StartCoreAsync(CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
            return;
        var state = CurrentState();
        var app = BuildApplication(state);
        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        _app = app;
        _running = true;
        // IServerAddressesFeature carries the concrete port when options.Port is 0.
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];
        _boundAddresses = addresses.Length > 0
            ? addresses
            : [$"http://0.0.0.0:{state.Options.Port}"];
        state.Audit.Write("gateway_started", detail: string.Join(",", _boundAddresses));
    }

    private async Task StopCoreAsync()
    {
        var app = _app;
        if (app is null)
            return;
        _app = null;
        _running = false;
        _boundAddresses = [];
        _state?.Audit.Write("gateway_stopped");
        try
        {
            await app.StopAsync();
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private GatewayState CurrentState()
    {
        lock (_stateLock)
        {
            _state ??= GatewayState.Create(_options);
            return _state;
        }
    }

    private static Socket CreateExclusiveListenSocket(EndPoint endpoint)
    {
        if (endpoint is not IPEndPoint ipEndpoint)
            return SocketTransportOptions.CreateDefaultBoundListenSocket(endpoint);
        var socket = new Socket(ipEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Windows dual-stack binds can otherwise succeed while IPv4 is owned by another listener.
            socket.ExclusiveAddressUse = true;
            if (ipEndpoint.Address.Equals(IPAddress.IPv6Any))
                socket.DualMode = true;
            socket.Bind(endpoint);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private WebApplication BuildApplication(GatewayState state)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (OperatingSystem.IsWindows())
            builder.Services.Configure<SocketTransportOptions>(options =>
                options.CreateBoundListenSocket = CreateExclusiveListenSocket);
        builder.WebHost.UseKestrel(server => server.ListenAnyIP(state.Options.Port));
        WebHostConfiguration?.Invoke(builder.WebHost);
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            ApplyCacheHeaders(context);
            await next();
        });
        if (!state.Options.AllowNonPrivateClients)
        {
            app.Use(async (context, next) =>
            {
                if (NetworkPolicy.IsPrivateClient(context.Connection.RemoteIpAddress))
                {
                    await next();
                    return;
                }
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.Body.WriteAsync(ForbiddenBody);
            });
        }
        app.Use((context, next) => GatewayAuthentication.InvokeAsync(context, state.Devices, next));
        app.UseWebSockets();

        MapShell(app);
        GatewayEndpoints.Map(app, state);
        return app;
    }

    private static void ApplyCacheHeaders(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api"))
            context.Response.Headers.CacheControl = "no-store";
        else
            context.Response.Headers.CacheControl = "no-cache";
    }

    private static void MapShell(WebApplication app)
    {
        var assembly = typeof(RemoteGateway).Assembly;
        var shell = new EmbeddedFileProvider(assembly, "Alcoler.Remote");
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
        // Resources are named Alcoler.Remote.wwwroot.<file>, so the static branch needs
        // a provider rooted one namespace deeper. Mounted twice: at /wwwroot and at the
        // root, because the shell references its assets with root-relative URLs.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new EmbeddedFileProvider(assembly, "Alcoler.Remote.wwwroot"),
            RequestPath = "",
            ContentTypeProvider = contentTypes,
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new EmbeddedFileProvider(assembly, "Alcoler.Remote.wwwroot"),
            RequestPath = "/wwwroot",
            ContentTypeProvider = contentTypes,
        });
        app.MapGet("/", () =>
        {
            var index = shell.GetFileInfo("wwwroot/index.html");
            if (!index.Exists)
                return GatewayJson.Raw(PlaceholderShell, "text/html; charset=utf-8");
            using var stream = index.CreateReadStream();
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            return GatewayJson.Raw(buffered.ToArray(), "text/html; charset=utf-8");
        });
    }
}
