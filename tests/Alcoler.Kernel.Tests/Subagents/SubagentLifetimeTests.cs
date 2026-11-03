using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tests.EndToEnd;
using Alcoler.Kernel.Tests.Models;
using Alcoler.Kernel.Tests.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Subagents;

public class SubagentLifetimeTests
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromHours(1);

    [Fact]
    public async Task TerminalTransitions_TrimBurstWithoutAnotherRegistration_AndNeverEvictRunningChildren()
    {
        await using var fixture = new Fixture(new ControlledModel { HoldFirstRuns = true });
        var parent = await fixture.CreateParentAsync();
        var runs = Enumerable.Range(0, 24).Select(_ => fixture.StartAsync(parent)).ToArray();
        await AsyncTestWait.UntilAsync(() => fixture.Agent.List().Count == runs.Length
            && fixture.Model.PendingCount == runs.Length);
        var children = fixture.Agent.List().ToArray();
        Assert.All(children, child => Assert.NotNull(fixture.Messenger.Find(parent, child.Id.Value)));

        foreach (var child in children.Take(20))
            fixture.Model.Release(child.ChildSessionId);
        await AsyncTestWait.UntilAsync(() => fixture.Agent.List().Count(child => child.Status != SubagentStatus.Running) == 20
            && children.Count(child => fixture.Messenger.Find(parent, child.Id.Value) is not null) == 20);
        Assert.All(children.Skip(20), child => Assert.NotNull(fixture.Messenger.Find(parent, child.Id.Value)));
        foreach (var child in children.Skip(20))
            fixture.Model.Release(child.ChildSessionId);
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10));
        await AsyncTestWait.UntilAsync(() => children.Count(child => fixture.Messenger.Find(parent, child.Id.Value) is not null) == 16);
        Assert.All(children, child => Assert.Equal(
            fixture.Messenger.Find(parent, child.Id.Value), fixture.Messenger.Find(parent, child.ChildSessionId.Value)));
    }

    [Fact]
    public async Task RegistryStopTarget_HasOnlyWeakRuntimeAndSessionId_AndEvictedDisposedChildrenCollect()
    {
        await using var fixture = new Fixture(new ControlledModel { HoldFirstRuns = true });
        var parent = await fixture.CreateParentAsync();
        var evicted = await CompleteBurstAndDropObserversAsync(fixture, parent);
        Assert.Equal(8, evicted.Length);
        Assert.All(evicted, item => AssertWeakStopShape(StopDelegate(fixture.Agent, item.Id)));
        await AssertCollectedAsync(evicted.Select(item => item.Reference).ToArray());
        Assert.Equal(24, fixture.Agent.List().Count);
        Assert.NotNull(fixture.Runtime.GetSession(parent));
        GC.KeepAlive(fixture);
    }

    [Fact]
    public async Task ParentUnload_ReleasesHeavyGraphs_ColdResumesSameChild_AndStopStillWorks()
    {
        await using var fixture = new Fixture(new ControlledModel { HoldResumedRuns = true });
        var parent = await fixture.CreateParentAsync();
        Assert.False((await fixture.StartAsync(parent)).IsError);
        var task = Assert.Single(fixture.Agent.List());
        var stop = StopDelegate(fixture.Agent, task.Id);
        var firstCompletion = fixture.Agent.TryGetCompletion(task.Id.Value)!;
        var references = await UnloadAndDropRuntimesAsync(fixture, parent, task);
        var channel = Assert.IsType<SubagentChannel>(fixture.Messenger.Find(parent, task.Id.Value));
        Assert.Null(channel.ResidentRuntime);
        Assert.Null(channel.ResumeAsync);
        Assert.Null(channel.ReadStatus);
        AssertLightColdTarget(channel.ColdLoadAsync!.Target!);
        await AssertCollectedAsync(references.Child);
        await AssertCollectedAsync(references.Parent);

        await fixture.Runtime.ResumeSessionAsync(parent, CancellationToken.None);
        using var parentProjection = fixture.Runtime.Subscribe(parent);
        var delivered = await fixture.SendAsync(parent, task.Id, "cold followup");
        Assert.False(delivered.IsError);
        Assert.Contains("resumed it in the background", delivered.SingleText());
        await AsyncTestWait.UntilAsync(() => fixture.Model.IsPending(task.ChildSessionId));
        Assert.NotNull(channel.ResidentRuntime);
        Assert.Same(stop.Target, StopDelegate(fixture.Agent, task.Id).Target);
        AssertWeakStopShape(stop);
        Assert.NotSame(firstCompletion, fixture.Agent.TryGetCompletion(task.Id.Value));
        Assert.False(fixture.Agent.TryGetCompletion(task.Id.Value)!.IsCompleted);
        using (var childProjection = channel.Runtime.Subscribe(task.ChildSessionId))
        {
            Assert.Contains(childProjection.Snapshot().Rows.OfType<UserMessageRow>(), row => row.Text == "initial work");
            Assert.Contains(childProjection.Snapshot().Rows.OfType<UserMessageRow>(), row => row.Text == "cold followup");
        }
        var output = new TaskOutputTool().ExecuteAsync(fixture.Context(parent,
            new { task_id = task.Id.Value, block = true, timeout = 600000 }, fixture.Agent), CancellationToken.None).AsTask();
        Assert.False(output.IsCompleted);
        Assert.True(await fixture.Agent.TryStopAsync(task.Id.Value, CancellationToken.None));
        var result = await output.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("<retrieval_status>success</retrieval_status>", result.SingleText());
        Assert.Contains("<status>stopped</status>", result.SingleText());
        await AsyncTestWait.UntilAsync(() => parentProjection.Snapshot().Rows.OfType<SystemNoticeRow>()
            .Any(row => row.Subagent is { } state && state.Id == task.Id && state.Status == SubagentStatus.Cancelled));
        using var observer = fixture.Runtime.TryObserveSubagent(parent, task.ChildSessionId);
        Assert.NotNull(observer);
        GC.KeepAlive(fixture);
    }

    [Fact]
    public async Task CompletionRecency_RetainsRecentlyResumedChildInsteadOfItsOriginalRegistrationOrder()
    {
        await using var fixture = new Fixture(new ControlledModel(), completedCapacity: 2);
        var parent = await fixture.CreateParentAsync();
        await fixture.StartAsync(parent);
        var first = Assert.Single(fixture.Agent.List());
        await fixture.StartAsync(parent);
        var second = fixture.Agent.List().Single(task => task.Id != first.Id);
        Assert.False((await fixture.SendAsync(parent, first.Id, "run again")).IsError);
        await AsyncTestWait.UntilAsync(() => fixture.Agent.Find(first.Id.Value)!.Status == SubagentStatus.Completed
            && fixture.Model.Runs(first.ChildSessionId) == 2);
        await fixture.StartAsync(parent);
        await AsyncTestWait.UntilAsync(() => fixture.Messenger.Find(parent, second.Id.Value) is null);
        Assert.NotNull(fixture.Messenger.Find(parent, first.Id.Value));
    }

    [Fact]
    public async Task ConcurrentUnloadSendAndParentResume_SerializeWithoutDeadlockOrUsingDisposedChild()
    {
        await using var fixture = new Fixture(new ControlledModel { HoldResumedRuns = true });
        var parent = await fixture.CreateParentAsync();
        await fixture.StartAsync(parent);
        var task = Assert.Single(fixture.Agent.List());
        var channel = fixture.Messenger.Find(parent, task.Id.Value)!;
        var original = channel.Runtime;
        fixture.Clock.Advance(IdleThreshold + TimeSpan.FromMinutes(1));
        await channel.DeliveryLock.WaitAsync();
        Task<int> unload;
        Task<ToolExecutionResult> send;
        Task<SessionHandle> resume;
        try
        {
            unload = fixture.Runtime.UnloadIdleSessionsAsync(IdleThreshold, null, CancellationToken.None).AsTask();
            await AsyncTestWait.UntilAsync(() => !fixture.Messenger.CanDeliver(parent, channel));
            send = fixture.SendAsync(parent, task.Id, "racing followup");
            resume = fixture.Runtime.ResumeSessionAsync(parent, CancellationToken.None).AsTask();
            Assert.False(unload.IsCompleted);
            Assert.False(send.IsCompleted);
            Assert.False(resume.IsCompleted);
        }
        finally
        {
            channel.DeliveryLock.Release();
        }
        Assert.Equal(1, await unload.WaitAsync(TimeSpan.FromSeconds(10)));
        await resume.WaitAsync(TimeSpan.FromSeconds(10));
        var delivery = await send.WaitAsync(TimeSpan.FromSeconds(10));
        if (delivery.IsError)
            Assert.False((await fixture.SendAsync(parent, task.Id, "retry after resume")).IsError);
        await AsyncTestWait.UntilAsync(() => fixture.Model.IsPending(task.ChildSessionId));
        Assert.NotSame(original, channel.Runtime);
        Assert.Equal(2, fixture.Model.Runs(task.ChildSessionId));
        Assert.True(await fixture.Agent.TryStopAsync(task.Id.Value, CancellationToken.None));
        await AsyncTestWait.UntilAsync(() => fixture.Agent.Find(task.Id.Value)!.Status == SubagentStatus.Cancelled);
    }

    [Fact]
    public async Task InFlightResume_MakesUnloadRecheckSafetyAfterDeliveryLock()
    {
        await using var fixture = new Fixture(new ControlledModel { HoldResumedRuns = true });
        var parent = await fixture.CreateParentAsync();
        await fixture.StartAsync(parent);
        var task = Assert.Single(fixture.Agent.List());
        var channel = fixture.Messenger.Find(parent, task.Id.Value)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = channel.ResumeAsync!;
        channel.ResumeAsync = async (prompt, summary, callId, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            await resume(prompt, summary, callId, token);
        };
        var send = fixture.SendAsync(parent, task.Id, "in flight");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(IdleThreshold + TimeSpan.FromMinutes(1));
        var unload = fixture.Runtime.UnloadIdleSessionsAsync(IdleThreshold, null, CancellationToken.None).AsTask();
        try
        {
            await AsyncTestWait.UntilAsync(() => !fixture.Messenger.CanDeliver(parent, channel));
            Assert.False(unload.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.False((await send.WaitAsync(TimeSpan.FromSeconds(10))).IsError);
        Assert.Equal(0, await unload.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(fixture.Runtime.GetSession(parent));
        Assert.True(fixture.Messenger.CanDeliver(parent, channel));
        Assert.Equal(SubagentStatus.Running, fixture.Agent.Find(task.Id.Value)!.Status);
        Assert.True(await fixture.Agent.TryStopAsync(task.Id.Value, CancellationToken.None));
        await AsyncTestWait.UntilAsync(() => fixture.Agent.Find(task.Id.Value)!.Status == SubagentStatus.Cancelled);
    }

    [Fact]
    public async Task ArchivedParentDeletion_RemovesColdMetadataAndBothChannelAliases()
    {
        await using var fixture = new Fixture(new ControlledModel());
        var parent = await fixture.CreateParentAsync();
        await fixture.StartAsync(parent);
        var child = Assert.Single(fixture.Agent.List());
        await fixture.Runtime.SetSessionArchivedAsync(parent, true, CancellationToken.None);
        fixture.Clock.Advance(IdleThreshold + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await fixture.Runtime.UnloadIdleSessionsAsync(IdleThreshold, null, CancellationToken.None));
        Assert.NotNull(fixture.Messenger.Find(parent, child.Id.Value));
        await fixture.Runtime.DeleteArchivedSessionsAsync(CancellationToken.None);
        Assert.Null(fixture.Messenger.Find(parent, child.Id.Value));
        Assert.Null(fixture.Messenger.Find(parent, child.ChildSessionId.Value));
    }

    [Fact]
    public async Task RuntimeDisposal_DetachesWarmChildrenAndRemovesParentBook()
    {
        await using var fixture = new Fixture(new ControlledModel());
        var parent = await fixture.CreateParentAsync();
        await fixture.StartAsync(parent);
        var child = Assert.Single(fixture.Agent.List());
        var channel = fixture.Messenger.Find(parent, child.Id.Value)!;
        await fixture.Runtime.DisposeAsync();
        Assert.Null(channel.ResidentRuntime);
        Assert.Null(channel.ResumeAsync);
        Assert.Null(fixture.Messenger.Find(parent, child.Id.Value));
        Assert.Null(fixture.Messenger.Find(parent, child.ChildSessionId.Value));
    }

    [Fact]
    public async Task RuntimeDisposal_CancelsRunningChildAndCompletesTaskOutput()
    {
        await using var fixture = new Fixture(new ControlledModel { HoldFirstRuns = true });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var parent = await fixture.CreateParentAsync();
        var run = fixture.StartAsync(parent);
        await AsyncTestWait.UntilAsync(() => fixture.Model.PendingCount == 1);
        var child = Assert.Single(fixture.Agent.List());
        var completion = fixture.Agent.TryGetCompletion(child.Id.Value)!;
        var output = new TaskOutputTool().ExecuteAsync(fixture.Context(parent,
            new { task_id = child.Id.Value, block = true, timeout = 600000 }, fixture.Agent), cancellation.Token).AsTask();
        Assert.False(output.IsCompleted);
        await fixture.Runtime.DisposeAsync().AsTask().WaitAsync(cancellation.Token);
        await run.WaitAsync(cancellation.Token);
        var result = await output.WaitAsync(cancellation.Token);
        Assert.Contains("<retrieval_status>success</retrieval_status>", result.SingleText());
        Assert.Contains("<status>stopped</status>", result.SingleText());
        Assert.Equal(SubagentStatus.Cancelled, fixture.Agent.Find(child.Id.Value)!.Status);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Null(fixture.Messenger.Find(parent, child.Id.Value));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(SubagentId Id, WeakReference<AgentRuntime> Reference)[]> CompleteBurstAndDropObserversAsync(
        Fixture fixture, SessionId parent)
    {
        var runs = Enumerable.Range(0, 24).Select(_ => fixture.StartAsync(parent)).ToArray();
        await AsyncTestWait.UntilAsync(() => fixture.Model.PendingCount == runs.Length);
        var children = fixture.Agent.List().ToArray();
        var runtimes = children.Select(child => (Task: child, Runtime: fixture.Messenger.Find(parent, child.Id.Value)!.Runtime)).ToArray();
        foreach (var child in children)
        {
            using var observer = fixture.Runtime.TryObserveSubagent(parent, child.ChildSessionId);
            Assert.NotNull(observer);
            fixture.Model.Release(child.ChildSessionId);
        }
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10));
        await AsyncTestWait.UntilAsync(() => children.Count(child => fixture.Messenger.Find(parent, child.Id.Value) is not null) == 16);
        var evicted = runtimes.Where(item => fixture.Messenger.Find(parent, item.Task.Id.Value) is null).ToArray();
        foreach (var item in evicted)
        {
            // The scheduled grace timer is an independent temporary root, not the registry root under test.
            await item.Runtime.DisposeAsync();
        }
        return evicted.Select(item => (item.Task.Id, new WeakReference<AgentRuntime>(item.Runtime))).ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<SessionRuntime> Parent, WeakReference<AgentRuntime> Child)> UnloadAndDropRuntimesAsync(
        Fixture fixture, SessionId parent, SubagentTaskInfo task)
    {
        var parentReference = new WeakReference<SessionRuntime>(fixture.Runtime.GetSession(parent)!);
        var childReference = new WeakReference<AgentRuntime>(fixture.Messenger.Find(parent, task.Id.Value)!.Runtime);
        fixture.Clock.Advance(IdleThreshold + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await fixture.Runtime.UnloadIdleSessionsAsync(IdleThreshold, null, CancellationToken.None));
        Assert.Null(fixture.Runtime.GetSession(parent));
        return (parentReference, childReference);
    }

    private static Func<CancellationToken, ValueTask> StopDelegate(SubagentTool agent, SubagentId id)
    {
        var registry = Assert.IsType<SubagentTaskRegistry>(agent.Tasks);
        var entries = typeof(SubagentTaskRegistry).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(registry)!;
        var values = (IEnumerable)entries.GetType().GetProperty("Values")!.GetValue(entries)!;
        foreach (var entry in values)
        {
            var info = (SubagentTaskInfo)entry!.GetType().GetMethod("Snapshot")!.Invoke(entry, null)!;
            if (info.Id == id)
                return (Func<CancellationToken, ValueTask>)entry!.GetType().GetProperty("StopAsync")!.GetValue(entry)!;
        }
        throw new InvalidOperationException("Subagent entry was not registered.");
    }

    private static void AssertWeakStopShape(Delegate stop)
    {
        var target = Assert.IsAssignableFrom<object>(stop.Target);
        var fields = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Equal(2, fields.Length);
        Assert.Single(fields, field => field.FieldType == typeof(WeakReference<AgentRuntime>));
        Assert.Single(fields, field => field.FieldType == typeof(SessionId));
    }

    private static void AssertLightColdTarget(object target)
    {
        var fields = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Contains(fields, field => field.FieldType == typeof(WeakReference<AgentRuntime>));
        Assert.DoesNotContain(fields, field => typeof(AgentRuntime).IsAssignableFrom(field.FieldType)
            || typeof(SessionRuntime).IsAssignableFrom(field.FieldType)
            || typeof(Delegate).IsAssignableFrom(field.FieldType)
            || field.FieldType == typeof(AgentRuntimeOptions)
            || field.FieldType == typeof(SubagentChannel));
    }

    private static async Task AssertCollectedAsync<T>(params WeakReference<T>[] references) where T : class
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (AllCollected(references))
                return;
            await Task.Delay(25);
        }
        Assert.True(AllCollected(references), "Unloaded runtime graph remained rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AllCollected<T>(WeakReference<T>[] references) where T : class =>
        references.All(reference => !reference.TryGetTarget(out _));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestHarness _harness = TestHarness.Create();
        public ManualTimeProvider Clock { get; } = new();
        public ControlledModel Model { get; }
        public SubagentMessenger Messenger { get; }
        public SubagentTool Agent { get; }
        public AgentRuntime Runtime { get; }

        public Fixture(ControlledModel model, int completedCapacity = 16)
        {
            Model = model;
            Messenger = new SubagentMessenger(completedCapacity);
            AgentRuntime? runtime = null;
            Agent = SubagentWiring.CreateTool(() => runtime ?? throw new InvalidOperationException("Runtime is not initialized."), Messenger);
            Runtime = TestHarness.BuildRuntimeFor(model, _harness.Root, transform: options => options with
            {
                Clock = Clock,
                Tools = [.. options.Tools.Where(tool => tool.Contract.Name is not ("Agent" or "SendMessage")),
                    Agent, new SendMessageTool(Messenger)],
            });
            runtime = Runtime;
        }

        public async Task<SessionId> CreateParentAsync() =>
            (await Runtime.CreateSessionAsync(new SessionOptions(_harness.Root, PermissionMode: PermissionMode.Yolo),
                CancellationToken.None)).Id;

        public Task<ToolExecutionResult> StartAsync(SessionId parent) => Agent.ExecuteAsync(Context(parent,
            new { prompt = "initial work", description = "lifetime test" }), CancellationToken.None).AsTask();

        public Task<ToolExecutionResult> SendAsync(SessionId parent, SubagentId child, string message) =>
            new SendMessageTool(Messenger).ExecuteAsync(Context(parent,
                new { to = child.Value, summary = "followup", message }), CancellationToken.None).AsTask();

        public ToolExecutionContext Context(SessionId parent, object input, ISubagentTasks? subagents = null) => new(
            parent, _harness.Root, JsonSerializer.SerializeToElement(input), null!, Clock,
            ToolCallId: new ToolCallId("call_" + Guid.NewGuid().ToString("N")), Subagents: subagents);

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await _harness.DisposeAsync();
        }
    }

    private sealed class ControlledModel : IChatModel
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();
        private readonly ConcurrentDictionary<string, int> _runs = new();
        public bool HoldFirstRuns { get; init; }
        public bool HoldResumedRuns { get; init; }
        public int PendingCount => _pending.Count;
        public bool IsPending(SessionId id) => _pending.ContainsKey(id.Value);
        public int Runs(SessionId id) => _runs.GetValueOrDefault(id.Value);
        public ModelDescriptor Descriptor { get; } = new("scripted", "test-model", "Lifecycle model", new ModelProperties(200_000));

        public void Release(SessionId id) => _pending[id.Value].TrySetResult();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var sessionId = request.CacheAffinity!.SessionId;
            var isChild = request.CacheAffinity.ParentSessionId is not null;
            var run = _runs.AddOrUpdate(sessionId, 1, static (_, count) => count + 1);
            if (isChild && ((run == 1 && HoldFirstRuns) || (run > 1 && HoldResumedRuns)))
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[sessionId] = release;
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    _pending.TryRemove(sessionId, out _);
                }
            }
            await Task.Yield();
            yield return new ModelTextDelta(isChild ? $"child run {run}" : "noted");
            yield return new ModelStreamFinished(ModelFinishReason.Stop);
        }
    }
}
