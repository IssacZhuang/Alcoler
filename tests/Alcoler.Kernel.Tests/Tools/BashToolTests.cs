using System.Diagnostics;
using System.Text.Json;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class BashToolTests
{
    [Fact]
    public async Task EchoCommand_RoundTripsOutput()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo hello"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("hello", result.SingleText());
    }

    [Fact]
    public async Task NonZeroExitCode_MarksErrorResult_WithExitCodeHeader()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "exit 3"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.StartsWith("Exit code 3", result.SingleText());
    }

    [Fact]
    public async Task SemanticExitCode_GrepNoMatch_IsNotError()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        File.WriteAllText(Path.Combine(workspace.Root, "file.txt"), "line one\nline two\n");
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "grep needle file.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(BashSemantics.NoMatchesFound, result.SingleText());
    }

    [Fact]
    public async Task WorkingDirectory_IsWorkspaceRoot()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo wd123 > wd-probe.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "wd-probe.txt")));
    }

    [Fact]
    public async Task Timeout_KillsProcessTree_AndReportsAbort()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "ping -n 20 127.0.0.1", "timeout": 1000}"""),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.False(result.IsError);
        Assert.Contains("<error>Command was aborted before completion</error>", result.SingleText());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TimeoutParam_IsAccepted_DangerouslyDisableSandbox_IsNoOp()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo sandboxed", "timeout": 30000, "dangerouslyDisableSandbox": true}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("sandboxed", result.SingleText());
    }

    [Fact]
    public void Metadata_RequiresApproval_AndClassifiesReadOnlyInput()
    {
        var tool = new BashTool();
        Assert.True(tool.Metadata.NeedsApproval);

        Assert.True(tool.IsReadOnlyInput(Json("""{"command": "git status"}""")));
        Assert.True(tool.IsReadOnlyInput(Json("""{"command": "cat notes.txt"}""")));
        Assert.False(tool.IsReadOnlyInput(Json("""{"command": "rm -rf build"}""")));
        Assert.False(tool.IsReadOnlyInput(Json("""{"command": "echo nope"}""")));
        Assert.False(tool.IsReadOnlyInput(Json("""{"command": "grep x f > out.txt"}""")));
    }

    [Fact]
    public void ContractSchema_ExposesTimeoutAndDangerouslyDisableSandbox()
    {
        var contract = new BashTool().Contract;

        Assert.Contains($"- `timeout` is in milliseconds: default {BashTool.DefaultTimeoutMs}, max {BashTool.MaxTimeoutMs}.", contract.Description);
        var properties = contract.InputSchema.GetProperty("properties");
        Assert.False(properties.TryGetProperty("timeout_ms", out _));
        var timeout = properties.GetProperty("timeout");
        Assert.Equal($"Optional timeout in milliseconds (max {BashTool.MaxTimeoutMs})", timeout.GetProperty("description").GetString());
        var sandbox = properties.GetProperty("dangerouslyDisableSandbox");
        Assert.Equal("boolean", sandbox.GetProperty("type").GetString());
        Assert.Equal(
            "Set this to true to dangerously override sandbox mode and run commands without sandboxing.",
            sandbox.GetProperty("description").GetString());
    }

    [Fact]
    public async Task PersistentCwd_CapturedWithinWorkspace()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "sub"));
        var shell = new ShellSessionState(workspace.Root);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "cd sub"}""", shell),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            Path.Combine(workspace.Root, "sub"),
            shell.CurrentDirectory,
            ignoreCase: true);

        var followUp = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "pwd"}""", shell),
            CancellationToken.None);
        Assert.EndsWith("/sub", followUp.SingleText().Trim());
    }

    [Fact]
    public async Task CwdLeavingWorkspace_ResetsToRoot_AndAppendsStderrHint()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var shell = new ShellSessionState(workspace.Root);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "cd .."}""", shell),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(workspace.Root, shell.CurrentDirectory, ignoreCase: true);
        Assert.Contains($"Shell cwd was reset to {workspace.Root}", result.SingleText());
    }

    [Fact]
    public async Task OversizedOutput_PersistedWithEnvelope()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "yes 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' | head -n 2000"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.StartsWith("<persisted-output>", text);
        Assert.Contains("Output too large (", text);
        Assert.Contains("). Full output saved to: ", text);
        Assert.Contains("Preview (first 2KB):", text);
        Assert.EndsWith("</persisted-output>", text);
    }

    [Fact]
    public async Task ModifiedPreviouslyReadFile_AppendsStaleReadHint()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "tracked.txt");
        File.WriteAllText(path, "before");
        var fileReads = new FileReadState();
        fileReads.RecordRead(path, completeView: true);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo after > tracked.txt"}""", fileReads: fileReads),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains(
            "[This command modified 1 file you've previously read: tracked.txt. Call Read before editing.]",
            result.SingleText());
    }

    [Fact]
    public async Task UnchangedReadFiles_DoNotProduceHint()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "tracked.txt");
        File.WriteAllText(path, "same");
        var fileReads = new FileReadState();
        fileReads.RecordRead(path, completeView: true);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "cat tracked.txt"}""", fileReads: fileReads),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.DoesNotContain("previously read", result.SingleText());
    }

    [Fact]
    public async Task RunInBackground_StartsJob_AndStreamsOutputToFile()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo bg-output-line", "run_in_background": true}""", jobs: jobs),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.StartsWith("Command running in background with ID: ", text);
        Assert.Contains("Output is being written to: ", text);
        Assert.Contains("You will be notified when it completes.", text);
        Assert.Contains("To check interim output, use Read on that file path.", text);

        var job = Assert.Single(jobs.List());
        Assert.Equal(BackgroundJobStatus.Running, job.Status);
        await WaitForJobCompletionAsync(jobs, job.Id);
        Assert.Equal(BackgroundJobStatus.Completed, jobs.Find(job.Id)!.Status);
        Assert.Equal(0, jobs.Find(job.Id)!.ExitCode);
        Assert.Contains("bg-output-line", await File.ReadAllTextAsync(jobs.Find(job.Id)!.OutputPath!));
    }

    [Fact]
    public async Task Timeout_OnEligibleCommand_ConvertsToBackground()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new BashTool();
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor(
                """{"command": "ping -n 20 127.0.0.1", "timeout": 1000}""",
                jobs: jobs),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.False(result.IsError);
        Assert.StartsWith("Command exceeded the blocking budget (1s) and was moved to the background with ID: ", result.SingleText());
        var job = Assert.Single(jobs.List());
        Assert.True(File.Exists(job.OutputPath));

        jobs.Stop(job.Id);
        await WaitForJobCompletionAsync(jobs, job.Id);
        Assert.Equal(BackgroundJobStatus.Stopped, jobs.Find(job.Id)!.Status);
    }

    [Fact]
    public async Task Timeout_OnSleepCommand_IsNotConvertedToBackground()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "sleep 5", "timeout": 500}""", jobs: jobs),
            CancellationToken.None);

        Assert.Contains("<error>Command was aborted before completion</error>", result.SingleText());
        Assert.Empty(jobs.List());
    }

    [Theory]
    [InlineData("ping -n 20 127.0.0.1", true)]
    [InlineData("sleep 5", false)]
    [InlineData("  sleep 5", false)]
    [InlineData("sleep", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsAutoBackgroundEligible_ExcludesSleep(string command, bool expected)
    {
        Assert.Equal(expected, BashTool.IsAutoBackgroundEligible(command));
    }

    internal static bool PosixShellAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        var spec = ShellResolver.Resolve();
        return Path.GetFileNameWithoutExtension(spec.FileName) is "bash" or "sh" or "zsh" or "dash";
    }

    private static async Task WaitForJobCompletionAsync(BackgroundJobService jobs, BackgroundJobId id)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (jobs.Find(id) is not { } job || job.Status == BackgroundJobStatus.Running)
        {
            if (deadline.IsCompleted)
                throw new TimeoutException("background job never completed");
            await Task.Delay(20);
        }
    }

    private static JsonElement Json(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
}

internal static class BashToolTestContext
{
    public static ToolExecutionContext ContextFor(
        this TempWorkspace workspace,
        string json,
        ShellSessionState? shell = null,
        FileReadState? fileReads = null,
        BackgroundJobService? jobs = null) =>
        new(
            new SessionId("sess_test"),
            workspace.Root,
            System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(),
            workspace.Workspace,
            TimeProvider.System,
            FileReads: fileReads,
            Shell: shell,
            BackgroundJobs: jobs);
}
