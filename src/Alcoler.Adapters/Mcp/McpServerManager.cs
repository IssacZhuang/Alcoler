using Alcoler.Contracts.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcoler.Adapters.Mcp;

public sealed record McpServerConfig(
    string Name,
    string Command,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null);

public enum McpServerState
{
    Starting,
    Running,
    Failed,
}

public sealed record McpServerStatus(string ServerName, McpServerState State, string? Error = null);

public sealed class McpServerManager : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<McpServerConfig, IMcpTransport> _transportFactory;
    private readonly ILogger _logger;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _callTimeout;
    private readonly List<ManagedServer> _servers = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public McpServerManager(IReadOnlyList<McpServerConfig> servers, ILogger? logger = null)
        : this(servers, static config => new StdioMcpTransport(config), logger)
    {
    }

    internal McpServerManager(
        IReadOnlyList<McpServerConfig> servers,
        Func<McpServerConfig, IMcpTransport> transportFactory,
        ILogger? logger = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? callTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(transportFactory);
        _transportFactory = transportFactory;
        _logger = logger ?? NullLogger.Instance;
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        _callTimeout = callTimeout ?? McpTool.DefaultCallTimeout;

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in servers)
        {
            if (string.IsNullOrWhiteSpace(config.Name) || string.IsNullOrWhiteSpace(config.Command))
            {
                _logger.LogWarning("Skipping an MCP server configuration without a name or command.");
                continue;
            }
            if (!seenNames.Add(config.Name))
            {
                _logger.LogWarning("Skipping a duplicate MCP server configuration '{ServerName}'.", config.Name);
                continue;
            }
            _servers.Add(new ManagedServer(config));
        }
    }

    public IReadOnlyList<McpServerStatus> Statuses => _servers.Select(static server => server.Status).ToArray();

    public async Task<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(McpServerManager));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tools = new List<ITool>();
            foreach (var server in _servers)
            {
                if (server.Tools is { } attached)
                {
                    tools.AddRange(attached);
                    continue;
                }
                tools.AddRange(await StartServerAsync(server, cancellationToken).ConfigureAwait(false));
            }
            return tools;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var server in _servers)
                await server.DisposeConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }

    private async Task<IReadOnlyList<ITool>> StartServerAsync(ManagedServer server, CancellationToken cancellationToken)
    {
        server.MarkStarting();
        McpServerConnection? connection = null;
        try
        {
            connection = new McpServerConnection(server.Config.Name, _transportFactory(server.Config), _logger);
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(_startupTimeout);
            await connection.InitializeAsync(startupCts.Token).ConfigureAwait(false);
            var definitions = await connection.ListToolsAsync(startupCts.Token).ConfigureAwait(false);
            var tools = definitions
                .Select(definition => new McpTool(connection, definition, _callTimeout))
                .Cast<ITool>()
                .ToArray();
            server.MarkRunning(connection, tools);
            return tools;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeConnectionQuietlyAsync(connection).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await DisposeConnectionQuietlyAsync(connection).ConfigureAwait(false);
            var error = $"MCP server '{server.Config.Name}' did not start within {_startupTimeout.TotalSeconds:0.#}s.";
            server.MarkFailed(error);
            _logger.LogWarning("MCP server '{ServerName}' failed to start and was skipped: {Error}", server.Config.Name, error);
            return [];
        }
        catch (Exception ex)
        {
            await DisposeConnectionQuietlyAsync(connection).ConfigureAwait(false);
            server.MarkFailed(ex.Message);
            _logger.LogWarning(ex, "MCP server '{ServerName}' failed to start and was skipped.", server.Config.Name);
            return [];
        }
    }

    private static async ValueTask DisposeConnectionQuietlyAsync(McpServerConnection? connection)
    {
        if (connection is null)
            return;
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private sealed class ManagedServer
    {
        private readonly object _sync = new();
        private McpServerStatus _status;
        private McpServerConnection? _connection;
        private IReadOnlyList<ITool>? _tools;

        public ManagedServer(McpServerConfig config)
        {
            Config = config;
            _status = new McpServerStatus(config.Name, McpServerState.Starting);
        }

        public McpServerConfig Config { get; }

        public McpServerStatus Status
        {
            get
            {
                lock (_sync)
                    return _status;
            }
        }

        public IReadOnlyList<ITool>? Tools
        {
            get
            {
                lock (_sync)
                    return _tools;
            }
        }

        public void MarkStarting()
        {
            lock (_sync)
            {
                _status = new McpServerStatus(Config.Name, McpServerState.Starting);
            }
        }

        public void MarkRunning(McpServerConnection connection, IReadOnlyList<ITool> tools)
        {
            lock (_sync)
            {
                _connection = connection;
                _tools = tools;
                _status = new McpServerStatus(Config.Name, McpServerState.Running);
            }
        }

        public void MarkFailed(string error)
        {
            lock (_sync)
            {
                _connection = null;
                _tools = null;
                _status = new McpServerStatus(Config.Name, McpServerState.Failed, error);
            }
        }

        public async ValueTask DisposeConnectionAsync()
        {
            McpServerConnection? connection;
            lock (_sync)
            {
                connection = _connection;
                _connection = null;
                _tools = null;
            }
            if (connection is null)
                return;
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }
}
