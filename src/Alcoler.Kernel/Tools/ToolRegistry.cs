using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _toolsByName;
    private readonly Func<string, string> _canonicalizeAlias;

    public ToolRegistry(IReadOnlyList<ITool> tools, Func<string, string>? aliasCanonicalizer = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        Tools = tools;
        _canonicalizeAlias = aliasCanonicalizer ?? (static name =>
            string.Equals(name, "Task", StringComparison.OrdinalIgnoreCase) ? "Agent" : name);
        _toolsByName = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools)
        {
            var key = CanonicalKey(tool.Contract.Name);
            if (_toolsByName.ContainsKey(key))
                throw new InvalidOperationException($"Tool name or alias '{key}' is registered more than once.");
            _toolsByName[key] = tool;
        }
    }

    public IReadOnlyList<ITool> Tools { get; }

    public IReadOnlyList<ToolContract> VisibleContracts =>
        Tools.Where(static tool => tool.Metadata.ProviderVisible)
            .Select(static tool => tool.Contract)
            .ToArray();

    private ModelProperties? _cachedContractsProperties;
    private IReadOnlyList<ToolContract>? _cachedContractsFor;

    // The tool set is fixed at construction; the properties instance comes from cached model
    // descriptors, so reference identity is a sufficient cache key.
    public IReadOnlyList<ToolContract> VisibleContractsFor(ModelProperties? properties)
    {
        if (properties is not null
            && ReferenceEquals(properties, Volatile.Read(ref _cachedContractsProperties))
            && Volatile.Read(ref _cachedContractsFor) is { } cached)
        {
            return cached;
        }
        var contracts = Tools.Where(static tool => tool.Metadata.ProviderVisible)
            .Where(tool => properties is null || tool is not IModelGatedTool gated || gated.IsAvailable(properties))
            .Select(static tool => tool.Contract)
            .ToArray();
        if (properties is not null)
        {
            Volatile.Write(ref _cachedContractsFor, contracts);
            Volatile.Write(ref _cachedContractsProperties, properties);
        }
        return contracts;
    }

    public ITool? TryFind(string name) =>
        _toolsByName.TryGetValue(CanonicalKey(name), out var tool) ? tool : null;

    private string CanonicalKey(string name) => _canonicalizeAlias(name.Trim());
}
