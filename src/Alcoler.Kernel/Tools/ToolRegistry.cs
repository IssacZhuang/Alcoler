using System.Runtime.InteropServices;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

public sealed class ToolRegistry
{
    private static readonly OSPlatform CurrentPlatform = DetectPlatform();
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
        Tools.Where(static tool => tool.Metadata.ProviderVisible && IsAvailableOnCurrentPlatform(tool))
            .Select(static tool => tool.Contract)
            .ToArray();

    private sealed record ContractsCache(ModelProperties Properties, IReadOnlyList<ToolContract> Contracts);

    private ContractsCache? _cachedContracts;

    // The tool set is fixed at construction; the properties instance comes from cached model
    // descriptors, so reference identity is a sufficient cache key.
    public IReadOnlyList<ToolContract> VisibleContractsFor(ModelProperties? properties)
    {
        var cached = Volatile.Read(ref _cachedContracts);
        if (properties is not null && cached is not null && ReferenceEquals(properties, cached.Properties))
        {
            return cached.Contracts;
        }
        var contracts = Tools.Where(static tool => tool.Metadata.ProviderVisible && IsAvailableOnCurrentPlatform(tool))
            .Where(tool => properties is null || tool is not IModelGatedTool gated || gated.IsAvailable(properties))
            .Select(static tool => tool.Contract)
            .ToArray();
        if (properties is not null)
        {
            Volatile.Write(ref _cachedContracts, new ContractsCache(properties, contracts));
        }
        return contracts;
    }

    public ITool? TryFind(string name) =>
        _toolsByName.TryGetValue(CanonicalKey(name), out var tool) ? tool : null;

    private string CanonicalKey(string name) => _canonicalizeAlias(name.Trim());

    private static bool IsAvailableOnCurrentPlatform(ITool tool) =>
        tool is not IPlatformGatedTool gated || gated.IsAvailable(CurrentPlatform);

    private static OSPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows()) return OSPlatform.Windows;
        if (OperatingSystem.IsMacOS()) return OSPlatform.OSX;
        if (OperatingSystem.IsLinux()) return OSPlatform.Linux;
        if (OperatingSystem.IsFreeBSD()) return OSPlatform.FreeBSD;
        return OSPlatform.Create("Unknown");
    }
}
