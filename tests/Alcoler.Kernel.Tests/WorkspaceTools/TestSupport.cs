using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Workspace;

namespace Alcoler.Kernel.Tests.WorkspaceTools;

internal sealed class FakeSessionCommands : ISessionCommands
{
    public List<PermissionMode> SetModes { get; } = [];

    public ValueTask SetModeAsync(PermissionMode mode, CancellationToken cancellationToken)
    {
        SetModes.Add(mode);
        return ValueTask.CompletedTask;
    }
}

internal static class WorkspaceToolTestContext
{
    public static ToolExecutionContext ForSession(SessionId sessionId, string json, ISessionCommands? commands = null) =>
        new(
            sessionId,
            @"C:\alcoler-test-workspace",
            JsonDocument.Parse(json).RootElement.Clone(),
            NoArtifactWorkspace.Instance,
            TimeProvider.System,
            commands);
}

internal sealed class NoArtifactWorkspace : IToolWorkspace
{
    public static readonly NoArtifactWorkspace Instance = new();

    public string Root => @"C:\";

    public string ResolvePath(string path) => path;

    public ValueTask<ArtifactReference> SaveArtifactAsync(
        SessionId sessionId,
        string fileName,
        string mimeType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("workspace tool tests never save artifacts");
}
