using System.Text.Json;
using Alcoler.Adapters.Mcp;
using Alcoler.Contracts.Tools;

namespace Alcoler.Workbench;

internal sealed class McpToolSource : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<McpServerManager> _retired = [];
    private McpServerManager? _current;
    private string? _fingerprint;
    private bool _disposed;

    public McpToolSource(IReadOnlyList<WorkbenchMcpServerConfig>? servers) => Update(servers);

    public async ValueTask<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        McpServerManager? manager;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            manager = _current;
        }
        return manager is null ? [] : await manager.GetToolsAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Update(IReadOnlyList<WorkbenchMcpServerConfig>? servers)
    {
        servers ??= [];
        var fingerprint = Fingerprint(servers);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _fingerprint = fingerprint;
            if (_current is not null)
            {
                // Sessions keep the tool instances they captured at creation, so a retired
                // manager must stay alive until shutdown even after its server list changes.
                _retired.Add(_current);
                _current = null;
            }
            if (servers.Count > 0)
            {
                _current = new McpServerManager(
                    [.. servers.Select(server => new McpServerConfig(server.Name, server.Command, server.Args, server.Env))]);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        McpServerManager[] managers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            managers = [.. _retired];
            if (_current is not null)
            {
                managers = [.. managers, _current];
                _current = null;
            }
            _retired.Clear();
        }
        foreach (var manager in managers)
        {
            await manager.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string Fingerprint(IReadOnlyList<WorkbenchMcpServerConfig> servers) =>
        JsonSerializer.Serialize(
            new WorkbenchConfig([], McpServers: servers),
            WorkbenchConfig.JsonOptions);
}
