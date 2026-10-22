using Alcoler.Contracts.Identifiers;

namespace Alcoler.Contracts.Storage;

public sealed record PlanFileReference(string Path, string Content);

public interface IPlanFileStore
{
    ValueTask<PlanFileReference> WriteAsync(SessionId sessionId, string plan, CancellationToken cancellationToken);

    ValueTask<PlanFileReference?> ReadAsync(SessionId sessionId, CancellationToken cancellationToken);
}

public sealed class NullPlanFileStore : IPlanFileStore
{
    public static readonly NullPlanFileStore Instance = new();

    public ValueTask<PlanFileReference> WriteAsync(SessionId sessionId, string plan, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PlanFileReference(string.Empty, plan));

    public ValueTask<PlanFileReference?> ReadAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<PlanFileReference?>(null);
}
