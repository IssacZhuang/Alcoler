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

    public IReadOnlyList<ToolContract> VisibleContractsFor(ModelProperties? properties) =>
        Tools.Where(static tool => tool.Metadata.ProviderVisible)
            .Where(tool => properties is null || tool is not IModelGatedTool gated || gated.IsAvailable(properties))
            .Select(static tool => tool.Contract)
            .ToArray();

    public ITool? TryFind(string name) =>
        _toolsByName.TryGetValue(CanonicalKey(name), out var tool) ? tool : null;

    private string CanonicalKey(string name) => _canonicalizeAlias(name.Trim());
}
