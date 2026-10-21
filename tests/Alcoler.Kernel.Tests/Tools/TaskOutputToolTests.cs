using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class TaskOutputToolTests
{
    [Fact]
    public async Task UnknownTask_ReportsNotFound()
    {
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new TaskOutputTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, """{"task_id": "bg_missing", "block": false}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("No task found with ID: bg_missing", result.SingleText());
    }

    [Fact]
    public async Task MissingTaskId_IsRejected()
    {
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new TaskOutputTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, """{"task_id": "", "block": false}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Task ID is required", result.SingleText());
    }

    [Fact]
    public async Task Block_True_WaitsForCompletion_AndReturnsOutput()
    {
        using var workspace = new TempWorkspace();
        var release = new TaskCompletionSource();
        var outputPath = Path.Combine(workspace.Root, "job.log");
        await File.WriteAllTextAsync(outputPath, "partial line\n");
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var started = jobs.Start(
            "long running command",
            outputPath,
            async ct =>
            {
                await release.Task.WaitAsync(ct);
                await File.AppendAllTextAsync(outputPath, "final line\n");
                return new BackgroundJobResult(0);
            });
        var tool = new TaskOutputTool();
        var query = tool.ExecuteAsync(
            Context(workspace, jobs, $$"""{"task_id": "{{started.Id.Value}}", "block": true, "timeout": 10000}"""),
            CancellationToken.None);

        await Task.Delay(300);
        Assert.False(query.IsCompleted);
        release.SetResult();
        var result = await query;

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.StartsWith("<retrieval_status>success</retrieval_status>", text);
        Assert.Contains("<task_id>" + started.Id.Value + "</task_id>", text);
        Assert.Contains("<task_type>local_bash</task_type>", text);
        Assert.Contains("<status>completed</status>", text);
        Assert.Contains("<exit_code>0</exit_code>", text);
        Assert.Contains("partial line", text);
        Assert.Contains("final line", text);
        Assert.Contains("<output>", text);
    }

    [Fact]
    public async Task Block_False_ReturnsNotReadyWhileRunning()
    {
        using var workspace = new TempWorkspace();
        var release = new TaskCompletionSource();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var started = jobs.Start("busy", null, ct => release.Task.WaitAsync(ct).ContinueWith(_ => new BackgroundJobResult(0)));
        var tool = new TaskOutputTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, $$"""{"task_id": "{{started.Id.Value}}", "block": false}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("<retrieval_status>not_ready</retrieval_status>", result.SingleText());
        Assert.Contains("<status>running</status>", result.SingleText());
        Assert.DoesNotContain("<exit_code>", result.SingleText());

        release.SetResult();
    }

    [Fact]
    public async Task Block_True_TimesOutWhileRunning()
    {
        using var workspace = new TempWorkspace();
        var release = new TaskCompletionSource();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var started = jobs.Start("busy", null, ct => release.Task.WaitAsync(ct).ContinueWith(_ => new BackgroundJobResult(0)));
        var tool = new TaskOutputTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, $$"""{"task_id": "{{started.Id.Value}}", "block": true, "timeout": 250}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("<retrieval_status>timeout</retrieval_status>", result.SingleText());
        Assert.Contains("<status>running</status>", result.SingleText());

        release.SetResult();
    }

    [Fact]
    public async Task OversizedOutput_TruncatesTail_WithFullPathPointer()
    {
        using var workspace = new TempWorkspace();
        var outputPath = Path.Combine(workspace.Root, "big.log");
        var head = new string('h', 45_000);
        var tail = new string('t', 40_000);
        await File.WriteAllTextAsync(outputPath, head + tail);
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var started = jobs.Start("big output", outputPath, _ => Task.FromResult(new BackgroundJobResult(0)));
        await WaitForStatusAsync(jobs, started.Id, BackgroundJobStatus.Completed);
        var tool = new TaskOutputTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, $$"""{"task_id": "{{started.Id.Value}}", "block": true}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.Contains($"[Truncated. Full output: {outputPath}]", text);
        Assert.True(text.Length <= 32_000 + "<output>\n\n</output>".Length + 200);
        Assert.DoesNotContain("hhhh", text);
        Assert.Contains("tttt", text);
    }

    [Fact]
    public async Task FailedJob_IncludesErrorBlock()
    {
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var started = jobs.Start(
            "broken",
            null,
            _ => Task.FromException<BackgroundJobResult>(new InvalidOperationException("command not found")));
        await WaitForStatusAsync(jobs, started.Id, BackgroundJobStatus.Failed);
        var tool = new TaskOutputTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, $$"""{"task_id": "{{started.Id.Value}}"}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.Contains("<status>failed</status>", text);
        Assert.DoesNotContain("<exit_code>", text);
        Assert.Contains("<error>command not found</error>", text);
    }

    [Fact]
    public async Task CompletedJob_WithNonZeroExit_KeepsExitCodeAndError()
    {
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var started = jobs.Start(
            "grep miss",
            null,
            _ => Task.FromResult(new BackgroundJobResult(127, "command not found")));
        await WaitForStatusAsync(jobs, started.Id, BackgroundJobStatus.Completed);
        var tool = new TaskOutputTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, $$"""{"task_id": "{{started.Id.Value}}"}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.Contains("<status>completed</status>", text);
        Assert.Contains("<exit_code>127</exit_code>", text);
        Assert.Contains("<error>command not found</error>", text);
    }

    [Fact]
    public void DefaultTimeoutMetadata_IsUnset_SoPollingOwnsTheDeadline()
    {
        Assert.Null(new TaskOutputTool().Metadata.DefaultTimeoutMs);
    }

    [Fact]
    public void Contract_DescriptionCarriesDeprecatedPreamble_AndAllParamsRequired()
    {
        var contract = new TaskOutputTool().Contract;

        Assert.Equal("TaskOutput", contract.Name);
        Assert.StartsWith("DEPRECATED: Background tasks return their output file path in the tool result", contract.Description);
        Assert.Contains("Task IDs can be found using the /tasks command", contract.Description);

        var schema = contract.InputSchema.GetRawText();
        Assert.Contains("\"required\": [\"task_id\", \"block\", \"timeout\"]", schema);
        Assert.Contains("\"default\": true", schema);
        Assert.Contains("\"default\": 30000", schema);
    }

    [Fact]
    public void CompletionNotification_UsesSystemNotificationWrapper()
    {
        var job = new BackgroundJobInfo(
            new BackgroundJobId("bg_test"),
            "npm test",
            DateTimeOffset.UtcNow,
            BackgroundJobStatus.Completed,
            ExitCode: 0,
            OutputPath: @"C:\ws\.alcoler\background\job.log",
            ToolUseId: new ToolCallId("call_bash"));
        var notification = BackgroundJobNotifications.FormatCompleted(job);

        Assert.StartsWith("[SYSTEM NOTIFICATION - NOT USER INPUT]", notification);
        Assert.Contains("NOT a message from the user", notification);
        Assert.Contains("<task-notification>", notification);
        Assert.Contains("<task-id>bg_test</task-id>\n<tool-use-id>call_bash</tool-use-id>", notification);
        Assert.Contains("<output-file>C:\\ws\\.alcoler\\background\\job.log</output-file>", notification);
        Assert.Contains("<status>completed</status>", notification);
        Assert.Contains("Background command \"npm test\" completed (exit code 0)", notification);
    }

    [Fact]
    public void CompletionNotification_MapsStoppedAndFailedStates()
    {
        var stopped = BackgroundJobNotifications.FormatCompleted(new BackgroundJobInfo(
            new BackgroundJobId("bg_s"), "watch", DateTimeOffset.UtcNow, BackgroundJobStatus.Stopped));
        Assert.Contains("<status>killed</status>", stopped);
        Assert.Contains("Background command \"watch\" was stopped", stopped);

        var failed = BackgroundJobNotifications.FormatCompleted(new BackgroundJobInfo(
            new BackgroundJobId("bg_f"), "watch", DateTimeOffset.UtcNow, BackgroundJobStatus.Failed, ExitCode: 2));
        Assert.Contains("<status>failed</status>", failed);
        Assert.Contains("Background command \"watch\" failed with exit code 2", failed);
    }

    [Fact]
    public void CompletionNotification_WithoutExitCode_KeepsFailureBare_AndDescribesLostState()
    {
        var failed = BackgroundJobNotifications.FormatCompleted(new BackgroundJobInfo(
            new BackgroundJobId("bg_f"), "watch", DateTimeOffset.UtcNow, BackgroundJobStatus.Failed,
            Error: "provider secret"));
        Assert.Contains("<summary>Background command \"watch\" failed</summary>", failed);
        Assert.DoesNotContain("provider secret", failed);

        var lost = BackgroundJobNotifications.FormatCompleted(new BackgroundJobInfo(
            new BackgroundJobId("bg_l"), "watch", DateTimeOffset.UtcNow, BackgroundJobStatus.Lost));
        Assert.Contains("<status>failed</status>", lost);
        Assert.Contains("failed because its in-process state was lost", lost);
    }

    [Fact]
    public void LocalAgentNotification_CorrelatesToolUse_EscapesXml_AndCapsBody()
    {
        var notification = BackgroundJobNotifications.FormatLocalAgentCompleted(
            "sub_a", "completed", "Explore", "find & review", "a < b", toolUses: 2, durationMs: 10,
            toolUseId: "call_\"<&");
        Assert.Contains("<tool-use-id>call_&quot;&lt;&amp;</tool-use-id>", notification);
        Assert.Contains("<result>a &lt; b</result>", notification);
        Assert.Contains("<usage><tool_uses>2</tool_uses><duration_ms>10</duration_ms></usage>", notification);

        var oversized = BackgroundJobNotifications.FormatLocalAgentCompleted(
            "sub_a", "completed", "Explore", "find", new string('x', 130_000));
        Assert.EndsWith("\n[truncated]", oversized);
        Assert.Equal(120_000 + "\n[truncated]".Length,
            oversized.Length - oversized.IndexOf("<task-notification>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackgroundJobService_PreservesToolUseId_ThroughCompletion()
    {
        var notified = new TaskCompletionSource<BackgroundJobInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = new BackgroundJobService(TimeProvider.System, info =>
        {
            notified.TrySetResult(info);
            return ValueTask.CompletedTask;
        });
        var toolUseId = new ToolCallId("call_background");
        var started = jobs.Start("command", null, _ => Task.FromResult(new BackgroundJobResult(0)), toolUseId);
        var completed = await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(toolUseId, started.ToolUseId);
        Assert.Equal(toolUseId, completed.ToolUseId);
        Assert.Equal(toolUseId, jobs.Find(started.Id)!.ToolUseId);
    }

    private static ToolExecutionContext Context(TempWorkspace workspace, BackgroundJobService jobs, string json) =>
        new(
            new SessionId("sess_test"),
            workspace.Root,
            JsonDocument.Parse(json).RootElement.Clone(),
            workspace.Workspace,
            TimeProvider.System,
            BackgroundJobs: jobs);

    private static async Task WaitForStatusAsync(BackgroundJobService jobs, BackgroundJobId id, BackgroundJobStatus status)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (jobs.Find(id)?.Status != status)
        {
            if (deadline.IsCompleted)
                throw new TimeoutException($"job never reached {status}");
            await Task.Delay(20);
        }
    }
}
