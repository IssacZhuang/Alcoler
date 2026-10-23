using Alcoler.Kernel.Workflows;
using Xunit;

namespace Alcoler.Kernel.Tests.Workflows;

public class WorkflowRegistryTests
{
    private static WorkflowDefinition Definition(string id, string? systemSection = "section") =>
        new(id, id, id, systemSection);

    [Fact]
    public void BuiltInRegistry_ExposesOrchestrator()
    {
        var orchestrator = WorkflowRegistry.BuiltIn.Find(OrchestratorWorkflow.Id);
        Assert.NotNull(orchestrator);
        Assert.NotNull(orchestrator.SystemSection);
        Assert.Null(orchestrator.FullReminder);
        Assert.Null(orchestrator.SparseReminder);
        Assert.Null(orchestrator.ExitReminder);
        Assert.Contains(OrchestratorWorkflow.Id, WorkflowRegistry.BuiltIn.Definitions.Select(d => d.Id));
    }

    [Fact]
    public void TryNormalize_RejectsUnknownAndOverCap()
    {
        var registry = new WorkflowRegistry(Enumerable.Range(1, 4).Select(i => Definition($"w{i}")));

        Assert.True(registry.TryNormalize(["W1", "w2"], out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(["w1", "w2"], normalized);

        Assert.True(registry.TryNormalize(null, out var empty, out _));
        Assert.Empty(empty);

        Assert.False(registry.TryNormalize(["w1", "missing"], out _, out var unknownError));
        Assert.Contains("unknown workflow", unknownError);

        Assert.False(registry.TryNormalize(["w1", "w2", "w3", "w4"], out _, out var capError));
        Assert.Contains("at most", capError);
    }

    [Fact]
    public void TryNormalize_DedupesCaseInsensitively()
    {
        var registry = new WorkflowRegistry([Definition("w1")]);
        Assert.True(registry.TryNormalize(["W1", " w1 "], out var normalized, out _));
        Assert.Equal(["w1"], normalized);
    }

    [Fact]
    public void ReminderMarker_AttributesRemindersToTheirWorkflow()
    {
        Assert.True(WorkflowRegistry.IsReminderFor(WorkflowRegistry.ReminderMarker("orchestrator") + "\nbody", "orchestrator"));
        Assert.False(WorkflowRegistry.IsReminderFor("plain text without markers", "orchestrator"));
        Assert.False(WorkflowRegistry.IsReminderFor(WorkflowRegistry.ReminderMarker("other"), "orchestrator"));
    }

    [Fact]
    public void BuildSystemSection_JoinsActiveSectionsInRecordOrder()
    {
        var registry = new WorkflowRegistry([Definition("a", "section A"), Definition("b", "section B")]);
        Assert.Equal("section A\n\nsection B", registry.BuildSystemSection(["a", "b"]));
        Assert.Equal("section B", registry.BuildSystemSection(["b"]));
        Assert.Null(registry.BuildSystemSection([]));
        Assert.Null(registry.BuildSystemSection(null));
    }
}
