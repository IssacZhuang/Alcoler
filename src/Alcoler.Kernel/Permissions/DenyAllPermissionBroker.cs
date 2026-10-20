using Alcoler.Contracts.Permissions;

namespace Alcoler.Kernel.Permissions;

public sealed class DenyAllPermissionBroker : IPermissionBroker
{
    public static DenyAllPermissionBroker Instance { get; } = new();

    private DenyAllPermissionBroker()
    {
    }

    public ValueTask<PermissionDecision> DecideAsync(PermissionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(PermissionDecision.Deny);
}
