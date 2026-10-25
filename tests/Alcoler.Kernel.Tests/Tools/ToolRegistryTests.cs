using System.Runtime.InteropServices;
using System.Text.Json;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class ToolRegistryTests
{
    [Fact]
    public void PlatformGatedTool_IsHiddenWhenUnavailable()
    {
        var registry = new ToolRegistry([new PlatformGatedFakeTool(_ => false)]);

        Assert.Empty(registry.VisibleContracts);
        Assert.Empty(registry.VisibleContractsFor(null));
    }

    [Fact]
    public void PlatformGatedTool_IsVisibleWhenAvailable()
    {
        var registry = new ToolRegistry([new PlatformGatedFakeTool(_ => true)]);

        Assert.Equal("Gated", Assert.Single(registry.VisibleContracts).Name);
        Assert.Equal("Gated", Assert.Single(registry.VisibleContractsFor(null)).Name);
    }

    private sealed class PlatformGatedFakeTool(Func<OSPlatform, bool> isAvailable) : ITool, IPlatformGatedTool
    {
        public ToolContract Contract { get; } =
            new("Gated", "fake gated tool", JsonDocument.Parse(TestSchemas.Permissive).RootElement.Clone());

        public ToolMetadata Metadata { get; } = FakeToolMetadata.ParallelSafe();

        public bool IsAvailable(OSPlatform platform) => isAvailable(platform);

        public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ToolInputIssue?>(null);

        public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult(input);

        public ValueTask<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolExecutionResult.Text("ok"));
    }
}
